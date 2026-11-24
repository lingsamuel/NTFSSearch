use crate::localization::{error as localized_error, system_language, text};
use serde_json::{json, Value};
use std::{
    env,
    io::{self, BufRead, BufReader, Read, Write},
    os::windows::process::CommandExt,
    process::{Child, ChildStdin, Command, Stdio},
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
    thread,
    time::{Duration, Instant},
};
use tauri::{Emitter, Manager, WebviewWindow};

const EVENT: &str = "backend-message";

pub fn show_quit_error(window: &WebviewWindow, message: String) {
    crate::show_window(window);
    let _ = window.emit(EVENT, json!({ "type": "quit-complete", "error": message }));
}

fn report_failure(window: &WebviewWindow, quitting: bool, message: String) {
    if quitting {
        show_quit_error(window, message);
    } else {
        let _ = window.emit(
            EVENT,
            json!({ "type": "error", "scope": "fatal", "message": message }),
        );
    }
}

fn backend_command() -> io::Result<Command> {
    let executable = env::current_exe()?.with_file_name("NtfsSearch.Backend.exe");
    let mut command = Command::new(executable);
    command
        .env("DOTNET_DISABLE_GUI_ERRORS", "1")
        .creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    Ok(command)
}

// 保留已发布版本的命令行诊断入口，报告仍由同一 .NET 实现写入。
pub fn run_diagnostic() -> Option<i32> {
    let args: Vec<_> = env::args_os().skip(1).collect();
    if args.len() != 2
        || !matches!(
            args[0].to_str(),
            Some(
                "--smoke-test"
                    | "--probe-volumes"
                    | "--automatic-test"
                    | "--parallel-test"
                    | "--verify-live"
                    | "--full-audit"
            )
        )
    {
        return None;
    }
    Some(
        backend_command()
            .and_then(|mut command| command.args(args).status())
            .ok()
            .and_then(|status| status.code())
            .unwrap_or(1),
    )
}

#[derive(Default)]
pub struct Backend(Mutex<Option<Process>>);

struct Process {
    child: Child,
    input: ChildStdin,
    stopping: Arc<AtomicBool>,
    failed: Arc<AtomicBool>,
    quitting: Arc<AtomicBool>,
}

