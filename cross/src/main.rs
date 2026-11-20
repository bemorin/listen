#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod audio;
mod dialogs;
mod media;
mod meta;
mod platform;
mod state;

use platform::Rect;
use slint::winit_030::{winit, EventResult, WinitWindowAccessor};
use slint::{ComponentHandle, Model, ModelRc, SharedString, VecModel};
use state::{AppState, Repeat};
use std::cell::RefCell;
use std::collections::HashMap;
use std::path::PathBuf;
use std::rc::Rc;
use std::sync::mpsc;
use std::time::{Duration, Instant};

slint::include_modules!();

const DOCK_TOLERANCE: f64 = 16.0;
const MODE_MORPH: Duration = Duration::from_millis(420);
const QUEUE_REVEAL: Duration = Duration::from_millis(340);
// Mode change choreography: the content dissolves, the window morphs while empty, and the
// new layout fades in as the morph settles, so no intermediate layout ever flashes by.
const FADE_OUT: f64 = 0.09;
const MORPH_DELAY: f64 = 0.04;
const FADE_IN: f64 = 0.17;
const FADE_IN_AT: f64 = 0.5; // fraction of the morph when the new layout starts to appear

pub fn is_french() -> bool {
    sys_locale::get_locale()
        .map(|l| l.to_ascii_lowercase().starts_with("fr"))
        .unwrap_or(false)
}

fn ease_out(t: f64) -> f64 {
    let t = t.clamp(0.0, 1.0);
    1.0 - (1.0 - t).powi(3)
}

/// Critically damped spring, as in the Windows app (WindowMorph.Ease).
fn ease(t: f64) -> f64 {
    const K: f64 = 7.0;
    if t <= 0.0 {
        return 0.0;
    }
    if t >= 1.0 {
        return 1.0;
    }
    let norm = 1.0 - (1.0 + K) * (-K).exp();
    let wt = K * t;
    (1.0 - (1.0 + wt) * (-wt).exp()) / norm
}

enum MorphKind {
    Mode,
    /// The drawer follows the window: its visible height is the growth past `base`.
    QueueOpen {
        base: f64,
    },
    QueueClose {
        target: f64,
    },
}

struct Morph {
    from: Rect,
    to: Rect,
    start: Instant,
    duration: Duration,
    kind: MorphKind,
    /// Dissolve the content while the window morphs (mode changes).
    fade: bool,
}

enum MetaJob {
    Current(PathBuf, u64),
    Duration(PathBuf),
}

enum MetaResult {
    Current(u64, Option<meta::TrackMeta>),
    Art(u64, Option<meta::Artwork>),
    Duration(PathBuf, Option<Duration>, Option<String>),
}

enum Launch {
    Play,
    Enqueue,
}

struct App {
    ui: slint::Weak<AppWindow>,
    state: AppState,
    engine: audio::Engine,
    media: Option<media::Media>,
    queue_model: Rc<VecModel<QueueItem>>,
    durations: HashMap<PathBuf, Option<Duration>>,
    titles: HashMap<PathBuf, String>,
    meta_tx: mpsc::Sender<MetaJob>,
    meta_rx: mpsc::Receiver<MetaResult>,
    generation: u64,
    current_meta: Option<meta::TrackMeta>,
    cover_file: Option<PathBuf>,

    // Window geometry, in logical pixels unless noted.
    normal_size: (f64, f64),
    height_before_queue: Option<f64>,
    placed_from: Option<Rect>,
    placed_to: Option<Rect>,
    morph: Option<Morph>,
    morph_timer: slint::Timer,
    progress_timer: slint::Timer,
    progress_interval: std::cell::Cell<Duration>,
    clock_anchor: std::cell::Cell<Option<(f64, Instant)>>,
    wave_epoch: Instant,
    resize_settle: slint::Timer,
    resize_start: Option<(f64, f64)>,
    last_size: (f64, f64),
    pending_drop: Vec<PathBuf>,
    hovered_files: Vec<PathBuf>,
    started: bool,
    snapping: bool,
}

thread_local! {
    static APP: RefCell<Option<Rc<RefCell<App>>>> = const { RefCell::new(None) };
}

fn with_app(f: impl FnOnce(&mut App)) {
    APP.with(|a| {
        if let Some(app) = a.borrow().as_ref() {
            if let Ok(mut app) = app.try_borrow_mut() {
                f(&mut app);
            }
        }
    });
}

impl App {
    fn ui(&self) -> AppWindow {
        self.ui.upgrade().expect("window alive")
    }

    fn scale(&self) -> f64 {
        self.ui().window().scale_factor() as f64
    }

    fn with_winit<T>(&self, f: impl FnOnce(&winit::window::Window) -> T) -> Option<T> {
        self.ui().window().with_winit_window(f)
    }

    fn save(&mut self) {
        self.state.show_remaining = self.ui().get_show_remaining();
        self.state.save();
    }

    // ----- Queue and playback ------------------------------------------------------

    fn current_path(&self) -> Option<&PathBuf> {
        self.state.current.and_then(|i| self.state.queue.get(i))
    }

    fn refresh_queue(&mut self) {
        let items: Vec<QueueItem> = self
            .state
            .queue
            .iter()
            .map(|p| QueueItem {
                title: match self.titles.get(p) {
                    Some(t) => t.as_str().into(),
                    None => meta::display_names(p, None).0.into(),
                },
                duration: match self.durations.get(p) {
                    Some(Some(d)) => meta::format_time(*d).into(),
                    _ => SharedString::new(),
                },
                ext: meta::extension_label(p).into(),
            })
            .collect();
        self.queue_model.set_vec(items);
        self.update_queue_summary();
        for p in &self.state.queue {
            if !self.durations.contains_key(p) {
                self.durations.insert(p.clone(), None);
                let _ = self.meta_tx.send(MetaJob::Duration(p.clone()));
            }
        }
        let ui = self.ui();
        ui.set_current_index(self.state.current.map(|i| i as i32).unwrap_or(-1));
        let sel = ui.get_selected_index();
        if sel >= self.state.queue.len() as i32 {
            ui.set_selected_index(self.state.queue.len() as i32 - 1);
        }
    }

