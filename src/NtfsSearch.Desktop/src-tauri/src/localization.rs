use serde_json::Value;
use std::{
    env,
    fmt::Display,
    fs,
    path::PathBuf,
    sync::{OnceLock, RwLock},
};
use windows::{
    core::PWSTR,
    Win32::Globalization::{GetUserPreferredUILanguages, MUI_LANGUAGE_NAME},
};

include!(concat!(env!("OUT_DIR"), "/desktop_locales.rs"));

const FALLBACK_LANGUAGE: &str = "en";

struct Catalog {
    language: &'static str,
    messages: Value,
}

static CATALOGS: OnceLock<Vec<Catalog>> = OnceLock::new();
static CURRENT: OnceLock<RwLock<&'static Catalog>> = OnceLock::new();
static SYSTEM_LANGUAGE: OnceLock<Option<String>> = OnceLock::new();

fn catalogs() -> &'static [Catalog] {
    CATALOGS.get_or_init(|| {
        EMBEDDED_CATALOGS
            .iter()
            .map(|&(language, content)| {
                let mut data: Value = serde_json::from_str(content)
                    .unwrap_or_else(|error| panic!("Invalid desktop locale {language}: {error}"));
                Catalog {
                    language,
                    messages: data["messages"].take(),
                }
            })
            .collect()
    })
}

fn find_catalog(language: &str) -> Option<&'static Catalog> {
    catalogs()
        .iter()
        .find(|catalog| catalog.language.eq_ignore_ascii_case(language))
}

fn fallback() -> &'static Catalog {
    find_catalog(FALLBACK_LANGUAGE).expect("Missing English desktop locale")
}

/// 读取 Windows 显示语言的首选项，供桌面壳启动和后端自动选择使用。
pub(crate) fn system_language() -> &'static str {
    SYSTEM_LANGUAGE
        .get_or_init(|| {
            let mut count = 0;
            let mut length = 0;
            unsafe {
                GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, &mut count, None, &mut length)
                    .ok()?;
            }
            let mut buffer = vec![0u16; length as usize];
            unsafe {
                GetUserPreferredUILanguages(
                    MUI_LANGUAGE_NAME,
                    &mut count,
                    Some(PWSTR(buffer.as_mut_ptr())),
                    &mut length,
                )
                .ok()?;
            }
            // Windows 返回 NUL 分隔的语言列表，首项对应当前显示语言。
            let primary = buffer.split(|&unit| unit == 0).next()?;
            (!primary.is_empty()).then(|| String::from_utf16_lossy(primary))
        })
        .as_deref()
        .unwrap_or(FALLBACK_LANGUAGE)
}

fn system_catalog(mut language: &str) -> Option<&'static Catalog> {
    loop {
        if let Some(catalog) = find_catalog(language) {
            return Some(catalog);
        }
        match language.rsplit_once('-') {
            Some((parent, _)) => language = parent,
            None => break,
        }
    }
    if language.eq_ignore_ascii_case("zh") {
        if let Some(catalog) = find_catalog("zh-CN") {
            return Some(catalog);
        }
    }
    catalogs().iter().find(|catalog| {
        catalog
            .language
            .split_once('-')
            .is_some_and(|(base, _)| base.eq_ignore_ascii_case(language))
    })
}

fn saved_catalog() -> Option<&'static Catalog> {
    // 仅在启动时读取后端使用的设置文件。之后由后端发布生效的语言。
    let path = if crate::desktop_mode() == "--desktop" {
        env::current_exe().ok()?.with_file_name("settings.json")
    } else {
        PathBuf::from(env::var_os("NTFS_SEARCH_TEST_DATA")?).join("settings.json")
    };
    let content = fs::read(path).ok()?;
    let content = content.strip_prefix(b"\xef\xbb\xbf").unwrap_or(&content);
    let settings: Value = serde_json::from_slice(content).ok()?;
    find_catalog(settings.get("Language")?.as_str()?)
}

fn current() -> &'static RwLock<&'static Catalog> {
    CURRENT.get_or_init(|| {
        RwLock::new(
            saved_catalog()
                .or_else(|| system_catalog(system_language()))
                .unwrap_or_else(fallback),
        )
    })
}

/// 返回当前词典的规范语言标签。
pub(crate) fn language() -> &'static str {
    current().read().unwrap().language
}

/// 以已保存设置返回的语言刷新当前译文，返回值表示托盘标签是否需要更新。
pub(crate) fn set_language(language: &str) -> bool {
    let next = find_catalog(language).unwrap_or_else(fallback);
    let mut current = current().write().unwrap();
    if current.language == next.language {
        return false;
    }
    *current = next;
    true
}

/// 从当前词典读取完整文案，缺失条目使用英文。
pub(crate) fn text(key: &'static str) -> &'static str {
    let catalog = *current().read().unwrap();
    catalog.messages[key]
        .as_str()
        .or_else(|| fallback().messages[key].as_str())
        .unwrap_or(key)
}

/// 用命名占位符填入路径或诊断原文。只替换译文中的占位符，保留参数中的花括号。
pub(crate) fn format(key: &'static str, parameters: &[(&str, &str)]) -> String {
    let mut template = text(key);
    let mut message = String::with_capacity(template.len());
    while let Some((prefix, rest)) = template.split_once('{') {
        let Some((name, suffix)) = rest.split_once('}') else {
            break;
        };
        message.push_str(prefix);
        if let Some((_, parameter)) = parameters.iter().find(|(key, _)| *key == name) {
            message.push_str(parameter);
        } else {
            message.push('{');
            message.push_str(name);
            message.push('}');
        }
        template = suffix;
    }
    message.push_str(template);
    message
}

/// 给系统错误添加本地化上下文，同时保留诊断详情。
pub(crate) fn error(key: &'static str, error: impl Display) -> String {
    format(key, &[("error", &error.to_string())])
}
