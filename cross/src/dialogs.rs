//! "Add audio files" dialog. Native dialogs on Windows and macOS (rfd); on Linux the desktop's
//! own picker through zenity or kdialog, so Listen links no GUI toolkit there.

use std::path::PathBuf;

pub fn pick_audio_files(title: &str) -> Vec<PathBuf> {
    #[cfg(any(windows, target_os = "macos"))]
    {
        return rfd::FileDialog::new()
            .set_title(title)
            .add_filter("Audio", &crate::state::EXTENSIONS)
            .pick_files()
            .unwrap_or_default();
    }
    #[cfg(all(unix, not(target_os = "macos")))]
    {
        use std::process::Command;
        let patterns: Vec<String> = crate::state::EXTENSIONS
            .iter()
            .flat_map(|e| [format!("*.{e}"), format!("*.{}", e.to_uppercase())])
            .collect();
        let zenity = Command::new("zenity")
            .args([
                "--file-selection",
                "--multiple",
                "--separator=\n",
                "--title",
                title,
            ])
            .arg(format!("--file-filter=Audio | {}", patterns.join(" ")))
            .output();
        let out = match zenity {
            Ok(o) => o,
            Err(_) => match Command::new("kdialog")
                .args([
                    "--getopenfilename",
                    ".",
                    &patterns.join(" "),
                    "--multiple",
                    "--separate-output",
                    "--title",
                    title,
                ])
                .output()
            {
                Ok(o) => o,
                Err(_) => return Vec::new(),
            },
        };
        String::from_utf8_lossy(&out.stdout)
            .lines()
            .map(str::trim)
            .filter(|l| !l.is_empty())
            .map(PathBuf::from)
            .collect()
    }
}