    /// "7 tracks · 24 min" (the length once every duration is known).
    fn update_queue_summary(&self) {
        let n = self.state.queue.len();
        let fr = is_french();
        let count = match (n, fr) {
            (0, true) => "Aucun titre".to_string(),
            (0, false) => "No tracks".to_string(),
            (1, true) => "1 titre".to_string(),
            (1, false) => "1 track".to_string(),
            (n, true) => format!("{n} titres"),
            (n, false) => format!("{n} tracks"),
        };
        let mut total = Duration::ZERO;
        let mut known = n > 0;
        for p in &self.state.queue {
            match self.durations.get(p) {
                Some(Some(d)) => total += *d,
                _ => known = false,
            }
        }
        let summary = if known {
            let mins = (total.as_secs() + 59) / 60;
            let length = if mins >= 60 { format!("{} h {} min", mins / 60, mins % 60) } else { format!("{mins} min") };
            format!("{count} · {length}")
        } else {
            count
        };
        self.ui().set_queue_summary(summary.into());
    }

    fn show_track(&mut self) {
        let ui = self.ui();
        self.generation += 1;
        self.current_meta = None;
        // The previous artwork stays until the new track's arrives (or is known to be absent),
        // so a track change cross-fades instead of flashing the placeholder.
        match self.current_path().cloned() {
            None => {
                ui.set_has_artwork(false);
                ui.set_has_track(false);
                ui.set_track_title("".into());
                ui.set_track_subtitle("".into());
                ui.set_duration_secs(0.0);
                ui.set_position_secs(0.0);
                ui.set_can_seek(false);
                if let Some(m) = &mut self.media {
                    m.set_track("", "", "", None, None);
                    m.set_playback(false, false, Duration::ZERO, true);
                }
            }
            Some(path) => {
                let (title, subtitle) = meta::display_names(&path, None);
                ui.set_has_track(true);
                ui.set_track_title(title.as_str().into());
                ui.set_track_subtitle(subtitle.as_str().into());
                if let Some(Some(d)) = self.durations.get(&path) {
                    ui.set_duration_secs(d.as_secs_f32());
                }
                let _ = self.meta_tx.send(MetaJob::Current(path, self.generation));
                if let Some(m) = &mut self.media {
                    m.set_track(&title, &subtitle, "", None, None);
                }
            }
        }
    }

    fn play_index(&mut self, index: usize) {
        self.load_index(index, true);
    }

    /// Loads queue item `index`; missing files are dropped from the queue on the way.
    fn load_index(&mut self, mut index: usize, autoplay: bool) {
        loop {
            if index >= self.state.queue.len() {
                return;
            }
            if self.state.queue[index].is_file() {
                break;
            }
            self.state.queue.remove(index);
            if self.state.queue.is_empty() {
                self.engine.unload();
                self.state.current = None;
                self.show_track();
                self.refresh_queue();
                self.sync_playback();
                self.save();
                return;
            }
            index = index.min(self.state.queue.len() - 1);
        }
        self.state.current = Some(index);
        let path = self.state.queue[index].clone();
        self.show_track();
        let ui = self.ui();
        match self.engine.load(&path) {
            Ok(d) => {
                ui.set_can_seek(true);
                if let Some(d) = d {
                    ui.set_duration_secs(d.as_secs_f32());
                }
                if autoplay {
                    let _ = self.engine.play();
                }
            }
            Err(_) => {
                ui.set_can_seek(false);
                ui.set_track_subtitle(
                    if is_french() {
                        "Format ou codec non pris en charge"
                    } else {
                        "Unsupported format or codec"
                    }
                    .into(),
                );
            }
        }
        ui.set_position_secs(0.0);
        self.refresh_queue();
        ui.set_selected_index(index as i32);
        self.sync_playback();
        self.save();
    }

    fn toggle_play(&mut self) {
        if self.state.queue.is_empty() {
            self.add_files();
            return;
        }
        if self.engine.is_playing() {
            self.engine.pause();
        } else if self.engine.is_loaded() {
            if self.at_end() {
                self.engine.seek(Duration::ZERO);
            }
            let _ = self.engine.play();
        } else {
            let i = self
                .state
                .current
                .unwrap_or(0)
                .min(self.state.queue.len() - 1);
            self.play_index(i);
            return;
        }
        self.sync_playback();
    }

    fn at_end(&self) -> bool {
        match self.engine.duration() {
            Some(d) => self.engine.position() + Duration::from_millis(400) >= d,
            None => false,
        }
    }

    fn previous(&mut self) {
        if self.state.queue.is_empty() {
            return;
        }
        if self.engine.is_loaded() && self.engine.position() > Duration::from_secs(4) {
            self.engine.seek(Duration::ZERO);
            self.tick_position();
            return;
        }
        // Before the first track, wrap to the last one (a single track simply restarts).
        let cur = self.state.current.unwrap_or(0);
        let target = if cur == 0 {
            self.state.queue.len() - 1
        } else {
            cur - 1
        };
        self.play_index(target);
    }

    fn next(&mut self, from_ended: bool) {
        let len = self.state.queue.len();
        if len == 0 {
            return;
        }
        let target = self.state.current.map(|c| c + 1).unwrap_or(0);
        if target < len {
            self.play_index(target);
            return;
        }
        if !from_ended {
            // A deliberate skip past the last track wraps to the first, mirroring Previous.
            if len > 1 {
                self.play_index(0);
            }
            return;
        }
        if self.state.repeat == Repeat::Queue {
            self.play_index(0);
            return;
        }
        // Natural end of the queue: stop on the last frame.
        self.engine.pause();
        self.sync_playback();
        let ui = self.ui();
        ui.set_position_secs(ui.get_duration_secs());
    }

    fn cycle_repeat(&mut self) {
        self.state.repeat = self.state.repeat.next();
        self.ui().set_repeat_mode(self.state.repeat as i32);
        self.save();
    }

    fn seek_fraction(&mut self, f: f32) {
        if !self.engine.is_loaded() {
            // The restored track is shown but not opened yet: open it, paused, then seek.
            match self.state.current {
                Some(i) => self.load_index(i, false),
                None => return,
            }
        }
        if let Some(d) = self.engine.duration() {
            self.engine.seek(d.mul_f32(f.clamp(0.0, 1.0)));
            self.tick_position();
            self.sync_playback_force();
        }
    }

