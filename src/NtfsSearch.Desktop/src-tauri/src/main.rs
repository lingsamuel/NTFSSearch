#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod backend;
mod file_delete;
mod file_drag;
mod localization;
mod shell_files;

use backend::Backend;
use localization::{error as localized_error, text};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::{env, fmt::Write, fs, io, os::windows::ffi::OsStrExt, sync::Mutex};
use tauri::{
    menu::{Menu, MenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    App, AppHandle, Emitter, Manager, State, WebviewWindow, WindowEvent,
};
use windows::Win32::{
    Foundation::{HWND, LPARAM, LRESULT, WPARAM},
    UI::{
        Shell::{DefSubclassProc, RemoveWindowSubclass, SetWindowSubclass},
        WindowsAndMessaging::{WA_INACTIVE, WM_ACTIVATE, WM_NCDESTROY},
    },
};

enum SettingsNavigation {
    Loading,
    Pending,
    Ready,
}

struct WindowNavigation {
    settings: SettingsNavigation,
    activate_settings: bool,
}

const TRAY_ITEMS: &[(&str, &str)] = &[
    ("open", "native.tray.open"),
    ("settings", "native.tray.settings"),
    ("quit", "native.tray.quit"),
];

struct TrayMenu(Vec<MenuItem<tauri::Wry>>);

fn desktop_mode() -> &'static str {
    match env::args().nth(1).as_deref() {
        Some("--desktop-test") => "--desktop-test",
        Some("--desktop-benchmark") => "--desktop-benchmark",
        _ => "--desktop",
    }
}

/// 供 WebView 首次渲染和 Windows 窗口测试读取桌面壳当前生效的语言。
#[tauri::command]
fn desktop_language() -> &'static str {
    localization::language()
}

#[tauri::command]
fn start_backend(
    window: WebviewWindow,
    backend: State<'_, Backend>,
    navigation: State<'_, Mutex<WindowNavigation>>,
) -> Result<(), String> {
    let pending = {
        let mut state = navigation
            .lock()
            .map_err(|error| localized_error("native.backend.start", error))?;
        matches!(
            std::mem::replace(&mut state.settings, SettingsNavigation::Ready),
            SettingsNavigation::Pending
        )
    };
    if pending {
        let _ = window.emit("open-settings", ());
    }
    backend
        .start(window)
        .map_err(|error| localized_error("native.backend.start", error))
}

#[tauri::command]
async fn drag_files(window: WebviewWindow, keys: Vec<String>) -> Result<(), String> {
    file_drag::drag_files(window, keys).await
}

#[tauri::command]
async fn delete_files(
    window: WebviewWindow,
    keys: Vec<String>,
    permanent: bool,
) -> Result<bool, String> {
    file_delete::delete_files(window, keys, permanent).await
}

#[tauri::command]
fn send_request(
    window: WebviewWindow,
    backend: State<'_, Backend>,
    mut request: Value,
) -> Result<(), String> {
    if request["type"] == "copy" {
        let hwnd = window
            .hwnd()
            .map_err(|error| localized_error("native.window.copy", error))?;
        request["windowHandle"] = Value::from(hwnd.0 as isize as i64);
    }
    backend.send(request)
}

// 同目录的重复启动复用原窗口，其他便携目录拥有各自的单实例标识。
fn portable_instance_id(identifier: &str) -> io::Result<String> {
    let executable = env::current_exe()?;
    let directory = fs::canonicalize(
        executable
            .parent()
            .expect(text("native.portable.missingDirectory")),
    )?;
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

fn open_main_window(app: &AppHandle, settings: bool) {
    if let Some(window) = app.get_webview_window("main") {
        let navigation = app.state::<Mutex<WindowNavigation>>();
        let open_settings = {
            let Ok(mut state) = navigation.lock() else {
                return;
            };
            state.activate_settings = settings;
            let ready = matches!(state.settings, SettingsNavigation::Ready);
            // 页面先注册事件再启动后端。冷启动时暂存从托盘打开设置的请求。
            if settings && !ready {
                state.settings = SettingsNavigation::Pending;
            } else if !settings && matches!(state.settings, SettingsNavigation::Pending) {
                state.settings = SettingsNavigation::Loading;
            }
            settings && ready
        };
        show_window(&window);
        // 显式设置入口只覆盖这次恢复窗口，拒绝激活时也释放标记。
        if settings {
            if let Ok(mut state) = navigation.lock() {
                state.activate_settings = false;
            }
        }
        if open_settings {
            let _ = window.emit("open-settings", ());
        }
    }
}

// 单 WebView 模式下 Tauri 的 Focused 来自 WebView2。主窗口激活消息只反映顶层切换。
unsafe extern "system" fn search_window_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
    subclass: usize,
    reference: usize,
) -> LRESULT {
    let app = reference as *mut AppHandle;
    if message == WM_NCDESTROY {
        let _ = unsafe { RemoveWindowSubclass(hwnd, Some(search_window_proc), subclass) };
        drop(unsafe { Box::from_raw(app) });
    } else if message == WM_ACTIVATE && (wparam.0 & 0xffff) != WA_INACTIVE as usize && wparam.0 >> 16 == 0 {
        let app = unsafe { &*app };
        let navigation = app.state::<Mutex<WindowNavigation>>();
        let settings = navigation.lock().is_ok_and(|mut state| {
            if state.activate_settings {
                return true;
            }
            if matches!(state.settings, SettingsNavigation::Pending) {
                state.settings = SettingsNavigation::Loading;
            }
            false
        });
        if !settings {
            let _ = app.emit("activate-search", ());
        }
    }
    unsafe { DefSubclassProc(hwnd, message, wparam, lparam) }
}

