use std::{env, ffi::OsStr, fmt::Write, fs, io, path::PathBuf};

fn main() {
    embed_locales().expect("Could not embed desktop locale catalogs");
    tauri_build::build();
}

fn embed_locales() -> io::Result<()> {
    let manifest_dir = PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").unwrap());
    let directory = manifest_dir.join("../../../locales");
    println!("cargo:rerun-if-changed={}", directory.display());
    let mut entries = fs::read_dir(directory)?.collect::<io::Result<Vec<_>>>()?;
    entries.sort_by_key(fs::DirEntry::file_name);

    // 目录和单个词典都参与 Cargo 的增量检查，以便新增语言或修改译文触发编译。
    let mut source = String::from("const EMBEDDED_CATALOGS: &[(&str, &str)] = &[\n");
    for entry in entries {
        let path = entry.path();
        if path.extension() != Some(OsStr::new("json")) || !path.is_file() {
            continue;
        }
        let language = path.file_stem().and_then(OsStr::to_str).unwrap();
        println!("cargo:rerun-if-changed={}", path.display());
        writeln!(source, "    ({language:?}, include_str!({path:?})),").unwrap();
    }
    source.push_str("];\n");
    let output = PathBuf::from(env::var_os("OUT_DIR").unwrap()).join("desktop_locales.rs");
    fs::write(output, source)
}
