//! Windows backend: Global System Media Transport Controls (`Windows.Media.Control`).
//!
//! One thread owns the session manager and the mirrored session. The OS calls the event
//! handlers on its thread pool; they only wake this thread, which reads the session, decodes
//! the artwork once per track and reports to the UI, in order. Between events it sleeps.

use super::{pretty_name, Cmd, Sink, Update};
use crate::meta;
use std::sync::mpsc::{self, Receiver, Sender};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use windows::core::{Interface, HSTRING};
use windows::Foundation::TypedEventHandler;
use windows_future::IAsyncOperation;
use windows::Media::Control::{
    GlobalSystemMediaTransportControlsSession as Session,
    GlobalSystemMediaTransportControlsSessionManager as Manager,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus as Status,
};
use windows::Storage::Streams::{DataReader, IRandomAccessStreamReference};

enum Ev {
    Rebind,
    Props,
    Playback,
    Timeline,
    Cmd(Cmd),
    Quit,
}

pub struct Backend {
    tx: Sender<Ev>,
}

impl Backend {
    pub fn start(sink: Sink) -> Option<Backend> {
        let (tx, rx) = mpsc::channel();
        let handlers = tx.clone();
        std::thread::Builder::new()
            .name("listen-remote".into())
            .stack_size(512 * 1024)
            .spawn(move || {
                let _ = run(handlers, rx, &sink);
            })
            .ok()?;
        Some(Backend { tx })
    }

    pub fn send(&self, cmd: Cmd) {
        let _ = self.tx.send(Ev::Cmd(cmd));
    }
}

impl Drop for Backend {
    fn drop(&mut self) {
        let _ = self.tx.send(Ev::Quit);
    }
}

#[derive(Default)]
struct Pending {
    rebind: bool,
    props: bool,
    playback: bool,
    timeline: bool,
    cmds: Vec<Cmd>,
    quit: bool,
}

impl Pending {
    fn add(&mut self, ev: Ev) {
        match ev {
            Ev::Rebind => self.rebind = true,
            Ev::Props => self.props = true,
            Ev::Playback => self.playback = true,
            Ev::Timeline => self.timeline = true,
            Ev::Cmd(c) => self.cmds.push(c),
            Ev::Quit => self.quit = true,
        }
    }
}

fn run(tx: Sender<Ev>, rx: Receiver<Ev>, sink: &Sink) -> windows::core::Result<()> {
    let manager = Manager::RequestAsync()?.join()?;
    let on_current = {
        let tx = tx.clone();
        manager.CurrentSessionChanged(&TypedEventHandler::new(move |_, _| {
            let _ = tx.send(Ev::Rebind);
            Ok(())
        }))?
    };
    let on_sessions = {
        let tx = tx.clone();
        manager.SessionsChanged(&TypedEventHandler::new(move |_, _| {
            let _ = tx.send(Ev::Rebind);
            Ok(())
        }))?
    };

    let mut mirror = Mirror::new(tx);
    let mut pending = Pending { rebind: true, ..Default::default() };
    loop {
        if pending.rebind {
            mirror.rebind(&manager, sink);
        }
        for cmd in pending.cmds.drain(..) {
            mirror.command(cmd);
        }
        if pending.props {
            let _ = mirror.props(sink);
        }
        if pending.playback {
            let _ = mirror.playback(sink);
        }
        if pending.timeline || pending.playback {
            let _ = mirror.timeline(sink);
        }
        pending = Pending::default();
        // Sleep until the OS (or the UI) has something new; take a burst of events at once.
        match rx.recv() {
            Ok(ev) => pending.add(ev),
            Err(_) => break,
        }
        while let Ok(ev) = rx.try_recv() {
            pending.add(ev);
        }
        if pending.quit {
            break;
        }
    }
    mirror.unbind();
    let _ = manager.RemoveCurrentSessionChanged(on_current);
    let _ = manager.RemoveSessionsChanged(on_sessions);
    Ok(())
}

struct Mirror {
    tx: Sender<Ev>,
    /// Listen's own app ids: its own media session is never mirrored.
    own: Vec<String>,
    session: Option<Session>,
    tokens: [i64; 3],
    id: u64,
    track_key: Option<String>,
    art_done: bool,
    last_playback: Option<(bool, bool, bool, bool)>,
    last_timeline: Option<(i64, i64, i64, i64)>,
    start_ticks: i64,
}

