use crate::shell_files::{decode_path, on_window_thread, ItemIdList};
use std::{
    mem::{size_of, ManuallyDrop},
    path::PathBuf,
    ptr,
};
use tauri::WebviewWindow;
use windows::{
    core::{Result as WinResult, PCSTR},
    Win32::{
        Foundation::HWND,
        UI::{
            Shell::{
                Common::ITEMIDLIST, IContextMenu, SHCreateDefaultContextMenu, SHGetDesktopFolder,
                CMF_NORMAL, CMINVOKECOMMANDINFO, DEFCONTEXTMENU, SEE_MASK_NOASYNC,
            },
            WindowsAndMessaging::{CreatePopupMenu, DestroyMenu, SW_SHOWNORMAL},
        },
    },
};

/// 将整组选区交给 Shell 内置的文件剪切或复制命令。
pub(crate) async fn copy_files(
    window: WebviewWindow,
    keys: Vec<String>,
    cut: bool,
) -> Result<(), String> {
    if keys.is_empty() {
        return Ok(());
    }
    let paths = keys
        .iter()
        .map(|key| decode_path(key))
        .collect::<Result<Vec<_>, _>>()?;
    let error_key = if cut {
        "native.clipboard.cutFailed"
    } else {
        "native.clipboard.copyFailed"
    };
    on_window_thread(window, error_key, move |hwnd| copy_paths(hwnd, &paths, cut)).await
}

fn copy_paths(hwnd: HWND, paths: &[PathBuf], cut: bool) -> WinResult<()> {
    let items = ItemIdList::from_paths(paths)?;
    let mut ids: Vec<_> = items.iter().map(|item| item.as_ptr().cast_mut()).collect();
    // 用桌面作为父目录。绝对 PIDL 在桌面下可表示来自多个父目录的选区。
    let mut root = ITEMIDLIST::default();
    let mut selection = DEFCONTEXTMENU {
        hwnd,
        pidlFolder: ptr::from_mut(&mut root),
        psf: ManuallyDrop::new(Some(unsafe { SHGetDesktopFolder()? })),
        cidl: ids.len() as u32,
        apidl: ids.as_mut_ptr(),
        ..Default::default()
    };
    let context = unsafe { SHCreateDefaultContextMenu::<IContextMenu>(&selection) };
    unsafe { ManuallyDrop::drop(&mut selection.psf) };
    let context = context?;
    unsafe {
        let menu = CreatePopupMenu()?;
        let verb = if cut {
            windows::core::s!("cut")
        } else {
            windows::core::s!("copy")
        };
        let result = context
            .QueryContextMenu(menu, 0, 1, 0x7fff, CMF_NORMAL)
            .ok()
            .and_then(|()| {
                context.InvokeCommand(&CMINVOKECOMMANDINFO {
                    cbSize: size_of::<CMINVOKECOMMANDINFO>() as u32,
                    fMask: SEE_MASK_NOASYNC,
                    hwnd,
                    lpVerb: PCSTR(verb.as_ptr()),
                    nShow: SW_SHOWNORMAL.0,
                    ..Default::default()
                })
            });
        let _ = DestroyMenu(menu);
        result
    }
}
