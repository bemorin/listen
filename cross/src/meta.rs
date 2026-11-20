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

/// Title and subtitle shown for a file. Tags win; otherwise the Windows app's rule:
/// "Artist - Title" file names split in two, else the folder name as subtitle.
pub fn display_names(path: &Path, meta: Option<&TrackMeta>) -> (String, String) {
    let stem = path
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("")
        .to_string();
    let folder = path
        .parent()
        .and_then(|p| p.file_name())
        .and_then(|s| s.to_str())
        .unwrap_or("");
    let (mut title, mut subtitle) = match stem.find(" - ") {
        // "03 - Song" is a track number, not an artist: keep the folder as subtitle.
        Some(i)
            if i > 0
                && i + 3 < stem.len()
                && stem[..i].trim().chars().all(|c| c.is_ascii_digit()) =>
        {
            (stem[i + 3..].trim().to_string(), folder.to_string())
        }
        Some(i) if i > 0 && i + 3 < stem.len() => (
            stem[i + 3..].trim().to_string(),
            stem[..i].trim().to_string(),
        ),
        _ => (stem.clone(), folder.to_string()),
    };
    if let Some(m) = meta {
        if let Some(t) = m.title.as_ref().filter(|t| !t.trim().is_empty()) {
            title = t.trim().to_string();
            if let Some(a) = m.artist.as_ref().filter(|a| !a.trim().is_empty()) {
                subtitle = a.trim().to_string();
            }
        }
    }
    if subtitle.is_empty() {
        subtitle = extension_label(path);
    }
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
    fn names_follow_the_windows_rule() {
        let p = PathBuf::from("/music/Album/Artist - Song.mp3");
        assert_eq!(display_names(&p, None), ("Song".into(), "Artist".into()));
        let p = PathBuf::from("/music/Album/03 - Song.flac");
        assert_eq!(display_names(&p, None), ("Song".into(), "Album".into()));
        let p = PathBuf::from("/music/Music/★ Back Up.wav");
        assert_eq!(
            display_names(&p, None),
            ("★ Back Up".into(), "Music".into())
        );
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
