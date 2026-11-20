//! Track metadata: titles, durations and artwork, read with symphonia (no OS services).

use std::fs::File;
use std::path::Path;
use std::time::Duration;
use symphonia::core::formats::FormatOptions;
use symphonia::core::io::{MediaSourceStream, MediaSourceStreamOptions};
use symphonia::core::meta::{
    Limit, MetadataOptions, MetadataRevision, StandardTagKey, StandardVisualKey,
};
use symphonia::core::probe::Hint;

#[derive(Default, Clone)]
pub struct TrackMeta {
    pub title: Option<String>,
    pub artist: Option<String>,
    pub album: Option<String>,
    pub duration: Option<Duration>,
    pub cover: Option<Vec<u8>>,
    /// The cover came from an image file next to the track, not from its tags.
    pub cover_from_folder: bool,
}

/// Title and subtitle (artist) shown for a file. The title is the title tag, else the file
/// name ("Artist - Title" names are split; a leading track number is not an artist). The
/// subtitle is the artist tag, else the artist part of the file name, else the album tag,
/// else nothing: a folder name says nothing about the music.
pub fn display_names(path: &Path, meta: Option<&TrackMeta>) -> (String, String) {
    let stem = path.file_stem().and_then(|s| s.to_str()).unwrap_or("").to_string();
    let (mut title, name_artist) = match stem.find(" - ") {
        Some(i) if i > 0 && i + 3 < stem.len() => {
            let left = stem[..i].trim();
            let artist = (!left.chars().all(|c| c.is_ascii_digit())).then(|| left.to_string());
            (stem[i + 3..].trim().to_string(), artist)
        }
        _ => (stem.clone(), None),
    };
    let tag = |f: fn(&TrackMeta) -> &Option<String>| {
        meta.and_then(|m| f(m).as_deref()).map(str::trim).filter(|t| !t.is_empty()).map(str::to_string)
    };
    if let Some(t) = tag(|m| &m.title) {
        title = t;
    }
    let subtitle = tag(|m| &m.artist).or(name_artist).or_else(|| tag(|m| &m.album)).unwrap_or_default();
    (title, subtitle)
}

pub fn extension_label(path: &Path) -> String {
    path.extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_ascii_uppercase()
}

/// Reads tags, duration and (optionally) the embedded picture. When the container does
/// not state its length (VBR MP3 without a Xing header), `exact` counts the packets.
pub fn probe(path: &Path, want_cover: bool, exact: bool) -> Option<TrackMeta> {
    let file = File::open(path).ok()?;
    let mss = MediaSourceStream::new(Box::new(file), MediaSourceStreamOptions::default());
    let mut hint = Hint::new();
    if let Some(ext) = path.extension().and_then(|e| e.to_str()) {
        hint.with_extension(ext);
    }
    let meta_opts = MetadataOptions {
        limit_metadata_bytes: Limit::Maximum(64 * 1024),
        limit_visual_bytes: if want_cover {
            Limit::Maximum(16 * 1024 * 1024)
        } else {
            Limit::Maximum(0)
        },
    };
    let mut probed = symphonia::default::get_probe()
        .format(&hint, mss, &FormatOptions::default(), &meta_opts)
        .ok()?;

    let mut out = TrackMeta::default();
    if let Some(mut m) = probed.metadata.get() {
        if let Some(rev) = m.skip_to_latest() {
            absorb(&mut out, rev, want_cover);
        }
    }
    let mut format = probed.format;
    if let Some(rev) = format.metadata().skip_to_latest() {
        absorb(&mut out, rev, want_cover);
    }

    let track = format.default_track()?;
    let params = &track.codec_params;
    let track_id = track.id;
    if let (Some(n), Some(tb)) = (params.n_frames, params.time_base) {
        let t = tb.calc_time(n);
        out.duration = Some(Duration::from_secs(t.seconds) + Duration::from_secs_f64(t.frac));
    } else if exact {
        let tb = params.time_base;
        let mut total: u64 = 0;
        while let Ok(packet) = format.next_packet() {
            if packet.track_id() == track_id {
                total = packet.ts() + packet.dur();
            }
        }
        if let (Some(tb), true) = (tb, total > 0) {
            let t = tb.calc_time(total);
            out.duration = Some(Duration::from_secs(t.seconds) + Duration::from_secs_f64(t.frac));
        }
    }

    if want_cover && out.cover.is_none() {
        out.cover = folder_cover(path);
        out.cover_from_folder = out.cover.is_some();
    }
    Some(out)
}

