use crate::shell_files::{
    decode_path, delete_items, on_window_thread, operation_completed, ItemIdList,
};
use std::path::PathBuf;
use tauri::WebviewWindow;
use windows::{
    core::Result as WinResult,
    Win32::{
        Foundation::HWND,
        UI::Shell::{FOFX_ADDUNDORECORD, FOFX_RECYCLEONDELETE, FOF_WANTNUKEWARNING},
    },
};

/// 等 WebView 退出 IPC 回调后，在窗口的 STA 中打开 Windows 删除对话框。
pub(crate) async fn delete_files(
    window: WebviewWindow,
    keys: Vec<String>,
    permanent: bool,
) -> Result<bool, String> {
    if keys.is_empty() {
        return Ok(false);
    }
    let paths = keys
        .iter()
        .map(|key| decode_path(key))
        .collect::<Result<Vec<_>, _>>()?;
    on_window_thread(window, "native.delete.failed", move |hwnd| {
        delete_paths(hwnd, &paths, permanent)
    })
    .await
}

fn delete_paths(hwnd: HWND, paths: &[PathBuf], permanent: bool) -> WinResult<bool> {
    let ids = ItemIdList::from_paths(paths)?;
    let pointers: Vec<_> = ids.iter().map(ItemIdList::as_ptr).collect();
    // 普通删除由 Shell 按回收站策略处理。凡是永久删除，都由系统显示警告。
    let mut flags = FOF_WANTNUKEWARNING;
    if !permanent {
        flags |= FOFX_RECYCLEONDELETE | FOFX_ADDUNDORECORD;
    }
    operation_completed(delete_items(hwnd, &pointers, flags))
}
