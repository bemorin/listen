fn main() {
    slint_build::compile("ui/app.slint").expect("Slint UI failed to compile");

    // Windows: icon, version info and manifest. Everything else is platform-neutral.
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        #[cfg(windows)]
        {
            let mut res = winresource::WindowsResource::new();
            res.set_icon("../build/icon.ico");
            res.set("ProductName", "Listen");
            res.set("FileDescription", "Listen");
            res.set_manifest(include_str!("assets/listen.manifest"));
            if let Err(e) = res.compile() {
                println!("cargo:warning=Windows resources skipped: {e}");
            }
        }
    }
    println!("cargo:rerun-if-changed=assets/listen.manifest");
}