fn absorb(out: &mut TrackMeta, rev: &MetadataRevision, want_cover: bool) {
    for tag in rev.tags() {
        let value = tag.value.to_string();
        match tag.std_key {
            Some(StandardTagKey::TrackTitle) if out.title.is_none() => out.title = Some(value),
            Some(StandardTagKey::Artist) if out.artist.is_none() => out.artist = Some(value),
            Some(StandardTagKey::AlbumArtist) if out.artist.is_none() => out.artist = Some(value),
            Some(StandardTagKey::Album) if out.album.is_none() => out.album = Some(value),
            _ => {}
        }
    }
    if want_cover && out.cover.is_none() {
        let visuals = rev.visuals();
        let front = visuals
            .iter()
            .find(|v| v.usage == Some(StandardVisualKey::FrontCover))
            .or_else(|| visuals.first());
        if let Some(v) = front {
            out.cover = Some(v.data.to_vec());
        }
    }
}

/// `cover.jpg`, `folder.jpg` and friends next to the file, as most players honour them.
fn folder_cover(path: &Path) -> Option<Vec<u8>> {
    let dir = path.parent()?;
    const NAMES: [&str; 4] = ["cover", "folder", "front", "album"];
    let entries = std::fs::read_dir(dir).ok()?;
    let mut best: Option<(usize, std::path::PathBuf)> = None;
    for e in entries.flatten() {
        let p = e.path();
        let (Some(stem), Some(ext)) = (
            p.file_stem()
                .and_then(|s| s.to_str())
                .map(str::to_ascii_lowercase),
            p.extension()
                .and_then(|s| s.to_str())
                .map(str::to_ascii_lowercase),
        ) else {
            continue;
        };
        if !matches!(ext.as_str(), "jpg" | "jpeg" | "png") {
            continue;
        }
        if let Some(rank) = NAMES.iter().position(|n| *n == stem) {
            if best.as_ref().map(|(r, _)| rank < *r).unwrap_or(true) {
                best = Some((rank, p));
            }
        }
    }
    let (_, p) = best?;
    let data = std::fs::read(p).ok()?;
    (data.len() < 32 * 1024 * 1024).then_some(data)
}

/// Decoded artwork, square-cropped and downscaled to `size` pixels, plus its mean colour.
pub struct Artwork {
    pub size: u32,
    pub rgba: Vec<u8>,
    pub tint: [u8; 3],
    /// Ambient colours from the top-left and bottom-right of the artwork, made vivid enough
    /// to read as light on a dark window.
    pub glow: [[u8; 3]; 2],
}

