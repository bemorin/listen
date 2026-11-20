//! Media keys when Listen is not the foreground app (Windows).
//!
//! Windows sends media keys to the media session of the foreground app when it has one,
//! even a paused one, and only otherwise to the most recent session. An app that keeps a
//! paused session (a browser, a chat app) therefore swallows them while it has the focus.
//!
//! While Listen is the active player (playing, or paused less than half an hour ago), it
//! registers the media keys as system hotkeys so it receives them whatever has the focus.
//! It does not steal them from other players: when Listen is not playing and another app's
//! session is, the key is forwarded to that session through the system media API.

use std::sync::OnceLock;
use windows::Media::Control::{
    GlobalSystemMediaTransportControlsSession, GlobalSystemMediaTransportControlsSessionManager,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus,
};
use windows_sys::Win32::Foundation::HWND;
use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
    RegisterHotKey, UnregisterHotKey, MOD_NOREPEAT, VK_MEDIA_NEXT_TRACK, VK_MEDIA_PLAY_PAUSE,
    VK_MEDIA_PREV_TRACK, VK_MEDIA_STOP,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MediaKey {
    PlayPause,
    Next,
    Previous,
    Stop,
}

const KEYS: [(i32, u16, MediaKey); 4] = [
    (0x4C01, VK_MEDIA_PLAY_PAUSE, MediaKey::PlayPause),
    (0x4C02, VK_MEDIA_NEXT_TRACK, MediaKey::Next),
    (0x4C03, VK_MEDIA_PREV_TRACK, MediaKey::Previous),
    (0x4C04, VK_MEDIA_STOP, MediaKey::Stop),
];

/// The key for a WM_HOTKEY id, if it is one of ours.
pub fn key_for(id: usize) -> Option<MediaKey> {
    KEYS.iter().find(|(i, _, _)| *i as usize == id).map(|(_, _, k)| *k)
}

/// Registers (or releases) the media keys for `hwnd`. Returns whether they are held.
pub fn set_registered(hwnd: isize, on: bool) -> bool {
    let mut held = false;
    for (id, vk, _) in KEYS {
        unsafe {
            if on {
                held |= RegisterHotKey(hwnd as HWND, id, MOD_NOREPEAT, vk as u32) != 0;
            } else {
                UnregisterHotKey(hwnd as HWND, id);
            }
        }
    }
    held
}

fn manager() -> Option<&'static GlobalSystemMediaTransportControlsSessionManager> {
    static MANAGER: OnceLock<Option<GlobalSystemMediaTransportControlsSessionManager>> = OnceLock::new();
    MANAGER
        .get_or_init(|| GlobalSystemMediaTransportControlsSessionManager::RequestAsync().ok()?.join().ok())
        .as_ref()
}

/// Another app's media session that is playing right now, if any.
fn other_playing_session() -> Option<GlobalSystemMediaTransportControlsSession> {
    let own = std::env::current_exe()
        .ok()
        .and_then(|p| p.file_name().map(|n| n.to_string_lossy().to_lowercase()))
        .unwrap_or_default();
    let sessions = manager()?.GetSessions().ok()?;
    for session in sessions {
        let id = session.SourceAppUserModelId().map(|s| s.to_string_lossy().to_lowercase()).unwrap_or_default();
        if !own.is_empty() && id.ends_with(&own) {
            continue;
        }
        let playing = session
            .GetPlaybackInfo()
            .and_then(|info| info.PlaybackStatus())
            .map(|s| s == GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing)
            .unwrap_or(false);
        if playing {
            return Some(session);
        }
    }
    None
}

/// Sends `key` to another app that is playing, when there is one. Returns whether it did.
pub fn forward_to_other_player(key: MediaKey) -> bool {
    let Some(session) = other_playing_session() else { return false };
    let sent = match key {
        MediaKey::PlayPause => session.TryTogglePlayPauseAsync().is_ok(),
        MediaKey::Next => session.TrySkipNextAsync().is_ok(),
        MediaKey::Previous => session.TrySkipPreviousAsync().is_ok(),
        MediaKey::Stop => session.TryStopAsync().is_ok(),
    };
    sent
}
