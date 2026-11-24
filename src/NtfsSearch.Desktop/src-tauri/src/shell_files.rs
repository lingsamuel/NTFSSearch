use crate::localization::text;
use base64::{engine::general_purpose::STANDARD, Engine};
use std::{
    ffi::OsString,
    os::windows::ffi::{OsStrExt, OsStringExt},
    path::{Path, PathBuf},
    ptr,
};
use windows::{
    core::{Result, PCWSTR},
    Win32::{
        Foundation::HWND,
        System::{
            Com::{CoCreateInstance, CLSCTX_INPROC_SERVER},
            Ole::{OleInitialize, OleUninitialize},
        },
        UI::Shell::{
            Common::ITEMIDLIST, FileOperation, IFileOperation, ILFree,
            SHCreateShellItemArrayFromIDLists, SHParseDisplayName, FILEOPERATION_FLAGS,
        },
    },
};

pub(crate) struct OleApartment;

impl OleApartment {
    pub(crate) fn enter() -> Result<Self> {
        unsafe { OleInitialize(None)? };
        Ok(Self)
    }
}

impl Drop for OleApartment {
    fn drop(&mut self) {
        unsafe { OleUninitialize() };
    }
}

pub(crate) struct ItemIdList(*mut ITEMIDLIST);

impl ItemIdList {
    fn from_path(path: &Path) -> Result<Self> {
        let wide: Vec<_> = path.as_os_str().encode_wide().chain([0]).collect();
        let mut id = ptr::null_mut();
        unsafe { SHParseDisplayName(PCWSTR(wide.as_ptr()), None, &mut id, 0, None)? };
        Ok(Self(id))
    }

    pub(crate) fn from_paths(paths: &[PathBuf]) -> Result<Vec<Self>> {
        paths.iter().map(|path| Self::from_path(path)).collect()
    }

    pub(crate) fn as_ptr(&self) -> *const ITEMIDLIST {
        self.0.cast_const()
    }
}

impl Drop for ItemIdList {
    fn drop(&mut self) {
        unsafe { ILFree(Some(self.0)) };
    }
}

/// 与后端的路径标识约定一致，保留单独的 UTF-16 代理项和原路径写法。
pub(crate) fn decode_path(key: &str) -> std::result::Result<PathBuf, String> {
    let bytes = STANDARD
        .decode(key)
        .map_err(|_| text("native.filePath.invalidKey").to_owned())?;
    if bytes.len() % 2 != 0 {
        return Err(text("native.filePath.invalidKey").into());
    }
    let units: Vec<_> = bytes
        .chunks_exact(2)
        .map(|pair| u16::from_le_bytes([pair[0], pair[1]]))
        .collect();
    if units.contains(&0) {
        return Err(text("native.filePath.nullInPath").into());
    }
    let path = PathBuf::from(OsString::from_wide(&units));
    if !path.is_absolute() {
        return Err(text("native.filePath.absolutePathRequired").into());
    }
    Ok(path)
}

/// 在已初始化的 STA 上删除一批绝对 Shell 条目，返回 Shell 是否中止了任意一项。
pub(crate) fn delete_items(
    hwnd: HWND,
    ids: &[*const ITEMIDLIST],
    flags: FILEOPERATION_FLAGS,
) -> Result<bool> {
    unsafe {
        let items = SHCreateShellItemArrayFromIDLists(ids)?;
        let operation: IFileOperation =
            CoCreateInstance(&FileOperation, None, CLSCTX_INPROC_SERVER)?;
        operation.SetOwnerWindow(hwnd)?;
        operation.SetOperationFlags(flags)?;
        operation.DeleteItems(&items)?;
        let performed = operation.PerformOperations();
        let aborted = operation.GetAnyOperationsAborted();
        performed?;
        Ok(aborted?.as_bool())
    }
}