    fn nudge(&mut self, secs: f64) {
        if !self.engine.is_loaded() {
            return;
        }
        let pos = self.engine.position().as_secs_f64() + secs;
        self.engine.seek(Duration::from_secs_f64(pos.max(0.0)));
        self.tick_position();
        self.sync_playback_force();
    }

    fn set_volume(&mut self, v: f32) {
        let v = v.clamp(0.0, 1.0);
        self.state.volume = v;
        if v > 0.001 {
            self.state.last_audible_volume = v;
        }
        self.engine.set_volume(v);
        self.ui().set_volume(v);
    }

    fn toggle_mute(&mut self) {
        if self.state.volume > 0.001 {
            self.state.last_audible_volume = self.state.volume;
            self.set_volume(0.0);
        } else {
            let v = if self.state.last_audible_volume > 0.001 {
                self.state.last_audible_volume
            } else {
                0.82
            };
            self.set_volume(v);
        }
        self.save();
    }

    fn remove_index(&mut self, index: usize) {
        if index >= self.state.queue.len() {
            return;
        }
        let removing_current = Some(index) == self.state.current;
        self.state.queue.remove(index);
        if self.state.queue.is_empty() {
            self.engine.unload();
            self.state.current = None;
            self.show_track();
        } else if removing_current {
            let was_playing = self.engine.is_playing();
            let i = index.min(self.state.queue.len() - 1);
            self.load_index(i, was_playing);
        } else if let Some(c) = self.state.current {
            if index < c {
                self.state.current = Some(c - 1);
            }
        }
        self.refresh_queue();
        let ui = self.ui();
        let sel = (index as i32).min(self.state.queue.len() as i32 - 1);
        ui.set_selected_index(sel);
        self.sync_playback();
        self.save();
    }

    fn clear_queue(&mut self) {
        self.state.queue.clear();
        self.state.current = None;
        self.engine.unload();
        self.show_track();
        self.refresh_queue();
        self.ui().set_selected_index(-1);
        self.sync_playback();
        self.save();
    }

    fn add_files(&mut self) {
        let title = if is_french() {
            "Ajouter des fichiers audio"
        } else {
            "Add audio files"
        };
        let files = dialogs::pick_audio_files(title);
        if !files.is_empty() {
            self.apply_launch(files, Launch::Enqueue, true);
        }
    }

    /// Adds files (dialog, drop, command line). Enqueue starts playback when nothing was
    /// queued before; Play replaces the queue.
    fn apply_launch(&mut self, files: Vec<PathBuf>, mode: Launch, play_if_idle: bool) {
        let files: Vec<PathBuf> = files
            .into_iter()
            .filter(|p| state::is_supported(p) && p.is_file())
            .collect();
        if files.is_empty() {
            return;
        }
        match mode {
            Launch::Play => {
                self.state.queue = files;
                self.play_index(0);
            }
            Launch::Enqueue => {
                let first_new = self.state.queue.len();
                self.state.queue.extend(files);
                if self.state.current.is_none() {
                    if play_if_idle {
                        self.play_index(first_new);
                    } else {
                        self.state.current = Some(first_new);
                        self.show_track();
                    }
                }
                self.refresh_queue();
            }
        }
        self.save();
    }

    fn sync_playback(&mut self) {
        let playing = self.engine.is_playing();
        let ui = self.ui();
        ui.set_playing(playing);
        self.retime_progress();
        let has = self.state.current.is_some();
        let pos = self.engine.position();
        if let Some(m) = &mut self.media {
            m.set_playback(playing, has, pos, false);
        }
    }

    fn sync_playback_force(&mut self) {
        let playing = self.engine.is_playing();
        let has = self.state.current.is_some();
        let pos = self.engine.position();
        if let Some(m) = &mut self.media {
            m.set_playback(playing, has, pos, true);
        }
    }

    /// Publishes the playback position. A local clock, re-anchored on the audio clock when
    /// they drift apart, keeps the motion even between the device's buffer-sized steps.
    fn tick_position(&self) {
        if !self.engine.is_loaded() {
            return;
        }
        let ui = self.ui();
        let audio = self.engine.position().as_secs_f64();
        let playing = self.engine.is_playing();
        let pos = match self.clock_anchor.get() {
            Some((at, start)) if playing => {
                let predicted = at + start.elapsed().as_secs_f64();
                if (predicted - audio).abs() > 0.08 {
                    self.clock_anchor.set(Some((audio, Instant::now())));
                    audio
                } else {
                    predicted
                }
            }
            _ => {
                self.clock_anchor.set(playing.then(|| (audio, Instant::now())));
                audio
            }
        } as f32;
        if pos.floor() != ui.get_position_secs().floor() || !playing {
            ui.set_position_secs(pos);
        }
        let dur = ui.get_duration_secs();
        if dur > 0.0 {
            ui.set_progress((pos / dur).clamp(0.0, 1.0));
        }
        if playing && ui.get_queue_shown() {
            ui.set_wave(self.wave_epoch.elapsed().as_secs_f32());
        }
    }

    /// The clock runs only while playing, at the pace the progress bar needs to glide: about a
    /// sixth of a pixel per frame, between 10 and 30 frames a second (16 with the queue's bars
    /// moving). A song of a few minutes needs about 11 redraws a second.
    fn retime_progress(&self) {
        if !self.engine.is_playing() {
            self.progress_timer.stop();
            self.progress_interval.set(Duration::ZERO);
            self.clock_anchor.set(None);
            self.tick_position();
            return;
        }
        let ui = self.ui();
        let dur = ui.get_duration_secs().max(0.0) as f64;
        let width = ui.window().size().width as f64;
        let scale = self.scale();
        let reserved = if ui.get_mini() { 110.0 } else { 160.0 };
        let bar_px = (width - reserved * scale).max(40.0);
        let mut ms = if dur > 0.0 { dur * 1000.0 / (bar_px * 6.0) } else { 100.0 };
        if ui.get_queue_shown() {
            ms = ms.min(62.0);
        }
        let interval = Duration::from_millis(ms.clamp(33.0, 100.0) as u64);
        if self.progress_interval.get() != interval {
            self.progress_interval.set(interval);
            self.progress_timer
                .start(slint::TimerMode::Repeated, interval, || with_app(|a| a.tick_position()));
        }
        self.tick_position();
    }

    fn tick(&mut self) {
        if self.engine.finished() {
            if self.state.repeat == Repeat::Track {
                if let Some(i) = self.state.current {
                    self.play_index(i);
                }
            } else {
                self.next(true);
            }
        }
        self.engine.tick();
    }

