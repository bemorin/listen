//! Persisted preferences and play queue.
//!
//! Same plain formats as the Windows app (`settings.ini` with `key=value` lines and
//! `queue.txt` with one base64-encoded UTF-8 path per line), stored next to them in
//! `<config>/Listen/` under `cross-` names so both builds can live side by side. On
//! first launch the Windows app's files are imported.

use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};

pub const EXTENSIONS: [&str; 5] = ["mp3", "wav", "m4a", "aac", "flac"];

pub fn is_supported(path: &Path) -> bool {
    path.extension()
        .and_then(|e| e.to_str())
        .map(|e| EXTENSIONS.iter().any(|x| x.eq_ignore_ascii_case(e)))
        .unwrap_or(false)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Repeat {
    Off = 0,
    Queue = 1,
    Track = 2,
}

impl Repeat {
    pub fn from_i32(v: i32) -> Self {
        match v {
            1 => Repeat::Queue,
            2 => Repeat::Track,
            _ => Repeat::Off,
        }
    }
    pub fn next(self) -> Self {
        Repeat::from_i32((self as i32 + 1) % 3)
    }
}

#[derive(Clone, Debug)]
pub struct AppState {
    pub volume: f32,
    pub last_audible_volume: f32,
    pub always_on_top: bool,
    pub mini: bool,
    pub queue_visible: bool,
    pub mini_width: f32,
    pub repeat: Repeat,
    /// Show the remaining time instead of the duration.
    pub show_remaining: bool,
    /// Remote mode: with nothing of its own playing, show and drive what another app plays.
    pub remote: bool,
    pub queue: Vec<PathBuf>,
    pub current: Option<usize>,
}

impl Default for AppState {
    fn default() -> Self {
        AppState {
            volume: 0.82,
            last_audible_volume: 0.82,
            always_on_top: false,
            mini: false,
            queue_visible: false,
            mini_width: 390.0,
            repeat: Repeat::Off,
            show_remaining: false,
            remote: true,
            queue: Vec::new(),
            current: None,
        }
    }
}

pub fn data_dir() -> PathBuf {
    // Lets tests and portable setups keep their state elsewhere.
    if let Some(dir) = std::env::var_os("LISTEN_DATA_DIR") {
        return PathBuf::from(dir);
    }
    #[cfg(windows)]
    let base = std::env::var_os("APPDATA").map(PathBuf::from);
    #[cfg(target_os = "macos")]
    let base =
        std::env::var_os("HOME").map(|h| PathBuf::from(h).join("Library/Application Support"));
    #[cfg(all(unix, not(target_os = "macos")))]
    let base = std::env::var_os("XDG_CONFIG_HOME")
        .map(PathBuf::from)
        .filter(|p| p.is_absolute())
        .or_else(|| std::env::var_os("HOME").map(|h| PathBuf::from(h).join(".config")));
    base.unwrap_or_else(std::env::temp_dir).join("Listen")
}

fn settings_path(dir: &Path) -> PathBuf {
    dir.join("cross-settings.ini")
}
fn queue_path(dir: &Path) -> PathBuf {
    dir.join("cross-queue.txt")
}

impl AppState {
    pub fn load() -> AppState {
        let dir = data_dir();
        let mut settings = settings_path(&dir);
        let mut queue = queue_path(&dir);
        if !settings.exists() && !queue.exists() {
            // First launch: start from the Windows app's state when there is one.
            settings = dir.join("settings.ini");
            queue = dir.join("queue.txt");
        }

        let mut s = AppState::default();
        if let Ok(text) = fs::read_to_string(&settings) {
            for line in text.lines() {
                let Some((k, v)) = line.split_once('=') else {
                    continue;
                };
                let (k, v) = (k.trim(), v.trim());
                let f = v.parse::<f32>().ok();
                match k.to_ascii_lowercase().as_str() {
                    "volume" => {
                        if let Some(f) = f {
                            s.volume = f.clamp(0.0, 1.0)
                        }
                    }
                    "lastvolume" => {
                        if let Some(f) = f {
                            s.last_audible_volume = f.clamp(0.01, 1.0)
                        }
                    }
                    "topmost" => s.always_on_top = v == "1",
                    "mini" => s.mini = v == "1",
                    "queuevisible" => s.queue_visible = v == "1",
                    "miniwidth" => {
                        if let Some(f) = f {
                            if f > 0.0 {
                                s.mini_width = f.min(10000.0)
                            }
                        }
                    }
                    "loopmode" => s.repeat = Repeat::from_i32(v.parse().unwrap_or(0)),
                    "current" => s.current = v.parse().ok(),
                    "remaining" => s.show_remaining = v == "1",
                    "remote" => s.remote = v != "0",
                    _ => {}
                }
            }
        }
        if s.volume > 0.001 {
            s.last_audible_volume = s.volume;
        }

        if let Ok(text) = fs::read_to_string(&queue) {
            for line in text.lines().map(str::trim).filter(|l| !l.is_empty()) {
                let Some(bytes) = base64_decode(line) else {
                    continue;
                };
                let Ok(p) = String::from_utf8(bytes) else {
                    continue;
                };
                let p = PathBuf::from(p);
                if is_supported(&p) && p.is_file() {
                    s.queue.push(p);
                }
            }
        }
        s.current = match s.current {
            Some(i) if i < s.queue.len() => Some(i),
            _ if !s.queue.is_empty() => Some(0),
            _ => None,
        };
        s
    }

    pub fn save(&self) {
        let dir = data_dir();
        if fs::create_dir_all(&dir).is_err() {
            return;
        }
        let mut settings = String::new();
        settings.push_str(&format!("volume={:.3}\n", self.volume));
        settings.push_str(&format!("lastVolume={:.3}\n", self.last_audible_volume));
        settings.push_str(&format!("topmost={}\n", self.always_on_top as u8));
        settings.push_str(&format!("mini={}\n", self.mini as u8));
        settings.push_str(&format!("queueVisible={}\n", self.queue_visible as u8));
        settings.push_str(&format!("miniWidth={:.0}\n", self.mini_width));
        settings.push_str(&format!("loopMode={}\n", self.repeat as i32));
        settings.push_str(&format!("remaining={}\n", self.show_remaining as u8));
        settings.push_str(&format!("remote={}\n", self.remote as u8));
        if let Some(c) = self.current {
            settings.push_str(&format!("current={c}\n"));
        }
        atomic_write(&settings_path(&dir), settings.as_bytes());

        let mut queue = String::new();
        for p in &self.queue {
            if let Some(s) = p.to_str() {
                queue.push_str(&base64_encode(s.as_bytes()));
                queue.push('\n');
            }
        }
        atomic_write(&queue_path(&dir), queue.as_bytes());
    }
}

impl AppState {
    /// Moves a queue item, keeping `current` on the same track.
    pub fn move_item(&mut self, from: usize, to: usize) {
        if from >= self.queue.len() || to >= self.queue.len() || from == to {
            return;
        }
        let item = self.queue.remove(from);
        self.queue.insert(to, item);
        if let Some(c) = self.current {
            self.current = Some(if c == from {
                to
            } else if from < c && to >= c {
                c - 1
            } else if from > c && to <= c {
                c + 1
            } else {
                c
            });
        }
    }
}

fn atomic_write(path: &Path, data: &[u8]) {
    let tmp = path.with_extension("tmp");
    let ok = fs::File::create(&tmp)
        .and_then(|mut f| f.write_all(data).and_then(|_| f.sync_all()))
        .is_ok();
    if ok {
        let _ = fs::rename(&tmp, path);
    }
}

const B64: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

fn base64_encode(data: &[u8]) -> String {
    let mut out = String::with_capacity(data.len().div_ceil(3) * 4);
    for chunk in data.chunks(3) {
        let b = [
            chunk[0],
            *chunk.get(1).unwrap_or(&0),
            *chunk.get(2).unwrap_or(&0),
        ];
        let n = (b[0] as u32) << 16 | (b[1] as u32) << 8 | b[2] as u32;
        for i in 0..4 {
            if i <= chunk.len() {
                out.push(B64[(n >> (18 - 6 * i) & 63) as usize] as char);
            } else {
                out.push('=');
            }
        }
    }
    out
}

fn base64_decode(text: &str) -> Option<Vec<u8>> {
    let mut out = Vec::with_capacity(text.len() * 3 / 4);
    let mut acc = 0u32;
    let mut bits = 0;
    for c in text.bytes() {
        if c == b'=' {
            break;
        }
        let v = B64.iter().position(|&x| x == c)? as u32;
        acc = acc << 6 | v;
        bits += 6;
        if bits >= 8 {
            bits -= 8;
            out.push((acc >> bits) as u8);
        }
    }
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn base64_round_trip() {
        for s in [
            "",
            "a",
            "ab",
            "abc",
            "C:\\Musique\\★ Back Up.wav",
            "/home/é/x.flac",
        ] {
            assert_eq!(
                base64_decode(&base64_encode(s.as_bytes())).unwrap(),
                s.as_bytes()
            );
        }
        assert_eq!(base64_encode(b"Man"), "TWFu");
        assert_eq!(base64_encode(b"Ma"), "TWE=");
    }

    #[test]
    fn moving_keeps_the_current_track() {
        let q = |v: &[usize]| v.iter().map(|i| PathBuf::from(format!("{i}.mp3"))).collect::<Vec<_>>();
        let mut s = AppState { queue: q(&[0, 1, 2, 3]), current: Some(1), ..Default::default() };
        s.move_item(1, 3);
        assert_eq!(s.queue, q(&[0, 2, 3, 1]));
        assert_eq!(s.current, Some(3));
        s.move_item(0, 3);
        assert_eq!(s.queue, q(&[2, 3, 1, 0]));
        assert_eq!(s.current, Some(2));
        s.move_item(3, 0);
        assert_eq!(s.queue, q(&[0, 2, 3, 1]));
        assert_eq!(s.current, Some(3));
    }

    #[test]
    fn repeat_cycles() {
        assert_eq!(Repeat::Off.next(), Repeat::Queue);
        assert_eq!(Repeat::Queue.next(), Repeat::Track);
        assert_eq!(Repeat::Track.next(), Repeat::Off);
    }
}
