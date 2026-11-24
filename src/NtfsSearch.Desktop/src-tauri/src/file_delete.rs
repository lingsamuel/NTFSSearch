use crate::{
    localization::{error as localized_error, text},
    shell_files::{decode_path, delete_items, ItemIdList, OleApartment},
};
use std::path::PathBuf;
use tauri::{async_runtime::channel, WebviewWindow};
use windows::{
    core::HRESULT,
    Win32::{
        Foundation::{ERROR_CANCELLED, HWND},
        UI::Shell::{
            COPYENGINE_E_USER_CANCELLED, FOFX_ADDUNDORECORD, FOFX_RECYCLEONDELETE,
            FOF_WANTNUKEWARNING,
        },
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
    let (completion, mut result) = channel(1);
    let owner = window.clone();
    window
        .run_on_main_thread(move || {
            let response = owner
                .hwnd()
                .map_err(|error| localized_error("native.delete.failed", error))
                .and_then(|hwnd| delete_paths(hwnd, &paths, permanent));
            let _ = completion.try_send(response);
        })
        .map_err(|error| localized_error("native.delete.failed", error))?;
    result
        .recv()
        .await
        .ok_or_else(|| text("native.delete.unavailable").to_owned())?
}

fn delete_paths(hwnd: HWND, paths: &[PathBuf], permanent: bool) -> Result<bool, String> {
    let _ole =
        OleApartment::enter().map_err(|error| localized_error("native.delete.failed", error))?;
    let ids = ItemIdList::from_paths(paths)
        .map_err(|error| localized_error("native.delete.failed", error))?;
    let pointers: Vec<_> = ids.iter().map(ItemIdList::as_ptr).collect();
    // 普通删除由 Shell 按回收站策略处理。凡是永久删除，都由系统显示警告。
    let mut flags = FOF_WANTNUKEWARNING;
    if !permanent {
        flags |= FOFX_RECYCLEONDELETE | FOFX_ADDUNDORECORD;
    }
    match delete_items(hwnd, &pointers, flags) {
        Ok(aborted) => Ok(!aborted),
        Err(error)
            if error.code() == COPYENGINE_E_USER_CANCELLED
                || error.code() == HRESULT::from_win32(ERROR_CANCELLED.0) =>
        {
            Ok(false)
        }
        Err(error) => Err(localized_error("native.delete.failed", error)),
    }
}
