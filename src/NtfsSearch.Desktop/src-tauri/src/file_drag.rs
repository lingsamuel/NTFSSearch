use crate::{
    localization::{error as localized_error, format as localized_format, text},
    shell_files::{decode_path, delete_items, ItemIdList, OleApartment},
};
use std::{
    cell::Cell,
    fs,
    mem::size_of,
    os::windows::{ffi::OsStrExt, fs::OpenOptionsExt, io::AsRawHandle},
    path::{Path, PathBuf},
    ptr, slice,
};
use tauri::{async_runtime::channel, WebviewWindow};
use windows::{
    core::{Error, BOOL},
    Win32::{
        Foundation::{GlobalFree, HANDLE, HWND},
        Storage::FileSystem::{
            FileIdInfo, GetFileInformationByHandleEx, FILE_FLAG_BACKUP_SEMANTICS,
            FILE_FLAG_OPEN_REPARSE_POINT, FILE_ID_INFO,
        },
        System::{
            Com::{
                IDataObject, DVASPECT_CONTENT, FORMATETC, STGMEDIUM, STGMEDIUM_0, TYMED_HGLOBAL,
            },
            DataExchange::RegisterClipboardFormatW,
            Memory::{GlobalAlloc, GlobalLock, GlobalSize, GlobalUnlock, GMEM_MOVEABLE},
            Ole::{ReleaseStgMedium, CF_HDROP, DROPEFFECT, DROPEFFECT_COPY, DROPEFFECT_MOVE},
        },
        UI::{
            Input::KeyboardAndMouse::{GetAsyncKeyState, VK_LBUTTON, VK_RBUTTON},
            Shell::{
                Common::ITEMIDLIST, SHCreateDataObject, SHDoDragDrop, CFSTR_PERFORMEDDROPEFFECT,
                DROPFILES, FOFX_RECYCLEONDELETE, FOF_NO_CONNECTED_ELEMENTS,
            },
            WindowsAndMessaging::{GetSystemMetrics, SM_SWAPBUTTON},
        },
    },
};

thread_local! {
    static DRAG_ACTIVE: Cell<bool> = const { Cell::new(false) };
}

/// 异步命令从工作线程投递到窗口线程，让 WebView2 先退出 IPC 回调再进入 OLE 消息循环。
pub(crate) async fn drag_files(window: WebviewWindow, keys: Vec<String>) -> Result<(), String> {
    if keys.is_empty() {
        return Ok(());
    }
    let paths = keys
        .iter()
        .map(|key| decode_path(key))
        .collect::<Result<Vec<_>, _>>()?;
    let identities = paths
        .iter()
        .map(|path| {
            let identity = file_identity(path);
            if identity.is_none() {
                fs::symlink_metadata(path).map_err(|error| {
                    localized_format(
                        "native.drag.path",
                        &[
                            ("path", &path.to_string_lossy()),
                            ("error", &error.to_string()),
                        ],
                    )
                })?;
            }
            Ok(identity)
        })
        .collect::<Result<Vec<_>, String>>()?;

    let (completion, mut result) = channel(1);
    let source = window.clone();
    window
        .run_on_main_thread(move || {
            let response = DRAG_ACTIVE.with(|active| {
                if active.replace(true) {
                    return Ok(());
                }
                let response = start_drag(source, &paths, &identities);
                active.set(false);
                response
            });
            let _ = completion.try_send(response);
        })
        .map_err(|error| localized_error("native.drag.startError", error))?;
    result
        .recv()
        .await
        .ok_or_else(|| text("native.drag.start").to_owned())?
}

type FileIdentity = (u64, [u8; 16]);