/// `reject_flat` drops near-uniform images: right for folder images (Windows Media Player
/// leaves blank `Folder.jpg` placeholders), not for artwork embedded on purpose.
pub fn decode_artwork(data: &[u8], size: u32, reject_flat: bool) -> Option<Artwork> {
    let img = image::load_from_memory(data).ok()?.to_rgba8();
    let (w, h) = img.dimensions();
    if w == 0 || h == 0 {
        return None;
    }
    let side = w.min(h);
    let cropped =
        image::imageops::crop_imm(&img, (w - side) / 2, (h - side) / 2, side, side).to_image();
    drop(img);
    let scaled = if side > size {
        image::imageops::resize(&cropped, size, size, image::imageops::FilterType::Triangle)
    } else {
        cropped
    };
    let actual = scaled.width();
    let (mut r, mut g, mut b, mut n) = (0u64, 0u64, 0u64, 0u64);
    for px in scaled.pixels().step_by(7) {
        r += px[0] as u64;
        g += px[1] as u64;
        b += px[2] as u64;
        n += 1;
    }
    let n = n.max(1);
    let mean = [(r / n) as f64, (g / n) as f64, (b / n) as f64];
    // A flat image (a placeholder `Folder.jpg`, a blank thumbnail) is not artwork.
    let mut var = 0f64;
    for px in scaled.pixels().step_by(7) {
        for c in 0..3 {
            let d = px[c] as f64 - mean[c];
            var += d * d;
        }
    }
    if reject_flat && (var / (3 * n) as f64).sqrt() < 6.0 {
        return None;
    }
    let quadrant = |x0: u32, y0: u32| {
        let half = (actual / 2).max(1);
        let (mut r, mut g, mut b, mut n) = (0u64, 0u64, 0u64, 0u64);
        for y in (y0..(y0 + half).min(actual)).step_by(3) {
            for x in (x0..(x0 + half).min(actual)).step_by(3) {
                let px = scaled.get_pixel(x, y);
                r += px[0] as u64;
                g += px[1] as u64;
                b += px[2] as u64;
                n += 1;
            }
        }
        let n = n.max(1);
        vivid([(r / n) as u8, (g / n) as u8, (b / n) as u8])
    };
    let glow = [quadrant(0, 0), quadrant(actual / 2, actual / 2)];
    Some(Artwork {
        size: actual,
        rgba: scaled.into_raw(),
        tint: vivid([(r / n) as u8, (g / n) as u8, (b / n) as u8]),
        glow,
    })
}

/// Lifts a sampled colour into a glow: more saturated, mid lightness. Greys stay grey.
fn vivid(c: [u8; 3]) -> [u8; 3] {
    let [r, g, b] = c.map(|v| v as f32 / 255.0);
    let max = r.max(g).max(b);
    let min = r.min(g).min(b);
    let l = (max + min) / 2.0;
    let d = max - min;
    if d < 0.02 {
        let v = (l.clamp(0.22, 0.4) * 255.0) as u8;
        return [v, v, v];
    }
    let s = d / (1.0 - (2.0 * l - 1.0).abs());
    let h = if max == r {
        ((g - b) / d).rem_euclid(6.0)
    } else if max == g {
        (b - r) / d + 2.0
    } else {
        (r - g) / d + 4.0
    } / 6.0;
    let s = (s * 1.25).min(0.8);
    let l = l.clamp(0.3, 0.48);
    let q = if l < 0.5 { l * (1.0 + s) } else { l + s - l * s };
    let p = 2.0 * l - q;
    let hue = |t: f32| {
        let t = t.rem_euclid(1.0);
        if t < 1.0 / 6.0 {
            p + (q - p) * 6.0 * t
        } else if t < 0.5 {
            q
        } else if t < 2.0 / 3.0 {
            p + (q - p) * (2.0 / 3.0 - t) * 6.0
        } else {
            p
        }
    };
    [hue(h + 1.0 / 3.0), hue(h), hue(h - 1.0 / 3.0)].map(|v| (v * 255.0).round() as u8)
}

