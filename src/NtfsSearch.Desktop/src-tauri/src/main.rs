#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod backend;

use backend::Backend;
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::{env, fmt::Write, fs, io, os::windows::ffi::OsStrExt};
use tauri::{
    menu::{Menu, MenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    App, AppHandle, Manager, State, WebviewWindow, WindowEvent,
};

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

// 同目录的重复启动复用原窗口，其他便携目录拥有各自的单实例标识。
fn portable_instance_id(identifier: &str) -> io::Result<String> {
    let executable = env::current_exe()?;
    let directory = fs::canonicalize(executable.parent().expect("应用程序路径缺少便携目录"))?;
    let bytes: Vec<_> = directory
        .as_os_str()
        .encode_wide()
        .flat_map(u16::to_le_bytes)
        .collect();
    let mut id = format!("{identifier}.");
    for byte in Sha256::digest(bytes) {
        let _ = write!(id, "{byte:02x}");
    }
    Ok(id)
}

fn show_window(window: &WebviewWindow) {
    let _ = window.show();
    if window.is_minimized().unwrap_or(false) {
        let _ = window.unminimize();
    }
    let _ = window.set_focus();
}

fn open_main_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        show_window(&window);
    }
}

fn quit_from_tray(app: &AppHandle) {
    match app.state::<Backend>().request_quit() {
        Ok(true) => {}
        Ok(false) => app.exit(0),
        Err(error) => {
            if let Some(window) = app.get_webview_window("main") {
                backend::show_quit_error(&window, format!("无法请求退出保存：{error}"));
            }
        }
    }
}

fn setup_tray(app: &mut App) -> Result<(), Box<dyn std::error::Error>> {
    let open = MenuItem::with_id(app, "open", "打开", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "退出", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&open, &quit])?;
    let tray = TrayIconBuilder::new()
        .menu(&menu)
        .tooltip("NTFS Search")
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id().as_ref() {
            "open" => open_main_window(app),
            "quit" => quit_from_tray(app),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } = event
            {
                open_main_window(tray.app_handle());
            }
        });
    let icon = app.default_window_icon().expect("桌面程序缺少图标").clone();
    tray.icon(icon).build(app)?;
    Ok(())
}

fn main() {
    if let Some(code) = backend::run_diagnostic() {
        std::process::exit(code);
    }
    let mut context = tauri::generate_context!();
    context.config_mut().identifier =
        portable_instance_id(&context.config().identifier).expect("无法确定便携版所在目录");
    let application = tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _, _| {
            open_main_window(app)
        }))
        .manage(Backend::default())
        .setup(setup_tray)
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                let _ = window.hide();
            }
        })
        .invoke_handler(tauri::generate_handler![start_backend, send_request])
        .build(context)
        .expect("无法启动 NTFS Search 桌面窗口");

    application.run(|app, event| {
        if let tauri::RunEvent::Exit = event {
            app.state::<Backend>().stop();
        }
    });
}
