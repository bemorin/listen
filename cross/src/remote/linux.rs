//! Linux backend: MPRIS over D-Bus (the same libdbus souvlaki already links).
//!
//! One thread blocks on its D-Bus connection and wakes only when a signal arrives: a player
//! appearing or leaving (`NameOwnerChanged`), a player's state changing (`PropertiesChanged`),
//! or a seek (`Seeked`). It then reads the chosen player and reports what changed.

use super::{pretty_name, Cmd, Sink, Update};
use crate::meta;
use dbus::arg::{PropMap, RefArg};
use dbus::blocking::stdintf::org_freedesktop_dbus::Properties;
use dbus::blocking::Connection;
use dbus::message::MatchRule;
use dbus::Message;
use std::cell::RefCell;
use std::sync::atomic::{AtomicBool, AtomicU8, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

const PREFIX: &str = "org.mpris.MediaPlayer2.";
const PATH: &str = "/org/mpris/MediaPlayer2";
const ROOT: &str = "org.mpris.MediaPlayer2";
const PLAYER: &str = "org.mpris.MediaPlayer2.Player";
/// Listen's own MPRIS name (souvlaki's `dbus_name`): never mirrored.
const OWN: &str = "org.mpris.MediaPlayer2.listen";
const CALL_TIMEOUT: Duration = Duration::from_millis(500);

const RESCAN: u8 = 1;
const SEEKED: u8 = 2;

#[derive(Default)]
struct Shared {
    /// Bus name of the mirrored player and its current track id (for SetPosition).
    target: Option<String>,
    track_id: Option<String>,
    /// The worker's unique bus name, to wake it when the backend stops.
    worker: Option<String>,
}

pub struct Backend {
    shared: Arc<Mutex<Shared>>,
    quit: Arc<AtomicBool>,
    /// Commands go out on their own connection, opened on first use.
    conn: RefCell<Option<Connection>>,
}

impl Backend {
    pub fn start(sink: Sink) -> Option<Backend> {
        let shared = Arc::new(Mutex::new(Shared::default()));
        let quit = Arc::new(AtomicBool::new(false));
        let (s, q) = (shared.clone(), quit.clone());
        std::thread::Builder::new()
            .name("listen-remote".into())
            .stack_size(512 * 1024)
            .spawn(move || {
                let _ = run(&s, &q, &sink);
            })
            .ok()?;
        Some(Backend { shared, quit, conn: RefCell::new(None) })
    }

    fn post(&self, msg: Message) {
        let mut conn = self.conn.borrow_mut();
        if conn.is_none() {
            *conn = Connection::new_session().ok();
        }
        if let Some(c) = conn.as_ref() {
            use dbus::channel::Sender;
            let mut msg = msg;
            msg.set_no_reply(true);
            let _ = c.send(msg);
            c.channel().flush();
        }
    }

    pub fn send(&self, cmd: Cmd) {
        let (target, track) = {
            let s = self.shared.lock().unwrap();
            (s.target.clone(), s.track_id.clone())
        };
        let Some(target) = target else { return };
        let msg = match cmd {
            Cmd::Toggle => Message::new_method_call(&target, PATH, PLAYER, "PlayPause"),
            Cmd::Next => Message::new_method_call(&target, PATH, PLAYER, "Next"),
            Cmd::Previous => Message::new_method_call(&target, PATH, PLAYER, "Previous"),
            Cmd::SeekTo(d) => {
                let Some(track) = track.and_then(|t| dbus::Path::new(t).ok()) else { return };
                Message::new_method_call(&target, PATH, PLAYER, "SetPosition")
                    .map(|m| m.append2(track, d.as_micros() as i64))
            }
        };
        if let Ok(m) = msg {
            self.post(m);
        }
    }
}

impl Drop for Backend {
    fn drop(&mut self) {
        self.quit.store(true, Ordering::Release);
        // Wake the worker out of its wait.
        let worker = self.shared.lock().unwrap().worker.clone();
        if let Some(w) = worker {
            if let Ok(m) = Message::new_method_call(w, "/", "org.freedesktop.DBus.Peer", "Ping") {
                self.post(m);
            }
        }
    }
}

fn run(shared: &Arc<Mutex<Shared>>, quit: &AtomicBool, sink: &Sink) -> Result<(), dbus::Error> {
    let conn = Connection::new_session()?;
    shared.lock().unwrap().worker = Some(conn.unique_name().to_string());
    let dirty = Arc::new(AtomicU8::new(RESCAN));
    let d = dirty.clone();
    conn.add_match(
        MatchRule::new_signal("org.freedesktop.DBus", "NameOwnerChanged"),
        move |(name, _, _): (String, String, String), _: &Connection, _: &Message| {
            if name.starts_with(PREFIX) {
                d.fetch_or(RESCAN, Ordering::Relaxed);
            }
            true
        },
    )?;
    let d = dirty.clone();
    conn.add_match(
        MatchRule::new_signal("org.freedesktop.DBus.Properties", "PropertiesChanged").with_path(PATH),
        move |_: (), _: &Connection, _: &Message| {
            d.fetch_or(RESCAN, Ordering::Relaxed);
            true
        },
    )?;
    let d = dirty.clone();
    conn.add_match(
        MatchRule::new_signal(PLAYER, "Seeked").with_path(PATH),
        move |_: (), _: &Connection, _: &Message| {
            d.fetch_or(SEEKED, Ordering::Relaxed);
            true
        },
    )?;

    let mut mirror = Mirror::default();
    loop {
        let flags = dirty.swap(0, Ordering::Relaxed);
        if flags != 0 {
            mirror.refresh(&conn, shared, sink, flags & SEEKED != 0);
        }
        if quit.load(Ordering::Acquire) {
            break;
        }
        // Blocks until a message arrives (the hour is only a ceiling, not a poll).
        conn.process(Duration::from_secs(3600))?;
        while conn.process(Duration::ZERO)? {}
        if quit.load(Ordering::Acquire) {
            break;
        }
    }
    Ok(())
}

#[derive(Default)]
struct Mirror {
    target: Option<String>,
    id: u64,
    track_key: Option<String>,
    art_url: Option<String>,
    playback: Option<(bool, bool, bool, bool)>,
}

impl Mirror {
    fn refresh(&mut self, conn: &Connection, shared: &Mutex<Shared>, sink: &Sink, seeked: bool) {
        let next = self.pick(conn);
        if next != self.target {
            self.id += 1;
            self.target = next.clone();
            self.track_key = None;
            self.art_url = None;
            self.playback = None;
            let mut s = shared.lock().unwrap();
            s.target = next.clone();
            s.track_id = None;
            drop(s);
            let source = next.as_ref().map(|n| identity(conn, n));
            sink(Update::Session { session: self.id, source });
        }
        let Some(target) = self.target.clone() else { return };
        let proxy = conn.with_proxy(target.as_str(), PATH, CALL_TIMEOUT);
        let Ok(props): Result<PropMap, _> = proxy.get_all(PLAYER) else { return };

        let mut title = String::new();
        let mut artist = String::new();
        let mut album = String::new();
        let mut art_url = String::new();
        let mut track_id = None;
        let mut length = None;
        if let Some(meta) = props.get("Metadata") {
            if let Some(mut it) = meta.0.as_iter() {
                while let (Some(k), Some(v)) = (it.next(), it.next()) {
                    let Some(k) = k.as_str() else { continue };
                    match k {
                        "xesam:title" => title = strings(v).join(", "),
                        "xesam:artist" => artist = strings(v).join(", "),
                        "xesam:album" => album = strings(v).join(", "),
                        "mpris:artUrl" => art_url = strings(v).join(""),
                        "mpris:trackid" => track_id = v.as_str().map(str::to_string),
                        "mpris:length" => length = number(v),
                        _ => {}
                    }
                }
            }
        }
        shared.lock().unwrap().track_id = track_id;

        let key = format!("{title}\u{1}{artist}\u{1}{album}");
        let new_track = self.track_key.as_deref() != Some(key.as_str());
        if new_track {
            self.track_key = Some(key);
            sink(Update::Track { session: self.id, title, artist, album });
        }
        // Artwork once per track (or when the player changes its URL).
        if new_track || self.art_url.as_deref() != Some(art_url.as_str()) {
            let art = load_art(&art_url);
            self.art_url = Some(art_url);
            sink(Update::Art { session: self.id, art });
        }

        let flag = |k: &str, default: bool| props.get(k).map(|v| truthy(&*v.0)).unwrap_or(default);
        let playing = props.get("PlaybackStatus").and_then(|v| v.0.as_str()) == Some("Playing");
        let state = (playing, flag("CanGoPrevious", true), flag("CanGoNext", true), flag("CanSeek", false));
        let changed = self.playback != Some(state);
        if changed {
            self.playback = Some(state);
            let (playing, can_prev, can_next, can_seek) = state;
            sink(Update::Playback { session: self.id, playing, can_prev, can_next, can_seek });
        }
        if changed || new_track || seeked {
            // MPRIS never signals the position: it is read on each change, then extrapolated.
            let position = props.get("Position").and_then(|v| number(&*v.0)).unwrap_or(Duration::ZERO);
            sink(Update::Timeline { session: self.id, position, duration: length, at: Instant::now() });
        }
    }

    /// The mirrored player keeps its place while it plays; otherwise the first one playing,
    /// else the current one if it is still there, else any.
    fn pick(&self, conn: &Connection) -> Option<String> {
        let proxy = conn.with_proxy("org.freedesktop.DBus", "/org/freedesktop/DBus", CALL_TIMEOUT);
        let (names,): (Vec<String>,) = proxy.method_call("org.freedesktop.DBus", "ListNames", ()).ok()?;
        let players: Vec<String> = names
            .into_iter()
            .filter(|n| n.starts_with(PREFIX) && n != OWN && !n.starts_with(&format!("{OWN}.")))
            .collect();
        let playing = |n: &str| {
            conn.with_proxy(n, PATH, CALL_TIMEOUT)
                .get::<String>(PLAYER, "PlaybackStatus")
                .map(|s| s == "Playing")
                .unwrap_or(false)
        };
        if let Some(t) = &self.target {
            if players.contains(t) && playing(t) {
                return Some(t.clone());
            }
        }
        if let Some(p) = players.iter().find(|n| playing(n)) {
            return Some(p.clone());
        }
        if let Some(t) = &self.target {
            if players.contains(t) {
                return Some(t.clone());
            }
        }
        players.into_iter().next()
    }
}

fn identity(conn: &Connection, name: &str) -> String {
    conn.with_proxy(name, PATH, CALL_TIMEOUT)
        .get::<String>(ROOT, "Identity")
        .ok()
        .filter(|s| !s.trim().is_empty())
        .unwrap_or_else(|| {
            let short = name.trim_start_matches(PREFIX);
            pretty_name(short.split('.').next().unwrap_or(short))
        })
}

/// Strings in a value, whether a string, a variant or an array of strings.
fn strings(v: &dyn RefArg) -> Vec<String> {
    if let Some(s) = v.as_str() {
        return vec![s.to_string()];
    }
    match v.as_iter() {
        Some(it) => it.flat_map(strings).collect(),
        None => Vec::new(),
    }
}

/// Microseconds (signed or unsigned, possibly in a variant).
fn number(v: &dyn RefArg) -> Option<Duration> {
    let n = v.as_i64().or_else(|| v.as_u64().map(|u| u as i64))?;
    (n > 0).then(|| Duration::from_micros(n as u64))
}

fn truthy(v: &dyn RefArg) -> bool {
    v.as_u64().map(|x| x != 0).or_else(|| v.as_i64().map(|x| x != 0)).unwrap_or(false)
}

/// Local artwork only (`file://`). Players that publish `https://` artwork (Spotify,
/// browsers) show none: fetching it would mean an HTTP and TLS stack in Listen.
fn load_art(url: &str) -> Option<meta::Artwork> {
    let path = url.strip_prefix("file://")?;
    let bytes = std::fs::read(percent_decode(path)).ok()?;
    if bytes.len() > 16 * 1024 * 1024 {
        return None;
    }
    meta::decode_artwork(&bytes, 256, false)
}

fn percent_decode(s: &str) -> String {
    let b = s.as_bytes();
    let mut out = Vec::with_capacity(b.len());
    let mut i = 0;
    while i < b.len() {
        if b[i] == b'%' && i + 2 < b.len() {
            let hex = |c: u8| (c as char).to_digit(16);
            if let (Some(h), Some(l)) = (hex(b[i + 1]), hex(b[i + 2])) {
                out.push((h * 16 + l) as u8);
                i += 3;
                continue;
            }
        }
        out.push(b[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}