pub fn format_time(d: Duration) -> String {
    let s = d.as_secs();
    if s >= 3600 {
        format!("{:02}:{:02}:{:02}", s / 3600, (s / 60) % 60, s % 60)
    } else {
        format!("{:02}:{:02}", s / 60, s % 60)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    #[test]
    fn names_prefer_tags_and_never_the_folder() {
        let p = PathBuf::from("/music/Album/Artist - Song.mp3");
        assert_eq!(display_names(&p, None), ("Song".into(), "Artist".into()));
        let p = PathBuf::from("/music/Album/03 - Song.flac");
        assert_eq!(display_names(&p, None), ("Song".into(), "".into()));
        let p = PathBuf::from("/music/Music/★ Back Up.wav");
        assert_eq!(display_names(&p, None), ("★ Back Up".into(), "".into()));
        let album = TrackMeta { album: Some("The Album".into()), ..Default::default() };
        assert_eq!(display_names(&p, Some(&album)), ("★ Back Up".into(), "The Album".into()));
        let tagged = TrackMeta {
            title: Some("Real".into()),
            artist: Some("Band".into()),
            ..Default::default()
        };
        assert_eq!(
            display_names(&p, Some(&tagged)),
            ("Real".into(), "Band".into())
        );
    }

    #[test]
    fn flat_images_are_not_artwork() {
        let flat = image::RgbaImage::from_pixel(64, 64, image::Rgba([3, 3, 3, 255]));
        let mut png = Vec::new();
        image::DynamicImage::ImageRgba8(flat)
            .write_to(&mut std::io::Cursor::new(&mut png), image::ImageFormat::Png)
            .unwrap();
        assert!(decode_artwork(&png, 256, true).is_none());
        assert!(decode_artwork(&png, 256, false).is_some());
        let busy = image::RgbaImage::from_fn(64, 64, |x, y| image::Rgba([(x * 4) as u8, (y * 4) as u8, 90, 255]));
        let mut png = Vec::new();
        image::DynamicImage::ImageRgba8(busy)
            .write_to(&mut std::io::Cursor::new(&mut png), image::ImageFormat::Png)
            .unwrap();
        assert!(decode_artwork(&png, 256, true).is_some());
    }

    #[test]
    fn times() {
        assert_eq!(format_time(Duration::from_secs(204)), "03:24");
        assert_eq!(format_time(Duration::from_secs(3725)), "01:02:05");
    }
}

/// The window's ambient wash for two glow colours: a small RGBA image (it is stretched to
/// the window) with a glow from the top left and one from the bottom right, on transparent.
/// Geometry matches the 500x410 window the design was drawn for.
pub fn ambient_wash(a: [u8; 3], b: [u8; 3]) -> (u32, u32, Vec<u8>) {
    const W: u32 = 125;
    const H: u32 = 103;
    let (ww, wh) = (500.0f32, 410.0f32);
    // (centre x, centre y, radius at which the glow is gone, strength), window units.
    let glows = [
        (0.30 * ww, 0.15 * ww, 0.68 * 1.06 * ww, 1.0f32, a),
        (0.90 * ww, wh - 0.05 * ww, 0.68 * 0.99 * ww, 0.9f32, b),
    ];
    let mut out = vec![0u8; (W * H * 4) as usize];
    for y in 0..H {
        for x in 0..W {
            let px = (x as f32 + 0.5) / W as f32 * ww;
            let py = (y as f32 + 0.5) / H as f32 * wh;
            // Premultiplied "over" compositing of the two glows.
            let (mut r, mut g, mut bl, mut al) = (0f32, 0f32, 0f32, 0f32);
            for (cx, cy, radius, strength, c) in glows {
                let d = ((px - cx).powi(2) + (py - cy).powi(2)).sqrt();
                let t = (1.0 - d / radius).clamp(0.0, 1.0);
                let ga = t * strength;
                r = c[0] as f32 * ga + r * (1.0 - ga);
                g = c[1] as f32 * ga + g * (1.0 - ga);
                bl = c[2] as f32 * ga + bl * (1.0 - ga);
                al = ga + al * (1.0 - ga);
            }
            let i = ((y * W + x) * 4) as usize;
            if al > 0.0 {
                out[i] = (r / al).round().min(255.0) as u8;
                out[i + 1] = (g / al).round().min(255.0) as u8;
                out[i + 2] = (bl / al).round().min(255.0) as u8;
                out[i + 3] = (al * 255.0).round() as u8;
            }
        }
    }
    (W, H, out)
}