    fn drain_meta(&mut self) {
        self.drain_meta_results();
        self.retime_progress();
    }

    fn drain_meta_results(&mut self) {
        while let Ok(r) = self.meta_rx.try_recv() {
            match r {
                MetaResult::Duration(p, d, title) => {
                    self.durations.insert(p.clone(), d);
                    self.update_queue_summary();
                    if let Some(t) = &title {
                        self.titles.insert(p.clone(), t.clone());
                    }
                    for (i, q) in self.state.queue.iter().enumerate() {
                        if *q != p {
                            continue;
                        }
                        if let Some(mut item) = self.queue_model.row_data(i) {
                            if let Some(d) = d {
                                item.duration = meta::format_time(d).into();
                            }
                            if let Some(t) = &title {
                                item.title = t.as_str().into();
                            }
                            self.queue_model.set_row_data(i, item);
                        }
                    }
                    if let Some(d) = d {
                        if self.current_path() == Some(&p) {
                            self.engine.set_duration_hint(d);
                            let ui = self.ui();
                            if ui.get_duration_secs() <= 0.0 {
                                ui.set_duration_secs(d.as_secs_f32());
                            }
                        }
                    }
                }
                MetaResult::Art(gen, art) => {
                    if gen != self.generation {
                        continue;
                    }
                    let ui = self.ui();
                    let mut cover_url = None;
                    if let Some(a) = &art {
                        let buf = slint::SharedPixelBuffer::<slint::Rgba8Pixel>::clone_from_slice(
                            &a.rgba, a.size, a.size,
                        );
                        ui.set_artwork(slint::Image::from_rgba8(buf));
                        ui.set_tint(slint::Color::from_rgb_u8(a.tint[0], a.tint[1], a.tint[2]));
                        let [ga, gb] = a.glow;
                        ui.set_tint_a(slint::Color::from_rgb_u8(ga[0], ga[1], ga[2]));
                        ui.set_tint_b(slint::Color::from_rgb_u8(gb[0], gb[1], gb[2]));
                        ui.set_has_artwork(true);
                        cover_url = self.write_cover(a);
                    } else {
                        ui.set_has_artwork(false);
                    }
                    if let (Some(url), Some(path)) = (cover_url, self.current_path().cloned()) {
                        let (title, subtitle) = meta::display_names(&path, self.current_meta.as_ref());
                        let album = self.current_meta.as_ref().and_then(|m| m.album.clone()).unwrap_or_default();
                        let duration = self.engine.duration();
                        if let Some(media) = &mut self.media {
                            media.set_track(&title, &subtitle, &album, duration, Some(&url));
                        }
                    }
                }
                MetaResult::Current(gen, m) => {
                    if gen != self.generation {
                        continue;
                    }
                    let Some(path) = self.current_path().cloned() else {
                        continue;
                    };
                    let ui = self.ui();
                    let (title, subtitle) = meta::display_names(&path, m.as_ref());
                    ui.set_track_title(title.as_str().into());
                    if ui.get_can_seek() || !self.engine.is_loaded() {
                        ui.set_track_subtitle(subtitle.as_str().into());
                    }
                    if let Some(d) = m.as_ref().and_then(|m| m.duration) {
                        self.engine.set_duration_hint(d);
                        if ui.get_duration_secs() <= 0.0 {
                            ui.set_duration_secs(d.as_secs_f32());
                        }
                    }
                    let album = m.as_ref().and_then(|m| m.album.clone()).unwrap_or_default();
                    let duration = self.engine.duration();
                    if let Some(media) = &mut self.media {
                        media.set_track(&title, &subtitle, &album, duration, None);
                    }
                    self.current_meta = m;
                }
            }
        }
    }

    /// The OS media panel reads artwork from a file.
    fn write_cover(&mut self, a: &meta::Artwork) -> Option<String> {
        let path = std::env::temp_dir().join(format!("listen-cover-{}.png", std::process::id()));
        image::save_buffer(
            &path,
            &a.rgba,
            a.size,
            a.size,
            image::ExtendedColorType::Rgba8,
        )
        .ok()?;
        self.cover_file = Some(path.clone());
        Some(format!("file://{}", path.display()))
    }

    // ----- Window modes ------------------------------------------------------------

    fn bounds(&self) -> Option<Rect> {
        self.with_winit(platform::bounds).flatten()
    }

    fn work_area(&self) -> Option<Rect> {
        self.with_winit(platform::work_area)
    }

    fn set_bounds(&self, r: Rect) {
        self.with_winit(|w| platform::set_bounds(w, r));
    }

    /// Target bounds (physical) for a resize to `w`×`h`. Undoing the previous resize returns
    /// exactly to where it started; otherwise the window stays fully on screen.
    fn plan_bounds(&mut self, current: Rect, w: f64, h: f64) -> Rect {
        let scale = self.scale();
        let target = match (self.placed_from, self.placed_to) {
            (Some(from), Some(to))
                if current.roughly_eq(&to)
                    && (from.w - w).abs() <= 2.0
                    && (from.h - h).abs() <= 2.0 =>
            {
                from
            }
            _ => {
                let area = self.work_area().unwrap_or(Rect {
                    x: -32000.0,
                    y: -32000.0,
                    w: 64000.0,
                    h: 64000.0,
                });
                platform::place(current, w, h, area, DOCK_TOLERANCE * scale)
            }
        };
        self.placed_from = Some(current);
        self.placed_to = Some(target);
        target
    }

    fn mini_min_width(&self) -> f64 {
        self.ui().get_mini_min_width() as f64
    }

    fn compact_height(&self) -> f64 {
        self.ui().get_compact_height() as f64
    }

    fn apply_constraints(&self) {
        let ui = self.ui();
        if self.morph.is_some() {
            ui.set_min_w(1.0);
            ui.set_min_h(1.0);
            return;
        }
        let mini = ui.get_mini();
        ui.set_min_w(if mini { ui.get_mini_min_width() } else { 360.0 });
        ui.set_min_h(ui.get_mini_height());
    }

    fn toggle_mini(&mut self) {
        let mini = self.ui().get_mini();
        self.set_mini(!mini, true);
    }