impl Mirror {
    fn new(tx: Sender<Ev>) -> Mirror {
        let mut own = Vec::new();
        if let Ok(exe) = std::env::current_exe() {
            own.push(exe.to_string_lossy().to_lowercase());
            if let Some(n) = exe.file_name() {
                own.push(n.to_string_lossy().to_lowercase());
            }
            if let Some(n) = exe.file_stem() {
                own.push(n.to_string_lossy().to_lowercase());
            }
        }
        Mirror {
            tx,
            own,
            session: None,
            tokens: [0; 3],
            id: 0,
            track_key: None,
            art_done: false,
            last_playback: None,
            last_timeline: None,
            start_ticks: 0,
        }
    }

    fn is_own(&self, s: &Session) -> bool {
        let id = s.SourceAppUserModelId().map(|h| h.to_string_lossy().to_lowercase()).unwrap_or_default();
        self.own.iter().any(|o| *o == id)
    }

    /// The session the system shows (unless it is Listen's), else one that plays, else any.
    fn pick(&self, manager: &Manager) -> Option<Session> {
        if let Ok(current) = manager.GetCurrentSession() {
            if !self.is_own(&current) {
                return Some(current);
            }
        }
        let sessions = manager.GetSessions().ok()?;
        let mut first = None;
        for i in 0..sessions.Size().unwrap_or(0) {
            let Ok(s) = sessions.GetAt(i) else { continue };
            if self.is_own(&s) {
                continue;
            }
            let playing = s
                .GetPlaybackInfo()
                .and_then(|p| p.PlaybackStatus())
                .map(|st| st == Status::Playing)
                .unwrap_or(false);
            if playing {
                return Some(s);
            }
            first.get_or_insert(s);
        }
        first
    }

    fn rebind(&mut self, manager: &Manager, sink: &Sink) {
        let next = self.pick(manager);
        // Each call hands out a new object for the same session: compare app ids instead.
        let id_of = |s: &Option<Session>| s.as_ref().and_then(|s| s.SourceAppUserModelId().ok());
        if id_of(&next) == id_of(&self.session) {
            return;
        }
        self.unbind();
        self.id += 1;
        let Some(s) = next else {
            sink(Update::Session { session: self.id, source: None });
            return;
        };
        let tx = self.tx.clone();
        let props = s.MediaPropertiesChanged(&TypedEventHandler::new(move |_, _| {
            let _ = tx.send(Ev::Props);
            Ok(())
        }));
        let tx = self.tx.clone();
        let playback = s.PlaybackInfoChanged(&TypedEventHandler::new(move |_, _| {
            let _ = tx.send(Ev::Playback);
            Ok(())
        }));
        let tx = self.tx.clone();
        let timeline = s.TimelinePropertiesChanged(&TypedEventHandler::new(move |_, _| {
            let _ = tx.send(Ev::Timeline);
            Ok(())
        }));
        self.tokens = [props.unwrap_or(0), playback.unwrap_or(0), timeline.unwrap_or(0)];
        let id = s.SourceAppUserModelId().unwrap_or_default();
        sink(Update::Session { session: self.id, source: Some(display_name(&id)) });
        self.session = Some(s);
        let _ = self.props(sink);
        let _ = self.playback(sink);
        let _ = self.timeline(sink);
    }

    fn unbind(&mut self) {
        if let Some(s) = self.session.take() {
            let [a, b, c] = self.tokens;
            let _ = s.RemoveMediaPropertiesChanged(a);
            let _ = s.RemovePlaybackInfoChanged(b);
            let _ = s.RemoveTimelinePropertiesChanged(c);
        }
        self.tokens = [0; 3];
        self.track_key = None;
        self.art_done = false;
        self.last_playback = None;
        self.last_timeline = None;
        self.start_ticks = 0;
    }

    fn props(&mut self, sink: &Sink) -> windows::core::Result<()> {
        let Some(s) = &self.session else { return Ok(()) };
        let p = s.TryGetMediaPropertiesAsync()?.join()?;
        let title = p.Title()?.to_string_lossy();
        let mut artist = p.Artist()?.to_string_lossy();
        if artist.is_empty() {
            artist = p.AlbumArtist().map(|a| a.to_string_lossy()).unwrap_or_default();
        }
        let album = p.AlbumTitle().map(|a| a.to_string_lossy()).unwrap_or_default();
        let key = format!("{title}\u{1}{artist}\u{1}{album}");
        let new = self.track_key.as_deref() != Some(key.as_str());
        if new {
            self.track_key = Some(key);
            self.art_done = false;
            sink(Update::Track { session: self.id, title, artist, album });
        }
        // Once per track: apps often publish the title first and the thumbnail a moment later.
        if !self.art_done {
            let art = p
                .Thumbnail()
                .ok()
                .and_then(|t| read_thumbnail(&t).ok())
                .and_then(|bytes| meta::decode_artwork(&bytes, 256, false));
            self.art_done = art.is_some();
            if art.is_some() || new {
                sink(Update::Art { session: self.id, art });
            }
        }
        Ok(())
    }

