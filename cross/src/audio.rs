//! Playback engine: symphonia decoders (through rodio) feeding the default output device.
//!
//! The output device is opened only while something is playing, and released after the
//! player has been paused for a while, so an idle Listen holds no audio thread, no device
//! buffers and costs no CPU. Resuming reopens the device and seeks back transparently.

use rodio::{Decoder, DeviceSinkBuilder, MixerDeviceSink, Player, Source};
use std::fs::File;
use std::io::BufReader;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

/// Paused this long, the output device is released.
const RELEASE_AFTER: Duration = Duration::from_secs(20);

pub struct Engine {
    device: Option<MixerDeviceSink>,
    player: Option<Player>,
    path: Option<PathBuf>,
    duration: Option<Duration>,
    /// Position to restore when the device was released while paused.
    parked_at: Option<Duration>,
    playing: bool,
    paused_since: Option<Instant>,
    volume: f32,
}

impl Engine {
    pub fn new(volume: f32) -> Self {
        Engine {
            device: None,
            player: None,
            path: None,
            duration: None,
            parked_at: None,
            playing: false,
            paused_since: None,
            volume,
        }
    }

    fn ensure_device(&mut self) -> Result<(), String> {
        if self.device.is_none() {
            let mut device = DeviceSinkBuilder::open_default_sink().map_err(|e| e.to_string())?;
            device.log_on_drop(false);
            self.device = Some(device);
        }
        Ok(())
    }

    /// Opens `path`, paused at the start. Returns the duration when the container knows it.
    pub fn load(&mut self, path: &Path) -> Result<Option<Duration>, String> {
        self.unload();
        self.ensure_device()?;
        let player = self.open(path)?;
        self.player = Some(player);
        self.path = Some(path.to_path_buf());
        Ok(self.duration)
    }

    fn open(&mut self, path: &Path) -> Result<Player, String> {
        let file = File::open(path).map_err(|e| e.to_string())?;
        let len = file.metadata().map(|m| m.len()).unwrap_or(0);
        let mut builder = Decoder::builder()
            .with_data(BufReader::with_capacity(64 * 1024, file))
            .with_byte_len(len)
            .with_seekable(true)
            .with_gapless(true);
        if let Some(ext) = path.extension().and_then(|e| e.to_str()) {
            builder = builder.with_hint(ext);
        }
        let decoder = builder.build().map_err(|e| e.to_string())?;
        self.duration = decoder.total_duration();
        let device = self.device.as_ref().ok_or("no output device")?;
        let player = Player::connect_new(device.mixer());
        player.pause();
        player.set_volume(perceptual(self.volume));
        player.append(decoder);
        Ok(player)
    }

    pub fn unload(&mut self) {
        if let Some(p) = self.player.take() {
            p.stop();
        }
        self.path = None;
        self.duration = None;
        self.parked_at = None;
        self.playing = false;
        self.paused_since = None;
    }

    pub fn is_loaded(&self) -> bool {
        self.path.is_some()
    }

    pub fn is_playing(&self) -> bool {
        self.playing
    }

    pub fn duration(&self) -> Option<Duration> {
        self.duration
    }

    /// The decoder did not know the length (some VBR MP3s); a scan found it.
    pub fn set_duration_hint(&mut self, d: Duration) {
        if self.duration.is_none() {
            self.duration = Some(d);
        }
    }

    pub fn play(&mut self) -> Result<(), String> {
        if self.player.is_none() {
            let Some(path) = self.path.clone() else {
                return Err("nothing loaded".into());
            };
            // The device was released while paused: reopen and return to the same spot.
            self.ensure_device()?;
            let player = self.open(&path)?;
            if let Some(pos) = self.parked_at.take() {
                let _ = player.try_seek(pos);
            }
            self.player = Some(player);
        }
        if let Some(p) = &self.player {
            p.play();
        }
        self.playing = true;
        self.paused_since = None;
        Ok(())
    }

    pub fn pause(&mut self) {
        if let Some(p) = &self.player {
            p.pause();
        }
        self.playing = false;
        self.paused_since = Some(Instant::now());
    }

    /// How long playback has been paused, if it is.
    pub fn paused_for(&self) -> Option<Duration> {
        self.paused_since.map(|t| t.elapsed())
    }

    pub fn position(&self) -> Duration {
        match (&self.player, self.parked_at) {
            (Some(p), _) => p.get_pos(),
            (None, Some(pos)) => pos,
            _ => Duration::ZERO,
        }
    }

    pub fn seek(&mut self, pos: Duration) {
        let pos = match self.duration {
            Some(d) => pos.min(d.saturating_sub(Duration::from_millis(50))),
            None => pos,
        };
        match &self.player {
            Some(p) => {
                if p.empty() {
                    // Seeking after the track ended: reopen it first.
                    if let Some(path) = self.path.clone() {
                        if let Ok(np) = self.open(&path) {
                            if self.playing {
                                np.play();
                            }
                            self.player = Some(np);
                        }
                    }
                }
                if let Some(p) = &self.player {
                    let _ = p.try_seek(pos);
                }
            }
            None => self.parked_at = Some(pos),
        }
    }

    pub fn set_volume(&mut self, v: f32) {
        self.volume = v.clamp(0.0, 1.0);
        if let Some(p) = &self.player {
            p.set_volume(perceptual(self.volume));
        }
    }

    /// The current track played to its end.
    pub fn finished(&self) -> bool {
        self.playing && self.player.as_ref().map(|p| p.empty()).unwrap_or(false)
    }

    /// Housekeeping, called a few times a second: releases the device after a long pause.
    pub fn tick(&mut self) {
        if let Some(since) = self.paused_since {
            if since.elapsed() >= RELEASE_AFTER && self.player.is_some() {
                self.parked_at = Some(self.position());
                self.player = None;
                self.device = None;
            }
        }
        if !self.is_loaded() && self.device.is_some() {
            self.device = None;
        }
    }
}

/// Slider position to amplitude: a squared curve so the slider feels even to the ear.
fn perceptual(v: f32) -> f32 {
    // Debug builds: LISTEN_SILENT=1 keeps test runs inaudible whatever the volume says.
    #[cfg(any(debug_assertions, feature = "test-hooks"))]
    if std::env::var_os("LISTEN_SILENT").is_some() {
        return 0.0;
    }
    v * v
}