fn install_search_activation(app: &App) -> Result<(), Box<dyn std::error::Error>> {
    let window = app
        .get_webview_window("main")
        .expect(text("native.window.missing"));
    let hwnd = window.hwnd()?;
    let handle = Box::into_raw(Box::new(app.handle().clone()));
    if !unsafe { SetWindowSubclass(hwnd, Some(search_window_proc), 1, handle as usize) }.as_bool() {
        drop(unsafe { Box::from_raw(handle) });
        return Err(io::Error::other(text("native.window.activation")).into());
    }
    Ok(())
}

fn quit_from_tray(app: &AppHandle) {
    match app.state::<Backend>().request_quit() {
        Ok(true) => {}
        Ok(false) => app.exit(0),
        Err(error) => {
            if let Some(window) = app.get_webview_window("main") {
                backend::show_quit_error(&window, localized_error("native.backend.quit", error));
            }
        }
    }
}

fn setup_tray(app: &mut App) -> Result<(), Box<dyn std::error::Error>> {
    let menu = Menu::new(app)?;
    let mut items = Vec::with_capacity(TRAY_ITEMS.len());
    for &(id, key) in TRAY_ITEMS {
        let item = MenuItem::with_id(app, id, text(key), true, None::<&str>)?;
        menu.append(&item)?;
        items.push(item);
    }
    let tray = TrayIconBuilder::new()
        .menu(&menu)
        .tooltip("NTFS Search")
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id().as_ref() {
            "open" => open_main_window(app, false),
            "settings" => open_main_window(app, true),
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
                open_main_window(tray.app_handle(), false);
            }
        });
    let icon = app
        .default_window_icon()
        .expect(text("native.window.missingIcon"))
        .clone();
    tray.icon(icon).build(app)?;
    app.manage(TrayMenu(items));
    Ok(())
}

fn update_tray_language(window: &WebviewWindow, language: &str) {
    if !localization::set_language(language) {
        return;
    }
    let app = window.app_handle().clone();
    // 复用现有菜单项并在主线程一起更新，后端读取线程可继续接收响应。
    let _ = window.run_on_main_thread(move || {
        let tray = app.state::<TrayMenu>();
        for (item, &(_, key)) in tray.0.iter().zip(TRAY_ITEMS) {
            let _ = item.set_text(text(key));
        }
    });
}

fn main() {
    if let Some(code) = backend::run_diagnostic() {
        std::process::exit(code);
    }
    let mut context = tauri::generate_context!();
    context.config_mut().identifier = portable_instance_id(&context.config().identifier)
        .expect(text("native.portable.directory"));
    let application = tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _, _| {
            open_main_window(app, false)
        }))
        .manage(Backend::default())
        .manage(Mutex::new(WindowNavigation {
            settings: SettingsNavigation::Loading,
            activate_settings: false,
        }))
        .setup(|app| {
            install_search_activation(app)?;
            setup_tray(app)
        })
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                let _ = window.hide();
            }
        })
        .invoke_handler(tauri::generate_handler![
            desktop_language,
            start_backend,
            send_request,
            drag_files,
            delete_files
        ])
        .build(context)
        .expect(text("native.window.start"));

    application.run(|app, event| {
        if let tauri::RunEvent::Exit = event {
            app.state::<Backend>().stop();
        }
    });
}