    fn set_mini(&mut self, mini: bool, animate: bool) {
        self.finish_morph();
        let ui = self.ui();
        if ui.get_mini() == mini {
            return;
        }
        let Some(current) = self.bounds() else { return };
        let scale = self.scale();
        if mini {
            self.normal_size = (current.w / scale, current.h / scale);
        } else {
            self.state.mini_width = (current.w / scale) as f32;
        }
        self.state.mini = mini;
        let (w, h) = if mini {
            (
                (self.state.mini_width as f64).max(self.mini_min_width()),
                ui.get_mini_height() as f64,
            )
        } else {
            (
                self.normal_size.0.max(360.0),
                self.normal_size.1.max(self.compact_height()),
            )
        };
        let target = self.plan_bounds(current, w * scale, h * scale);
        self.start_morph(
            current,
            target,
            if animate { MODE_MORPH } else { Duration::ZERO },
            MorphKind::Mode,
        );
        self.save();
    }

    fn toggle_queue(&mut self) {
        let visible = !self.state.queue_visible;
        self.set_queue_visible(visible, true);
    }

    /// Window height (logical) for the queue state: the queue adds its own height below the
    /// player instead of squeezing it, and closing restores the height from before.
    fn queue_window_height(&mut self, visible: bool, height: f64) -> f64 {
        let panel = self.ui().get_queue_height() as f64;
        if visible {
            if self.height_before_queue.is_some() {
                return height;
            }
            self.height_before_queue = Some(height);
            height + panel
        } else {
            self.height_before_queue.take().unwrap_or(height)
        }
    }

    fn set_queue_visible(&mut self, visible: bool, animate: bool) {
        self.finish_morph();
        self.state.queue_visible = visible;
        let ui = self.ui();
        ui.set_queue_open(visible);
        let panel = ui.get_queue_height() as f64;
        let current = self.bounds();
        match current {
            Some(current) if self.started && !ui.get_mini() => {
                let scale = self.scale();
                let h = self.queue_window_height(visible, current.h / scale) * scale;
                let target = if (h - current.h).abs() < 1.0 {
                    current
                } else {
                    self.plan_bounds(current, current.w, h)
                };
                let change = (target.h - current.h).abs();
                let drawer =
                    (change - panel * scale).abs() <= 1.5 && (target.w - current.w).abs() < 1.0;
                let kind = match (visible, drawer) {
                    (true, true) => MorphKind::QueueOpen { base: current.h },
                    (false, true) => MorphKind::QueueClose { target: target.h },
                    _ => {
                        ui.set_queue_extra(if visible { panel as f32 } else { 0.0 });
                        MorphKind::Mode
                    }
                };
                if visible && drawer {
                    ui.set_queue_extra(0.0);
                }
                self.start_morph(
                    current,
                    target,
                    if animate {
                        QUEUE_REVEAL
                    } else {
                        Duration::ZERO
                    },
                    kind,
                );
            }
            _ => {
                // The full player is not on screen: update its layout and remembered height.
                ui.set_queue_extra(if visible { panel as f32 } else { 0.0 });
                self.normal_size.1 = self.queue_window_height(visible, self.normal_size.1);
            }
        }
        self.save();
    }

    fn start_morph(&mut self, from: Rect, to: Rect, duration: Duration, kind: MorphKind) {
        #[cfg(debug_assertions)]
        eprintln!(
            "morph {from:?} -> {to:?} area={:?} scale={}",
            self.work_area(),
            self.scale()
        );
        let fade = matches!(kind, MorphKind::Mode) && !duration.is_zero() && !self.snapping;
        self.morph = Some(Morph {
            from,
            to,
            start: Instant::now(),
            duration,
            kind,
            fade,
        });
        self.ui().set_morphing(true);
        self.apply_constraints();
        if duration.is_zero() {
            self.step_morph();
            return;
        }
        self.step_morph();
        let weak = self.ui.clone();
        self.morph_timer.start(
            slint::TimerMode::Repeated,
            Duration::from_millis(7),
            move || {
                let _ = &weak;
                with_app(|app| app.step_morph());
            },
        );
    }

    fn step_morph(&mut self) {
        let Some(m) = &self.morph else { return };
        let elapsed = m.start.elapsed().as_secs_f64();
        let dur = m.duration.as_secs_f64();
        let (t, done, fade) = if dur == 0.0 {
            (1.0, true, 1.0)
        } else if m.fade {
            let t = ((elapsed - MORPH_DELAY) / dur).clamp(0.0, 1.0);
            let fade_in_at = MORPH_DELAY + dur * FADE_IN_AT;
            let fade = if elapsed < FADE_OUT {
                1.0 - ease_out(elapsed / FADE_OUT)
            } else if elapsed < fade_in_at {
                0.0
            } else {
                ease_out(((elapsed - fade_in_at) / FADE_IN).min(1.0))
            };
            let done = elapsed >= (MORPH_DELAY + dur).max(fade_in_at + FADE_IN);
            (t, done, fade)
        } else {
            let t = (elapsed / dur).min(1.0);
            (t, t >= 1.0, 1.0)
        };
        let e = ease(t);
        let r = m.from.lerp(&m.to, e);
        let scale = self.scale();
        let ui = self.ui();
        match m.kind {
            MorphKind::QueueOpen { base } => {
                ui.set_queue_extra(((r.h - base) / scale).max(0.0) as f32)
            }
            MorphKind::QueueClose { target } => {
                ui.set_queue_extra(((r.h - target) / scale).max(0.0) as f32)
            }
            MorphKind::Mode => {}
        }
        ui.set_content_fade(fade as f32);
        self.set_bounds(r);
        if done {
            self.finish_morph();
        }
    }

    fn finish_morph(&mut self) {
        let Some(m) = self.morph.take() else { return };
        self.morph_timer.stop();
        self.set_bounds(m.to);
        let ui = self.ui();
        let panel = ui.get_queue_height();
        match m.kind {
            MorphKind::QueueOpen { .. } => ui.set_queue_extra(panel),
            MorphKind::QueueClose { .. } => ui.set_queue_extra(0.0),
            MorphKind::Mode => {}
        }
        ui.set_morphing(false);
        ui.set_content_fade(1.0);
        self.last_size = (m.to.w / self.scale(), m.to.h / self.scale());
        self.apply_constraints();
        self.retime_progress();
        if self.snapping {
            // A resize settled onto a resting height: now adopt the mode it landed in.
            self.sync_mode_with_size();
        } else {
            self.resize_start = None;
        }
    }

