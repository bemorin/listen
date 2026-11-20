//! Remote mode: when Listen itself plays nothing, it shows and drives what another app plays
//! (Spotify, a browser tab, any media app), through the system's media sessions.
//!
//! - Windows: Global System Media Transport Controls (`Windows.Media.Control`), the API behind
//!   the volume flyout's media panel.
//! - Linux: MPRIS over D-Bus.
//! - macOS: no public API (see README).
//!
//! Each backend runs one thread that sleeps until the OS reports a change: no polling. It reads
//! the session, decodes the artwork once per track, and sends `Update`s to the UI thread.

use crate::meta::Artwork;
use std::time::{Duration, Instant};

#[cfg(windows)]
#[path = "windows.rs"]
mod imp;

#[cfg(all(unix, not(target_os = "macos")))]
#[path = "linux.rs"]
mod imp;

#[cfg(not(any(windows, all(unix, not(target_os = "macos")))))]
mod imp {
    pub struct Backend;
    impl Backend {
        pub fn start(_sink: super::Sink) -> Option<Backend> {
            None
        }
        pub fn send(&self, _cmd: super::Cmd) {}
    }
}

/// What the backend reports, in order. `session` numbers each mirrored session: updates of
/// a session that is no longer mirrored are ignored.
pub enum Update {
    /// A new session is mirrored (`Some(app name)`), or none is left (`None`).
    Session { session: u64, source: Option<String> },
    Track { session: u64, title: String, artist: String, album: String },
    /// The current track's artwork, decoded off the UI thread. `None`: the track has none (yet).
    Art { session: u64, art: Option<Artwork> },
    Playback { session: u64, playing: bool, can_prev: bool, can_next: bool, can_seek: bool },
    /// `position` was the position at `at`; it advances from there while playing.
    Timeline { session: u64, position: Duration, duration: Option<Duration>, at: Instant },
}

impl Update {
    /// One line per update, for `LISTEN_REMOTE_LOG` (test hooks).
    #[cfg(any(debug_assertions, feature = "test-hooks"))]
    pub fn describe(&self) -> String {
        match self {
            Update::Session { session, source } => format!("session {session} {source:?}"),
            Update::Track { session, title, artist, album } => format!("track {session} {title:?} {artist:?} {album:?}"),
            Update::Art { session, art } => {
                format!("art {session} {}", art.as_ref().map(|a| format!("{}px tint {:?}", a.size, a.tint)).unwrap_or("none".into()))
            }
            Update::Playback { session, playing, can_prev, can_next, can_seek } => {
                format!("playback {session} playing={playing} prev={can_prev} next={can_next} seek={can_seek}")
            }
            Update::Timeline { session, position, duration, at } => {
                format!("timeline {session} pos={:.1}s dur={:?} age={:.1}s", position.as_secs_f64(), duration.map(|d| d.as_secs()), at.elapsed().as_secs_f64())
            }
        }
    }
}

#[derive(Clone, Copy, Debug)]
pub enum Cmd {
    Toggle,
    Next,
    Previous,
    SeekTo(Duration),
}

pub type Sink = Box<dyn Fn(Update) + Send + 'static>;

/// A running backend. Dropping it unsubscribes from the OS and ends its thread.
pub struct Remote(imp::Backend);

impl Remote {
    /// `None` where the platform has no usable API. `sink` runs on the backend's thread.
    pub fn start(sink: impl Fn(Update) + Send + 'static) -> Option<Remote> {
        imp::Backend::start(Box::new(sink)).map(Remote)
    }

    pub fn send(&self, cmd: Cmd) {
        self.0.send(cmd);
    }
}

/// A friendlier name for an app id: "Spotify.exe" → "Spotify", "MSEdge" → "Edge".
#[allow(dead_code)]
pub fn pretty_name(id: &str) -> String {
    let base = id.rsplit(['\\', '/', '!']).next().unwrap_or(id);
    let base = base.strip_suffix(".exe").or_else(|| base.strip_suffix(".EXE")).unwrap_or(base);
    let known = match base.to_ascii_lowercase().as_str() {
        "chrome" => Some("Chrome"),
        "msedge" | "microsoftedge" => Some("Edge"),
        "firefox" | "308046b0af4a39cb" | "6f193ccc56814779" => Some("Firefox"),
        "brave" => Some("Brave"),
        "opera" | "launcher" => Some("Opera"),
        "vivaldi" => Some("Vivaldi"),
        "spotify" => Some("Spotify"),
        "vlc" => Some("VLC"),
        "foobar2000" => Some("foobar2000"),
        "musicbee" => Some("MusicBee"),
        "itunes" => Some("iTunes"),
        "deezer" => Some("Deezer"),
        "tidal" => Some("TIDAL"),
        _ => None,
    };
    if let Some(k) = known {
        return k.to_string();
    }
    let mut chars = base.chars();
    match chars.next() {
        Some(c) => c.to_uppercase().chain(chars).collect(),
        None => String::new(),
    }
}

#[cfg(test)]
mod tests {
    use super::pretty_name;

    #[test]
    fn names() {
        assert_eq!(pretty_name("Spotify.exe"), "Spotify");
        assert_eq!(pretty_name("Chrome"), "Chrome");
        assert_eq!(pretty_name("MSEdge"), "Edge");
        assert_eq!(pretty_name("308046B0AF4A39CB"), "Firefox");
        assert_eq!(pretty_name(r"C:\Apps\foo.exe"), "Foo");
        assert_eq!(pretty_name("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"), "Spotify");
    }
}
