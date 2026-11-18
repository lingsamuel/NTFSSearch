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
use tauri::{Emitter, WebviewWindow};

const EVENT: &str = "backend-message";

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
        let mode = match env::args().nth(1).as_deref() {
            Some("--desktop-test") => "--desktop-test",
            Some("--desktop-benchmark") => "--desktop-benchmark",
            _ => "--desktop",
        };
        let mut child = command
            .arg(mode)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|error| format!("无法启动搜索后端（NtfsSearch.Backend.exe）：{error}"))?;
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
        let reader_stopping = stopping.clone();
        let reader_failed = failed.clone();
        thread::spawn(move || {
            let mut reported_fatal = false;
            for line in BufReader::new(output).lines() {
                if reader_stopping.load(Ordering::Acquire) {
                    return;
                }
                match line.map_err(|error| error.to_string()).and_then(|line| {
                    serde_json::from_str::<Value>(&line).map_err(|error| error.to_string())
                }) {
                    Ok(message) => {
                        if message["scope"] == "fatal" {
                            reported_fatal = true;
                            reader_failed.store(true, Ordering::Release);
                        }
                        let _ = window.emit(EVENT, message);
                    }
                    Err(error) => {
                        reader_failed.store(true, Ordering::Release);
                        let _ = window.emit(
                            EVENT,
                            json!({
                                "type": "error", "scope": "fatal",
                                "message": format!("无法读取搜索后端响应：{error}")
                            }),
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
                let message = if reason.is_empty() {
                    "搜索后端已退出。".to_owned()
                } else {
                    format!("无法运行搜索后端。\n{reason}")
                };
                let _ = window.emit(
                    EVENT,
                    json!({ "type": "error", "scope": "fatal", "message": message }),
                );
            }
        });
        *process = Some(Process {
            child,
            input,
            stopping,
            failed,
        });
        Ok(())
    }

    pub fn send(&self, request: Value) -> Result<(), String> {
        let mut process = self.0.lock().map_err(|error| error.to_string())?;
        let process = process.as_mut().ok_or("搜索后端尚未启动。")?;
        let mut message = serde_json::to_vec(&request).map_err(|error| error.to_string())?;
        message.push(b'\n');
        process
            .input
            .write_all(&message)
            .map_err(|error| error.to_string())
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
