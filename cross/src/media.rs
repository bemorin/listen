//! Media keys, headset buttons and the OS "now playing" panel, through souvlaki:
//! System Media Transport Controls on Windows, MPRIS on Linux, MPNowPlayingInfoCenter on macOS.

use souvlaki::{
    MediaControlEvent, MediaControls, MediaMetadata, MediaPlayback, MediaPosition, PlatformConfig,
    SeekDirection,
};
use std::time::Duration;

#[derive(Clone, Copy, Debug)]
pub enum Command {
    Play,
    Pause,
    Toggle,
    Next,
    Previous,
    Stop,
    SeekBy(f64),
    SeekTo(Duration),
    Raise,
    Quit,
}

pub struct Media {
    controls: MediaControls,
    last: Option<(bool, bool)>,
}

impl Media {
    /// `hwnd` is required on Windows. `on_command` runs on a souvlaki thread.
    pub fn new(
        hwnd: Option<*mut std::ffi::c_void>,
        on_command: impl Fn(Command) + Send + 'static,
    ) -> Option<Media> {
        let config = PlatformConfig {
            display_name: "Listen",
            dbus_name: "listen",
            hwnd,
        };
        let mut controls = MediaControls::new(config).ok()?;
        controls
            .attach(move |e| {
                let cmd = match e {
                    MediaControlEvent::Play => Command::Play,
                    MediaControlEvent::Pause => Command::Pause,
                    MediaControlEvent::Toggle => Command::Toggle,
                    MediaControlEvent::Next => Command::Next,
                    MediaControlEvent::Previous => Command::Previous,
                    MediaControlEvent::Stop => Command::Stop,
                    MediaControlEvent::Seek(SeekDirection::Forward) => Command::SeekBy(10.0),
                    MediaControlEvent::Seek(SeekDirection::Backward) => Command::SeekBy(-10.0),
                    MediaControlEvent::SeekBy(SeekDirection::Forward, d) => {
                        Command::SeekBy(d.as_secs_f64())
                    }
                    MediaControlEvent::SeekBy(SeekDirection::Backward, d) => {
                        Command::SeekBy(-d.as_secs_f64())
                    }
                    MediaControlEvent::SetPosition(MediaPosition(d)) => Command::SeekTo(d),
                    MediaControlEvent::Raise => Command::Raise,
                    MediaControlEvent::Quit => Command::Quit,
                    _ => return,
                };
                on_command(cmd);
            })
            .ok()?;
        Some(Media {
            controls,
            last: None,
        })
    }

    pub fn set_track(
        &mut self,
        title: &str,
        artist: &str,
        album: &str,
        duration: Option<Duration>,
        cover_url: Option<&str>,
    ) {
        let _ = self.controls.set_metadata(MediaMetadata {
            title: Some(title),
            artist: Some(artist),
            album: Some(album),
            cover_url,
            duration,
        });
    }

    pub fn set_playback(
        &mut self,
        playing: bool,
        has_track: bool,
        position: Duration,
        force: bool,
    ) {
        if !force && self.last == Some((playing, has_track)) {
            return;
        }
        self.last = Some((playing, has_track));
        let progress = Some(MediaPosition(position));
        let state = match (has_track, playing) {
            (false, _) => MediaPlayback::Stopped,
            (true, true) => MediaPlayback::Playing { progress },
            (true, false) => MediaPlayback::Paused { progress },
        };
        let _ = self.controls.set_playback(state);
    }
}