fn file_identity(path: &Path) -> Option<FileIdentity> {
    let file = fs::OpenOptions::new()
        .access_mode(0)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS.0 | FILE_FLAG_OPEN_REPARSE_POINT.0)
        .open(path)
        .ok()?;
    let mut info = FILE_ID_INFO::default();
    unsafe {
        GetFileInformationByHandleEx(
            HANDLE(file.as_raw_handle()),
            FileIdInfo,
            ptr::from_mut(&mut info).cast(),
            size_of::<FILE_ID_INFO>() as u32,
        )
        .ok()?;
    }
    Some((info.VolumeSerialNumber, info.FileId.Identifier))
}

fn start_drag(
    window: WebviewWindow,
    paths: &[PathBuf],
    identities: &[Option<FileIdentity>],
) -> Result<(), String> {
    let hwnd = window
        .hwnd()
        .map_err(|error| localized_error("native.drag.startError", error))?;
    let _ole =
        OleApartment::enter().map_err(|error| localized_error("native.drag.startError", error))?;
    // Explorer 可通过桌面下的绝对 PIDL 识别不同父目录中的 Shell 条目。
    // 无法解析为 Shell 条目的路径仍可通过 CF_HDROP 供接收方读取。
    let item_ids = ItemIdList::from_paths(paths);
    let id_ptrs = item_ids
        .as_ref()
        .ok()
        .map(|items| items.iter().map(ItemIdList::as_ptr).collect::<Vec<_>>());
    let data = file_data_object(paths, id_ptrs.as_deref())
        .map_err(|error| localized_error("native.drag.startError", error))?;
    let performed = performed_effect_format()
        .map_err(|error| localized_error("native.drag.startError", error))?;

    // IPC 调度期间用户可能已松开鼠标。GetAsyncKeyState 读取的是物理按键。
    let primary = if unsafe { GetSystemMetrics(SM_SWAPBUTTON) } == 0 {
        VK_LBUTTON
    } else {
        VK_RBUTTON
    };
    if unsafe { GetAsyncKeyState(i32::from(primary.0)) } >= 0 {
        return Ok(());
    }

    // Shell 负责拖动图像、修饰键与默认效果。取消后绑定返回初始的 DROPEFFECT_NONE。
    let effect =
        unsafe { SHDoDragDrop(Some(hwnd), &data, None, DROPEFFECT_COPY | DROPEFFECT_MOVE) }
            .map_err(|error| localized_error("native.drag.startError", error))?;
    if needs_source_cleanup(&data, performed, effect) {
        // 删除前核对源条目仍是起拖时的文件，目标也不能通过修改数据对象替换删除列表。
        if paths
            .iter()
            .zip(identities)
            .any(|(path, &identity)| identity.is_none() || file_identity(path) != identity)
        {
            return Err(text("native.drag.sourceChanged").into());
        }
        let ids = id_ptrs
            .as_deref()
            .ok_or_else(|| text("native.drag.sourceItemsUnknown"))?;
        complete_move(hwnd, ids)?;
    }
    Ok(())
}

fn file_data_object(
    paths: &[PathBuf],
    ids: Option<&[*const ITEMIDLIST]>,
) -> windows::core::Result<IDataObject> {
    let desktop = ITEMIDLIST::default();
    let folder = ids.map(|_| ptr::from_ref(&desktop));
    let data = unsafe { SHCreateDataObject::<_, IDataObject>(folder, ids, None)? };
    let header = DROPFILES {
        pFiles: size_of::<DROPFILES>() as u32,
        fWide: BOOL(1),
        ..Default::default()
    };
    // DROPFILES 为 packed 结构，紧跟以双 NUL 结尾的绝对路径列表。
    let mut bytes = unsafe {
        slice::from_raw_parts(ptr::from_ref(&header).cast::<u8>(), size_of::<DROPFILES>())
    }
    .to_vec();
    for path in paths {
        bytes.extend(
            path.as_os_str()
                .encode_wide()
                .chain([0])
                .flat_map(u16::to_le_bytes),
        );
    }
    bytes.extend(0u16.to_le_bytes());
    set_global_data(&data, CF_HDROP.0, &bytes)?;
    Ok(data)
}