    fn playback(&mut self, sink: &Sink) -> windows::core::Result<()> {
        let Some(s) = &self.session else { return Ok(()) };
        let info = s.GetPlaybackInfo()?;
        let playing = info.PlaybackStatus()? == Status::Playing;
        let c = info.Controls()?;
        let state = (
            playing,
            c.IsPreviousEnabled().unwrap_or(true),
            c.IsNextEnabled().unwrap_or(true),
            c.IsPlaybackPositionEnabled().unwrap_or(false),
        );
        if self.last_playback != Some(state) {
            self.last_playback = Some(state);
            let (playing, can_prev, can_next, can_seek) = state;
            sink(Update::Playback { session: self.id, playing, can_prev, can_next, can_seek });
        }
        Ok(())
    }

    fn timeline(&mut self, sink: &Sink) -> windows::core::Result<()> {
        let Some(s) = &self.session else { return Ok(()) };
        let t = s.GetTimelineProperties()?;
        let start = t.StartTime()?.Duration;
        let end = t.EndTime()?.Duration;
        let pos = t.Position()?.Duration;
        let updated = t.LastUpdatedTime()?.UniversalTime;
        let raw = (start, end, pos, updated);
        // Re-read after a play/pause, an unchanged timeline is stale: the UI already holds
        // the position where playback stopped.
        if self.last_timeline == Some(raw) {
            return Ok(());
        }
        self.last_timeline = Some(raw);
        self.start_ticks = start;
        let duration = (end > start).then(|| ticks(end - start));
        let position = ticks((pos - start).max(0));
        // The position was true at `updated` (100 ns ticks since 1601, UTC).
        let age = now_ticks() - updated;
        let now = Instant::now();
        let at = if updated > 0 && (0..864_000_000_000).contains(&age) {
            now.checked_sub(ticks(age)).unwrap_or(now)
        } else {
            now
        };
        sink(Update::Timeline { session: self.id, position, duration, at });
        Ok(())
    }

    fn command(&self, cmd: Cmd) {
        let Some(s) = &self.session else { return };
        // Fire and forget: the session reports the outcome through its events.
        let _ = match cmd {
            Cmd::Toggle => s.TryTogglePlayPauseAsync(),
            Cmd::Next => s.TrySkipNextAsync(),
            Cmd::Previous => s.TrySkipPreviousAsync(),
            Cmd::SeekTo(d) => s.TryChangePlaybackPositionAsync(self.start_ticks + (d.as_nanos() / 100) as i64),
        };
    }
}

fn ticks(t: i64) -> Duration {
    Duration::from_nanos(t.max(0) as u64 * 100)
}

fn now_ticks() -> i64 {
    const UNIX_TO_1601: i64 = 11_644_473_600 * 10_000_000;
    let since = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default();
    (since.as_nanos() / 100) as i64 + UNIX_TO_1601
}

fn read_thumbnail(r: &IRandomAccessStreamReference) -> windows::core::Result<Vec<u8>> {
    let stream = r.OpenReadAsync()?.join()?;
    let size = stream.Size()?;
    if size == 0 || size > 16 * 1024 * 1024 {
        return Err(windows::core::Error::empty());
    }
    let reader = DataReader::CreateDataReader(&stream)?;
    let load: IAsyncOperation<u32> = reader.LoadAsync(size as u32)?.cast()?;
    let read = load.join()? as usize;
    let mut buf = vec![0u8; read];
    reader.ReadBytes(&mut buf)?;
    Ok(buf)
}

/// The app's own name where Windows knows it (packaged apps), else a tidied app id.
fn display_name(id: &HSTRING) -> String {
    let known = windows::ApplicationModel::AppInfo::GetFromAppUserModelId(id)
        .and_then(|info| info.DisplayInfo())
        .and_then(|d| d.DisplayName())
        .map(|n| n.to_string_lossy())
        .unwrap_or_default();
    if !known.trim().is_empty() {
        return known;
    }
    pretty_name(&id.to_string_lossy())
}