    /// Heights between two layouts are transitions, not resting places: released there, the
    /// window settles on the nearer layout (mini, compact, title, artwork).
    fn snap_height(&self) -> Option<f64> {
        let ui = self.ui();
        let q = if ui.get_queue_shown() {
            ui.get_queue_extra() as f64
        } else {
            0.0
        };
        let c = self.last_size.1 - q;
        let mini = ui.get_mini_height() as f64;
        let compact = ui.get_compact_height() as f64;
        let hero = ui.get_hero_min_height() as f64;
        let art = hero + 12.0 + 32.0;
        let bands = [(mini, compact), (compact, hero), (hero, art)];
        for (lo, hi) in bands {
            if c > lo + 0.5 && c < hi - 0.5 {
                let to = if c - lo < hi - c { lo } else { hi };
                return Some(to + q);
            }
        }
        None
    }

    /// Window resized (by the user, the OS or a morph).
    fn on_resized(&mut self, w: f64, h: f64) {
        if self.morph.is_some() || !self.started {
            self.last_size = (w, h);
            return;
        }
        if self.resize_start.is_none() {
            self.resize_start = Some(self.last_size);
        }
        self.last_size = (w, h);
        // Constraints follow the layout the window is in right now (mini or full).
        self.apply_constraints();
        self.retime_progress();
        self.resize_settle.start(
            slint::TimerMode::SingleShot,
            Duration::from_millis(280),
            || {
                with_app(|app| app.sync_mode_with_size());
            },
        );
    }

    /// After a resize, the mode follows the layout it was left in: shrunk into the mini layout
    /// it becomes the mini player (Expand returns to the size from before), pulled open from
    /// the mini player it becomes the full player.
    fn sync_mode_with_size(&mut self) {
        if platform::primary_button_down() {
            // Still resizing (the OS reports no end of a size drag): look again shortly.
            self.resize_settle.start(
                slint::TimerMode::SingleShot,
                Duration::from_millis(120),
                || {
                    with_app(|app| app.sync_mode_with_size());
                },
            );
            return;
        }
        if self.resize_start.is_none() {
            return;
        }
        if !self.snapping {
            if let Some(h) = self.snap_height() {
                if let Some(current) = self.bounds() {
                    let scale = self.scale();
                    let area = self.work_area().unwrap_or(current);
                    let target = platform::place(
                        current,
                        current.w,
                        h * scale,
                        area,
                        DOCK_TOLERANCE * scale,
                    );
                    self.snapping = true;
                    self.start_morph(current, target, Duration::from_millis(300), MorphKind::Mode);
                    return;
                }
            }
        }
        self.snapping = false;
        let Some(start) = self.resize_start.take() else {
            return;
        };
        let mini = self.ui().get_mini();
        self.placed_to = None;
        if mini != self.state.mini {
            self.state.mini = mini;
            if mini {
                self.normal_size = start;
            } else {
                self.state.mini_width = start.0 as f32;
            }
        }
        if mini {
            self.state.mini_width = self.last_size.0 as f32;
        }
        self.apply_constraints();
        self.save();
    }

    fn on_close(&mut self) {
        self.finish_morph();
        if self.ui().get_mini() {
            self.state.mini_width = self.last_size.0 as f32;
            self.state.mini = true;
        }
        self.save();
        self.engine.unload();
        if let Some(p) = &self.cover_file {
            let _ = std::fs::remove_file(p);
        }
    }
}

fn spawn_meta_worker(jobs: mpsc::Receiver<MetaJob>, results: mpsc::Sender<MetaResult>) {
    std::thread::Builder::new()
        .name("listen-meta".into())
        .stack_size(512 * 1024)
        .spawn(move || {
            let mut pending: Vec<MetaJob> = Vec::new();
            loop {
                if pending.is_empty() {
                    match jobs.recv() {
                        Ok(j) => pending.push(j),
                        Err(_) => return,
                    }
                }
                while let Ok(j) = jobs.try_recv() {
                    pending.push(j);
                }
                // The current track first: its title and artwork are on screen.
                let idx = pending
                    .iter()
                    .rposition(|j| matches!(j, MetaJob::Current(..)))
                    .unwrap_or(0);
                let job = pending.remove(idx);
                if matches!(job, MetaJob::Current(..)) {
                    pending.retain(|j| !matches!(j, MetaJob::Current(..)));
                }
                let send = |r: MetaResult| {
                    let ok = results.send(r).is_ok();
                    let _ = slint::invoke_from_event_loop(|| with_app(|app| app.drain_meta()));
                    ok
                };
                let ok = match job {
                    MetaJob::Current(path, gen) => {
                        // Tags first (fast), so the title is final before the cross-fade ends;
                        // then the artwork, which may take a few tens of milliseconds to decode.
                        let mut m = meta::probe(&path, true, false);
                        let cover = m.as_mut().and_then(|m| m.cover.take());
                        let from_folder = m.as_ref().map(|m| m.cover_from_folder).unwrap_or(false);
                        send(MetaResult::Current(gen, m))
                            && send(MetaResult::Art(
                                gen,
                                cover.as_deref().and_then(|c| meta::decode_artwork(c, 256, from_folder)),
                            ))
                    }
                    MetaJob::Duration(path) => {
                        let m = meta::probe(&path, false, true);
                        let d = m.as_ref().and_then(|m| m.duration);
                        let title = m
                            .as_ref()
                            .and_then(|m| m.title.as_ref())
                            .map(|_| meta::display_names(&path, m.as_ref()).0);
                        send(MetaResult::Duration(path, d, title))
                    }
                };
                if !ok {
                    return;
                }
            }
        })
        .expect("metadata thread");
}

fn launch_from_args() -> Option<(Vec<PathBuf>, Launch)> {
    let mut enqueue = false;
    let mut files = Vec::new();
    for a in std::env::args_os().skip(1) {
        match a.to_str() {
            Some("--enqueue") | Some("/enqueue") => enqueue = true,
            Some("--play") | Some("/play") => enqueue = false,
            _ => {
                let p = PathBuf::from(&a);
                let p = std::fs::canonicalize(&p).map(strip_verbatim).unwrap_or(p);
                files.push(p);
            }
        }
    }
    (!files.is_empty()).then_some((
        files,
        if enqueue {
            Launch::Enqueue
        } else {
            Launch::Play
        },
    ))
}