fn global_format(format: u16) -> FORMATETC {
    FORMATETC {
        cfFormat: format,
        dwAspect: DVASPECT_CONTENT.0,
        lindex: -1,
        tymed: TYMED_HGLOBAL.0 as u32,
        ..Default::default()
    }
}

fn set_global_data(data: &IDataObject, format: u16, bytes: &[u8]) -> windows::core::Result<()> {
    unsafe {
        let memory = GlobalAlloc(GMEM_MOVEABLE, bytes.len())?;
        let target = GlobalLock(memory).cast::<u8>();
        if target.is_null() {
            let error = Error::from_thread();
            let _ = GlobalFree(Some(memory));
            return Err(error);
        }
        ptr::copy_nonoverlapping(bytes.as_ptr(), target, bytes.len());
        let _ = GlobalUnlock(memory);
        let mut medium = STGMEDIUM {
            tymed: TYMED_HGLOBAL.0 as u32,
            u: STGMEDIUM_0 { hGlobal: memory },
            ..Default::default()
        };
        // SetData 成功后 COM 数据对象接管句柄，失败时仍由调用方释放。
        let result = data.SetData(&global_format(format), &medium, true);
        if result.is_err() {
            ReleaseStgMedium(&mut medium);
        }
        result
    }
}

fn performed_effect_format() -> windows::core::Result<u16> {
    let format = unsafe { RegisterClipboardFormatW(CFSTR_PERFORMEDDROPEFFECT) };
    if format == 0 {
        return Err(Error::from_thread());
    }
    Ok(format as u16)
}

fn needs_source_cleanup(data: &IDataObject, format: u16, returned: DROPEFFECT) -> bool {
    // 优化移动由目标完整执行。仅在返回值和 Performed DropEffect 都确认 MOVE 时清理源路径。
    if returned != DROPEFFECT_MOVE {
        return false;
    }
    unsafe {
        let Ok(mut medium) = data.GetData(&global_format(format)) else {
            return false;
        };
        let mut performed_move = false;
        if medium.tymed == TYMED_HGLOBAL.0 as u32 {
            let memory = medium.u.hGlobal;
            if GlobalSize(memory) >= size_of::<u32>() {
                let address = GlobalLock(memory).cast::<u32>();
                if !address.is_null() {
                    performed_move = address.read() == DROPEFFECT_MOVE.0;
                    let _ = GlobalUnlock(memory);
                }
            }
        }
        ReleaseStgMedium(&mut medium);
        performed_move
    }
}