impl Backend {
    pub fn start(&self, window: WebviewWindow) -> Result<(), String> {
        let mut process = self.0.lock().map_err(|error| error.to_string())?;
        if let Some(existing) = process.as_mut() {
            let running = existing
                .child
                .try_wait()
                .map_err(|error| error.to_string())?
                .is_none();
            if running && !existing.failed.load(Ordering::Acquire) {
                return Ok(());
            }
            if let Some(previous) = process.take() {
                previous.stop();
            }
        }

        let mut command = backend_command().map_err(|error| error.to_string())?;
        command.env("NTFS_SEARCH_UI_LANGUAGE", system_language());
        let mut child = command
            .arg(crate::desktop_mode())
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|error| error.to_string())?;
        let input = child.stdin.take().expect("stdin configured as pipe");
        let output = child.stdout.take().expect("stdout configured as pipe");
        let error_output = child.stderr.take().expect("stderr configured as pipe");
        let diagnostics = thread::spawn(move || {
            let mut bytes = Vec::new();
            let _ = error_output.take(8192).read_to_end(&mut bytes);
            String::from_utf8_lossy(&bytes).trim().to_owned()
        });
        let stopping = Arc::new(AtomicBool::new(false));
        let failed = Arc::new(AtomicBool::new(false));
        let quitting = Arc::new(AtomicBool::new(false));
        let reader_quitting = quitting.clone();
        let reader_stopping = stopping.clone();
        let reader_failed = failed.clone();
        thread::spawn(move || {
            let mut reported_fatal = false;
            let mut saving = false;
            for line in BufReader::new(output).lines() {
                if reader_stopping.load(Ordering::Acquire) {
                    return;
                }
                match line.map_err(|error| error.to_string()).and_then(|line| {
                    serde_json::from_str::<Value>(&line).map_err(|error| error.to_string())
                }) {
                    Ok(message) => {
                        if message["type"] == "settingsStatus" {
                            if let Some(language) = message["effectiveLanguage"].as_str() {
                                crate::update_tray_language(&window, language);
                            }
                        }
                        if message["type"] == "quit-complete" {
                            reader_failed.store(true, Ordering::Release);
                            if let Some(error) = message["error"].as_str() {
                                show_quit_error(&window, error.to_owned());
                            } else {
                                window.app_handle().exit(0);
                            }
                            return;
                        }
                        let show_saving = message["type"] == "saving" && !saving;
                        if message["scope"] == "fatal" {
                            reader_failed.store(true, Ordering::Release);
                            if reader_quitting.load(Ordering::Acquire) {
                                let error = message["message"]
                                    .as_str()
                                    .unwrap_or_else(|| text("native.backend.failed"));
                                show_quit_error(&window, error.to_owned());
                                return;
                            }
                            reported_fatal = true;
                        }
                        let _ = window.emit(EVENT, message);
                        if show_saving {
                            saving = true;
                            // 先显示保存状态，再激活托盘隐去的窗口，避免焦点切到搜索框。
                            crate::show_window(&window);
                        }
                    }
                    Err(error) => {
                        reader_failed.store(true, Ordering::Release);
                        report_failure(
                            &window,
                            reader_quitting.load(Ordering::Acquire),
                            localized_error("native.backend.read", error),
                        );
                        return;
                    }
                }
            }
            if !reported_fatal && !reader_stopping.load(Ordering::Acquire) {
                let reason = diagnostics.join().unwrap_or_default();
                if reader_stopping.load(Ordering::Acquire) {
                    return;
                }
                reader_failed.store(true, Ordering::Release);
                let quitting = reader_quitting.load(Ordering::Acquire);
                let message = if quitting {
                    text("native.backend.stoppedBeforeSave").to_owned()
                } else if reason.is_empty() {
                    text("native.backend.stopped").to_owned()
                } else {
                    localized_error("native.backend.run", reason)
                };
                report_failure(&window, quitting, message);
            }
        });
        *process = Some(Process {
            child,
            input,
            stopping,
            failed,
            quitting,
        });
        Ok(())
    }

    pub fn send(&self, request: Value) -> Result<(), String> {
        let mut process = self
            .0
            .lock()
            .map_err(|error| localized_error("native.backend.send", error))?;
        let process = process
            .as_mut()
            .ok_or_else(|| text("native.backend.notStarted"))?;
        let mut message = serde_json::to_vec(&request)
            .map_err(|error| localized_error("native.backend.send", error))?;
        message.push(b'\n');
        process
            .input
            .write_all(&message)
            .map_err(|error| localized_error("native.backend.send", error))
    }

    /// 向仍在运行的后端请求正常关闭。收到确认前保持窗口与通信管道打开。
    pub fn request_quit(&self) -> Result<bool, String> {
        let mut process = self.0.lock().map_err(|error| error.to_string())?;
        let Some(process) = process.as_mut() else {
            return Ok(false);
        };
        if process.failed.load(Ordering::Acquire)
            || process
                .child
                .try_wait()
                .map_err(|error| error.to_string())?
                .is_some()
        {
            return Ok(false);
        }
        if !process.quitting.swap(true, Ordering::AcqRel) {
            if let Err(error) = process.input.write_all(b"{\"type\":\"quit\"}\n") {
                process.failed.store(true, Ordering::Release);
                return Err(error.to_string());
            }
        }
        Ok(true)
    }

    pub fn stop(&self) {
        let Ok(mut process) = self.0.lock() else {
            return;
        };
        if let Some(child) = process.take() {
            child.stop();
        }
    }
}

impl Process {
    fn stop(self) {
        let Process {
            mut child,
            input,
            stopping,
            ..
        } = self;
        stopping.store(true, Ordering::Release);
        // 关闭输入管道让后端取消任务并退出。管理员采集进程沿用已有的父进程监控。
        drop(input);
        let deadline = Instant::now() + Duration::from_secs(2);
        while Instant::now() < deadline {
            if matches!(child.try_wait(), Ok(Some(_))) {
                return;
            }
            thread::sleep(Duration::from_millis(25));
        }
        let _ = child.kill();
        let _ = child.wait();
    }
}