/// `\\?\C:\x` → `C:\x` (canonicalize on Windows returns verbatim paths).
fn strip_verbatim(p: PathBuf) -> PathBuf {
    let s = p.to_string_lossy();
    match s.strip_prefix(r"\\?\") {
        Some(rest) if !rest.starts_with("UNC\\") => PathBuf::from(rest),
        _ => p,
    }
}

fn main() -> Result<(), slint::PlatformError> {
    let state = AppState::load();
    let start_mini = state.mini;
    // The queue opens below the player, so a window restored with it open is taller.
    let base_h = 410.0;
    let (start_w, start_h) = (
        500.0,
        if state.queue_visible {
            base_h + 190.0
        } else {
            base_h
        },
    );

    let position = platform::startup_position(start_w, start_h);
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name("software".into())
        .with_winit_window_attributes_hook(move |attrs| {
            // Opaque: Slint asks for a transparent window by default, which on Windows turns
            // every not-yet-painted pixel see-through (the strip a growing window exposes
            // showed whatever was behind it, like a white flash).
            let mut attrs = attrs
                .with_transparent(false)
                .with_inner_size(winit::dpi::LogicalSize::new(start_w, start_h));
            if let Some((x, y)) = position {
                attrs = attrs.with_position(winit::dpi::PhysicalPosition::new(x, y));
            }
            #[cfg(windows)]
            {
                use winit::platform::windows::WindowAttributesExtWindows;
                attrs = attrs.with_undecorated_shadow(true);
            }
            #[cfg(target_os = "macos")]
            {
                use winit::platform::macos::WindowAttributesExtMacOS;
                attrs = attrs
                    .with_titlebar_transparent(true)
                    .with_title_hidden(true)
                    .with_fullsize_content_view(true)
                    .with_titlebar_buttons_hidden(true)
                    .with_movable_by_window_background(false);
            }
            attrs
        })
        .select()?;

    let ui = AppWindow::new()?;
    ui.window()
        .set_size(slint::LogicalSize::new(start_w as f32, start_h as f32));
    let fr = is_french();
    ui.global::<L>().set_fr(fr);
    #[cfg(windows)]
    {
        ui.global::<T>()
            .set_ui_font("Segoe UI Variable Text".into());
        ui.global::<T>().set_time_font("Bahnschrift".into());
    }
    #[cfg(target_os = "macos")]
    {
        ui.set_frameless(false);
        ui.set_mac(true);
    }

    let (job_tx, job_rx) = mpsc::channel();
    let (res_tx, res_rx) = mpsc::channel();
    spawn_meta_worker(job_rx, res_tx);

    let queue_model = Rc::new(VecModel::<QueueItem>::default());
    ui.set_queue(ModelRc::from(queue_model.clone()));
    ui.set_volume(state.volume);
    ui.set_repeat_mode(state.repeat as i32);
    ui.set_show_remaining(state.show_remaining);
    ui.set_topmost(state.always_on_top);
    ui.set_queue_open(state.queue_visible);
    if state.queue_visible {
        ui.set_queue_extra(ui.get_queue_height());
    }

    let app = Rc::new(RefCell::new(App {
        ui: ui.as_weak(),
        engine: audio::Engine::new(state.volume),
        state,
        media: None,
        queue_model,
        durations: HashMap::new(),
        titles: HashMap::new(),
        meta_tx: job_tx,
        meta_rx: res_rx,
        generation: 0,
        current_meta: None,
        cover_file: None,
        normal_size: (start_w, start_h),
        height_before_queue: None,
        placed_from: None,
        placed_to: None,
        morph: None,
        morph_timer: slint::Timer::default(),
        progress_timer: slint::Timer::default(),
        progress_interval: std::cell::Cell::new(Duration::ZERO),
        clock_anchor: std::cell::Cell::new(None),
        wave_epoch: Instant::now(),
        resize_settle: slint::Timer::default(),
        resize_start: None,
        last_size: (start_w, start_h),
        pending_drop: Vec::new(),
        hovered_files: Vec::new(),
        started: false,
        snapping: false,
    }));
    APP.with(|a| *a.borrow_mut() = Some(app.clone()));

    {
        let mut a = app.borrow_mut();
        // The queue opens below the player: the start height includes it.
        if a.state.queue_visible {
            a.height_before_queue = Some(base_h);
        }
        a.refresh_queue();
        if let Some((files, mode)) = launch_from_args() {
            a.apply_launch(files, mode, true);
        } else {
            a.show_track();
            if let Some(i) = a.state.current {
                // Ready to resume: the track is shown, the device opens on Play.
                let path = a.state.queue[i].clone();
                if let Some(Some(d)) = a.durations.get(&path) {
                    a.ui().set_duration_secs(d.as_secs_f32());
                }
                a.ui().set_can_seek(true);
            }
        }
    }

    macro_rules! on {
        ($setter:ident, |$app:ident $(, $arg:ident)*| $body:expr) => {
            ui.$setter(move |$($arg),*| with_app(|$app| $body));
        };
    }
    on!(on_toggle_play, |a| a.toggle_play());
    on!(on_previous, |a| a.previous());
    on!(on_next, |a| a.next(false));
    on!(on_cycle_repeat, |a| a.cycle_repeat());
    on!(on_seek, |a, f| a.seek_fraction(f));
    on!(on_nudge, |a, s| a.nudge(s as f64));
    on!(on_set_volume, |a, v| {
        a.set_volume(v);
        a.save();
    });
    on!(on_toggle_mute, |a| a.toggle_mute());
    on!(on_toggle_queue, |a| a.toggle_queue());
    on!(on_toggle_mini, |a| a.toggle_mini());
    on!(on_toggle_topmost, |a| {
        a.state.always_on_top = !a.state.always_on_top;
        a.ui().set_topmost(a.state.always_on_top);
        a.save();
    });
    on!(on_minimize, |a| a.ui().window().set_minimized(true));
    on!(on_quit, |a| {
        a.on_close();
        let _ = slint::quit_event_loop();
    });
    on!(on_add_files, |a| a.add_files());
    on!(on_play_index, |a, i| a.play_index(i.max(0) as usize));
    on!(on_remove_index, |a, i| a.remove_index(i.max(0) as usize));
    on!(on_clear_queue, |a| a.clear_queue());
    on!(on_move_item, |a, from, to| {
        a.state.move_item(from.max(0) as usize, to.max(0) as usize);
        a.refresh_queue();
        a.ui().set_selected_index(to);
        a.save();
    });
    on!(on_start_drag, |a| {
        a.with_winit(|w| {
            let _ = w.drag_window();
        });
    });

    ui.window().on_close_requested(|| {
        with_app(|a| a.on_close());
        slint::CloseRequestResponse::HideWindow
    });

    // Playback clock: position, end of track, device housekeeping.
    let clock = slint::Timer::default();
    clock.start(
        slint::TimerMode::Repeated,
        Duration::from_millis(250),
        || with_app(|a| a.tick()),
    );

    // Native window: available once the event loop runs.
    let weak = ui.as_weak();
    let _ = slint::spawn_local(async move {
        let Some(ui) = weak.upgrade() else { return };
        let Ok(winit_window) = ui.window().winit_window().await else {
            return;
        };
        platform::style_window(&winit_window);
        drop(winit_window);
        ui.window().on_winit_window_event(|_, event| {
            use winit::event::WindowEvent;
            match event {
                WindowEvent::HoveredFile(p) => with_app(|a| {
                    a.hovered_files.push(p.clone());
                    let any = a.hovered_files.iter().any(|p| state::is_supported(p));
                    a.ui().set_dropping(any);
                }),
                WindowEvent::HoveredFileCancelled => with_app(|a| {
                    a.hovered_files.clear();
                    a.ui().set_dropping(false);
                }),
                WindowEvent::DroppedFile(p) => with_app(|a| {
                    a.hovered_files.clear();
                    a.ui().set_dropping(false);
                    a.pending_drop.push(p.clone());
                    // One drop delivers one event per file: apply them together.
                    slint::Timer::single_shot(Duration::from_millis(30), || {
                        with_app(|a| {
                            let files = std::mem::take(&mut a.pending_drop);
                            let idle = a.state.current.is_none();
                            a.apply_launch(files, Launch::Enqueue, idle);
                        })
                    });
                }),
                WindowEvent::Resized(size) => {
                    let size = *size;
                    with_app(|a| {
                        let s = a.scale();
                        a.on_resized(size.width as f64 / s, size.height as f64 / s);
                    });
                }
                _ => {}
            }
            EventResult::Propagate
        });

        with_app(|a| {
            // Media keys and the OS media panel.
            #[cfg(windows)]
            let hwnd = a
                .with_winit(platform::hwnd)
                .flatten()
                .map(|h| h as *mut std::ffi::c_void);
            #[cfg(not(windows))]
            let hwnd: Option<*mut std::ffi::c_void> = None;
            let available = hwnd.is_some() || cfg!(not(windows));
            a.media = if !available {
                None
            } else {
                media::Media::new(hwnd, |cmd| {
                    let _ = slint::invoke_from_event_loop(move || {
                        with_app(|a| match cmd {
                            media::Command::Play => {
                                if !a.engine.is_playing() {
                                    a.toggle_play()
                                }
                            }
                            media::Command::Pause | media::Command::Stop => {
                                if a.engine.is_playing() {
                                    a.toggle_play()
                                }
                            }
                            media::Command::Toggle => a.toggle_play(),
                            media::Command::Next => a.next(false),
                            media::Command::Previous => a.previous(),
                            media::Command::SeekBy(s) => a.nudge(s),
                            media::Command::SeekTo(d) => {
                                a.engine.seek(d);
                                a.tick_position();
                                a.sync_playback_force();
                            }
                            media::Command::Raise => {
                                a.ui().window().set_minimized(false);
                                a.with_winit(|w| w.focus_window());
                            }
                            media::Command::Quit => {
                                a.on_close();
                                let _ = slint::quit_event_loop();
                            }
                        })
                    });
                })
            };
            a.show_track_media();
            a.started = true;
            if let Some(b) = a.bounds() {
                let s = a.scale();
                a.last_size = (b.w / s, b.h / s);
            }
            if start_mini {
                a.state.mini = false;
                a.set_mini(true, false);
            }
            a.apply_constraints();
        });
    });

    // Debug builds only: drive the app from a command file (one command per line), so it can
    // be exercised without synthetic keyboard or mouse input.
    #[cfg(any(debug_assertions, feature = "test-hooks"))]
    let _debug_cmds = std::env::var_os("LISTEN_DEBUG_CMDS").map(|path| {
        let path = PathBuf::from(path);
        let timer = slint::Timer::default();
        timer.start(slint::TimerMode::Repeated, Duration::from_millis(100), move || {
            let Ok(text) = std::fs::read_to_string(&path) else { return };
            if text.trim().is_empty() {
                return;
            }
            let _ = std::fs::write(&path, "");
            for line in text.lines() {
                let mut parts = line.split_whitespace();
                let cmd = parts.next().unwrap_or("");
                let args: Vec<f64> = parts.filter_map(|p| p.parse().ok()).collect();
                with_app(|a| match cmd {
                    "mini" => a.toggle_mini(),
                    "queue" => a.toggle_queue(),
                    "play" => a.toggle_play(),
                    "next" => a.next(false),
                    "prev" => a.previous(),
                    "repeat" => a.cycle_repeat(),
                    "seek" => a.seek_fraction(args.first().copied().unwrap_or(0.0) as f32),
                    "size" if args.len() == 2 => {
                        let s = a.scale();
                        if let Some(b) = a.bounds() {
                            a.set_bounds(Rect { w: args[0] * s, h: args[1] * s, ..b });
                        }
                    }
                    _ => eprintln!("unknown debug command: {line}"),
                });
            }
        });
        timer
    });

    ui.run()?;
    with_app(|a| a.on_close());
    drop(clock);
    Ok(())
}

impl App {
    /// Publishes the current track to the media panel once the controls exist.
    fn show_track_media(&mut self) {
        let Some(path) = self.current_path().cloned() else {
            self.sync_playback_force();
            return;
        };
        let (title, subtitle) = meta::display_names(&path, self.current_meta.as_ref());
        let duration = self.engine.duration();
        if let Some(m) = &mut self.media {
            m.set_track(&title, &subtitle, "", duration, None);
        }
        self.sync_playback_force();
    }
}