fn complete_move(hwnd: HWND, id_ptrs: &[*const ITEMIDLIST]) -> Result<(), String> {
    match delete_items(
        hwnd,
        id_ptrs,
        FOFX_RECYCLEONDELETE | FOF_NO_CONNECTED_ELEMENTS,
    ) {
        Ok(false) => Ok(()),
        Ok(true) => Err(text("native.drag.sourceCleanupIncomplete").into()),
        Err(error) => Err(localized_error("native.drag.sourceCleanupError", error)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use base64::{engine::general_purpose::STANDARD, Engine};
    use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_INPROC_SERVER};
    use windows::Win32::System::Ole::DROPEFFECT_NONE;
    use windows::Win32::UI::Shell::{
        DragQueryFileW, FileOperation, IFileOperation, SHCreateShellItemArrayFromIDLists, HDROP,
    };

    #[test]
    fn shell_data_preserves_utf16_paths_from_multiple_volumes() {
        let _ole = OleApartment::enter().unwrap();
        let first: Vec<u16> = r"C:\第一处\file.txt".encode_utf16().collect();
        let mut second: Vec<u16> = r"D:\其他目录\name-".encode_utf16().collect();
        second.push(0xD800);
        let keys = [first.as_slice(), second.as_slice()].map(|path| {
            STANDARD.encode(
                path.iter()
                    .flat_map(|unit| unit.to_le_bytes())
                    .collect::<Vec<_>>(),
            )
        });
        let paths = keys
            .iter()
            .map(|key| decode_path(key).unwrap())
            .collect::<Vec<_>>();
        let data = file_data_object(&paths, None).unwrap();
        let mut medium = unsafe { data.GetData(&global_format(CF_HDROP.0)) }.unwrap();
        let drop = HDROP(unsafe { medium.u.hGlobal }.0);
        unsafe {
            assert_eq!(DragQueryFileW(drop, u32::MAX, None), 2);
            for (index, expected) in [first, second].iter().enumerate() {
                let mut actual = vec![0; expected.len() + 1];
                let length = DragQueryFileW(drop, index as u32, Some(&mut actual));
                assert_eq!(length as usize, expected.len());
                assert_eq!(&actual[..length as usize], expected);
                assert_eq!(actual[length as usize], 0);
            }
            ReleaseStgMedium(&mut medium);
        }
    }

    #[test]
    fn source_cleanup_requires_both_move_confirmations() {
        let _ole = OleApartment::enter().unwrap();
        let data = unsafe { SHCreateDataObject::<_, IDataObject>(None, None, None) }.unwrap();
        let format = performed_effect_format().unwrap();
        assert!(!needs_source_cleanup(&data, format, DROPEFFECT_MOVE));
        set_global_data(&data, format, &DROPEFFECT_NONE.0.to_le_bytes()).unwrap();
        assert!(!needs_source_cleanup(&data, format, DROPEFFECT_MOVE));
        set_global_data(&data, format, &DROPEFFECT_MOVE.0.to_le_bytes()).unwrap();
        assert!(!needs_source_cleanup(&data, format, DROPEFFECT_COPY));
        assert!(needs_source_cleanup(&data, format, DROPEFFECT_MOVE));
    }

    #[test]
    fn shell_resolves_items_under_different_parents() {
        use windows::Win32::UI::Shell::{
            IShellItemArray, SHCreateShellItemArrayFromDataObject, SICHINT_CANONICAL,
        };

        let _ole = OleApartment::enter().unwrap();
        let windows_dir = PathBuf::from(std::env::var_os("SystemRoot").unwrap());
        let paths = [
            windows_dir.join("System32"),
            windows_dir.join("System32").join("kernel32.dll"),
        ];
        assert_ne!(
            file_identity(&paths[0]).unwrap(),
            file_identity(&paths[1]).unwrap()
        );
        let ids = ItemIdList::from_paths(&paths).unwrap();
        let id_ptrs: Vec<_> = ids.iter().map(ItemIdList::as_ptr).collect();
        let data = file_data_object(&[windows_dir.join("raw-hdrop-path")], Some(&id_ptrs)).unwrap();
        unsafe {
            let mut medium = data.GetData(&global_format(CF_HDROP.0)).unwrap();
            let drop = HDROP(medium.u.hGlobal.0);
            let path_count = DragQueryFileW(drop, u32::MAX, None);
            ReleaseStgMedium(&mut medium);
            assert_eq!(path_count, 1);
            let offered: IShellItemArray = SHCreateShellItemArrayFromDataObject(&data).unwrap();
            assert_eq!(offered.GetCount().unwrap(), 2);
            let items = SHCreateShellItemArrayFromIDLists(&id_ptrs).unwrap();
            for index in 0..2 {
                let item = offered.GetItemAt(index).unwrap();
                let expected = items.GetItemAt(index).unwrap();
                assert_eq!(
                    item.Compare(&expected, SICHINT_CANONICAL.0 as u32).unwrap(),
                    0
                );
            }
            let operation: IFileOperation =
                CoCreateInstance(&FileOperation, None, CLSCTX_INPROC_SERVER).unwrap();
            operation.DeleteItems(&items).unwrap();
        }
    }
}
