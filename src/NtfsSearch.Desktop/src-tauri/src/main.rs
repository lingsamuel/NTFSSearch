#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod backend;

use backend::Backend;
use serde_json::{json, Value};
use tauri::{Emitter, Manager, State, WebviewWindow, WindowEvent};

#[tauri::command]
fn start_backend(window: WebviewWindow, backend: State<'_, Backend>) -> Result<(), String> {
    backend.start(window)
}

#[tauri::command]
fn send_request(
    window: WebviewWindow,
    backend: State<'_, Backend>,
    mut request: Value,
) -> Result<(), String> {
    if request["type"] == "copy" {
        let hwnd = window.hwnd().map_err(|error| error.to_string())?;
        request["windowHandle"] = Value::from(hwnd.0 as isize as i64);
    }
    backend.send(request)
}

fn main() {
    if let Some(code) = backend::run_diagnostic() {
        std::process::exit(code);
    }
    let application = tauri::Builder::default()
        .manage(Backend::default())
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                match window.state::<Backend>().request_quit() {
                    Ok(true) => api.prevent_close(),
                    Ok(false) => {}
                    Err(error) => {
                        api.prevent_close();
                        let _ = window.emit(
                            "backend-message",
                            json!({
                                "type": "quit-complete",
                                "error": format!("无法请求退出保存：{error}")
                            }),
                        );
                    }
                }
            }
        })
        .invoke_handler(tauri::generate_handler![start_backend, send_request])
        .build(tauri::generate_context!())
        .expect("无法启动 NTFS Search 桌面窗口");

    application.run(|app, event| {
        if let tauri::RunEvent::Exit = event {
            app.state::<Backend>().stop();
        }
    });
}
