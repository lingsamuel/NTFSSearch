use crate::{
    localization::text,
    shell_files::{
        decode_path, on_window_thread, operation_completed, perform_file_operation, ItemIdList,
    },
};
use tauri::WebviewWindow;
use windows::{
    core::PCWSTR,
    Win32::UI::Shell::{IShellItem, SHCreateItemFromIDList, FOFX_ADDUNDORECORD},
};

/// UI 提供单个新文件名，Windows 负责改名及相关确认。
pub(crate) async fn rename_file(
    window: WebviewWindow,
    key: &str,
    name: &str,
) -> Result<bool, String> {
    let path = decode_path(key)?;
    if name.is_empty() || name.contains(['\0', '/', '\\']) {
        return Err(text("native.rename.fileNameRequired").into());
    }
    let name: Vec<_> = name.encode_utf16().chain([0]).collect();
    on_window_thread(window, "native.rename.failed", move |hwnd| {
        let id = ItemIdList::from_path(&path)?;
        let item: IShellItem = unsafe { SHCreateItemFromIDList(id.as_ptr())? };
        operation_completed(perform_file_operation(
            hwnd,
            FOFX_ADDUNDORECORD,
            |operation| unsafe { operation.RenameItem(&item, PCWSTR(name.as_ptr()), None) },
        ))
    })
    .await
}
