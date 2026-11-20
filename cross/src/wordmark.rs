//! The "listen" wordmark, rendered by the app at the screen's exact pixel size.
//!
//! Drawn as a vector path by the UI, its stems landed between pixels (a 1.95 px stroke is
//! 2.9 physical pixels at 150 %), which looked soft. Like a hinted font, this renders it at
//! the physical size with a whole-pixel stroke and every vertical stem centred on the pixel
//! grid, then shows it 1:1. tiny-skia is already in the binary (Slint's SVG support uses it).

use tiny_skia::{LineCap, LineJoin, Paint, PathBuilder, Pixmap, Stroke, Transform};

/// One letter: its path in design units (x-height 5..14) and the x of the stem to snap.
struct Letter {
    path: &'static str,
    stem: Option<f32>,
    /// Second stem whose distance to the first is snapped too (the n's right leg).
    second_stem: Option<f32>,
}

const LETTERS: [Letter; 6] = [
    Letter { path: "M1.2,0.6 L1.2,14", stem: Some(1.2), second_stem: None },
    Letter { path: "M6.9,0.75 L6.9,0.95 M6.9,5 L6.9,14", stem: Some(6.9), second_stem: None },
    Letter {
        path: "M18,6.6 C17.3,5.5 16.2,5 14.9,5 C13.2,5 12.2,5.9 12.2,7.1 C12.2,9.8 18.4,8.6 18.4,11.4 C18.4,12.8 17.1,14 15.1,14 C13.6,14 12.4,13.4 11.6,12.2",
        stem: None,
        second_stem: None,
    },
    Letter {
        path: "M24.2,1.8 L24.2,11.6 C24.2,13.2 25,14 26.5,14 L27.2,14 M22.2,5.3 L27,5.3",
        stem: Some(24.2),
        second_stem: None,
    },
    Letter {
        path: "M31.3,9.6 L39.2,9.6 C39.2,7 37.6,5 35.3,5 C32.9,5 31.2,7 31.2,9.5 C31.2,12 32.9,14 35.4,14 C37,14 38.2,13.3 38.9,12.1",
        stem: None,
        second_stem: None,
    },
    Letter {
        path: "M44,14 L44,5 M44,9.2 C44,6.6 45.7,5 47.7,5 C49.8,5 51.2,6.6 51.2,9.2 L51.2,14",
        stem: Some(44.0),
        second_stem: Some(51.2),
    },
];

/// Design box: x from -1.4 to 53.6, y from -1.4 to 15.4 (stroke and caps included).
const ORIGIN: (f32, f32) = (-1.4, -1.4);
const SIZE: (f32, f32) = (55.0, 16.8);

/// Renders the wordmark for `scale` (physical pixels per logical pixel) at `units` logical
/// pixels per design unit. Returns width, height and premultiplied RGBA.
pub fn render(scale: f32, units: f32) -> Option<(u32, u32, Vec<u8>)> {
    // Whole-pixel stroke, and the x-height (5..14) snapped to whole pixels.
    let stroke_px = (1.95 * units * scale).round().max(1.0);
    let x_height_px = (9.0 * units * scale).round();
    let f = x_height_px / 9.0;
    let w = (SIZE.0 * f).ceil() as u32 + 1;
    let h = (SIZE.1 * f).ceil() as u32 + 1;
    let mut pixmap = Pixmap::new(w, h)?;

    // Stem centres sit on pixel centres for an odd stroke, on pixel edges for an even one.
    let grid = |x: f32| {
        if stroke_px as i32 % 2 == 1 {
            (x - 0.5).round() + 0.5
        } else {
            x.round()
        }
    };
    // Baseline: the round cap's lower edge on a pixel edge.
    let oy = ((14.0 - ORIGIN.1) * f + stroke_px / 2.0).round() - stroke_px / 2.0 - 14.0 * f;
    let ox = -ORIGIN.0 * f;

    let mut pb = PathBuilder::new();
    for letter in &LETTERS {
        let (shift, stretch) = match letter.stem {
            Some(stem) => {
                let at = ox + stem * f;
                let shift = grid(at) - at;
                let stretch = letter.second_stem.map(|second| {
                    let span = (second - stem) * f;
                    (stem, span.round() / span)
                });
                (shift, stretch)
            }
            None => (0.0, None),
        };
        let map = |x: f32, y: f32| {
            let x = match stretch {
                Some((stem, k)) => stem + (x - stem) * k,
                None => x,
            };
            (ox + x * f + shift, oy + y * f)
        };
        append(&mut pb, letter.path, map);
    }
    let path = pb.finish()?;

    let mut paint = Paint::default();
    paint.set_color_rgba8(0xF2, 0xF2, 0xF4, 0xFF);
    paint.anti_alias = true;
    let stroke = Stroke { width: stroke_px, line_cap: LineCap::Round, line_join: LineJoin::Round, ..Stroke::default() };
    pixmap.stroke_path(&path, &paint, &stroke, Transform::identity(), None);
    Some((w, h, pixmap.take()))
}

/// Appends an SVG path made of M, L and C commands, mapped through `map`.
fn append(pb: &mut PathBuilder, d: &str, map: impl Fn(f32, f32) -> (f32, f32)) {
    let mut cmd = 'M';
    let mut nums: Vec<f32> = Vec::new();
    let flush = |pb: &mut PathBuilder, cmd: char, nums: &mut Vec<f32>| {
        match (cmd, nums.len()) {
            ('M', 2) => {
                let (x, y) = map(nums[0], nums[1]);
                pb.move_to(x, y);
            }
            ('L', 2) => {
                let (x, y) = map(nums[0], nums[1]);
                pb.line_to(x, y);
            }
            ('C', 6) => {
                let (x1, y1) = map(nums[0], nums[1]);
                let (x2, y2) = map(nums[2], nums[3]);
                let (x, y) = map(nums[4], nums[5]);
                pb.cubic_to(x1, y1, x2, y2, x, y);
            }
            _ => return,
        }
        nums.clear();
    };
    for token in d.split(|c: char| c == ' ' || c == ',').filter(|t| !t.is_empty()) {
        let (head, rest) = token.split_at(if token.starts_with(|c: char| c.is_ascii_alphabetic()) { 1 } else { 0 });
        if let Some(c) = head.chars().next() {
            cmd = c;
        }
        if let Ok(v) = rest.parse::<f32>() {
            nums.push(v);
            flush(pb, cmd, &mut nums);
        }
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn renders_at_common_scales() {
        for scale in [1.0, 1.25, 1.5, 2.0] {
            let (w, h, rgba) = super::render(scale, 1.0).expect("rendered");
            assert_eq!(rgba.len(), (w * h * 4) as usize);
            assert!(rgba.chunks(4).any(|p| p[3] == 255), "some fully covered pixels at {scale}");
        }
    }
}
