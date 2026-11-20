//! The few window services Slint/winit do not cover: the work area (screen minus taskbar or
//! dock), atomic move+resize, and Windows 11 rounded corners. Rectangles are in physical
//! pixels.

use slint::winit_030::winit;
use winit::window::Window;

#[derive(Clone, Copy, Debug, PartialEq, Default)]
pub struct Rect {
    pub x: f64,
    pub y: f64,
    pub w: f64,
    pub h: f64,
}

impl Rect {
    pub fn right(&self) -> f64 {
        self.x + self.w
    }
    pub fn bottom(&self) -> f64 {
        self.y + self.h
    }
    pub fn roughly_eq(&self, o: &Rect) -> bool {
        (self.x - o.x).abs() <= 2.0
            && (self.y - o.y).abs() <= 2.0
            && (self.right() - o.right()).abs() <= 2.0
            && (self.bottom() - o.bottom()).abs() <= 2.0
    }
    pub fn lerp(&self, to: &Rect, t: f64) -> Rect {
        Rect {
            x: self.x + (to.x - self.x) * t,
            y: self.y + (to.y - self.y) * t,
            w: self.w + (to.w - self.w) * t,
            h: self.h + (to.h - self.h) * t,
        }
    }
}

/// Window bounds on screen.
pub fn bounds(w: &Window) -> Option<Rect> {
    let pos = w.outer_position().ok()?;
    let size = w.outer_size();
    Some(Rect {
        x: pos.x as f64,
        y: pos.y as f64,
        w: size.width as f64,
        h: size.height as f64,
    })
}

/// Moves and resizes in one step where the OS allows it, so a growing window anchored at
/// its bottom or right edge does not wobble.
pub fn set_bounds(w: &Window, r: Rect) {
    #[cfg(windows)]
    if let Some(hwnd) = hwnd(w) {
        use windows_sys::Win32::UI::WindowsAndMessaging::*;
        unsafe {
            SetWindowPos(
                hwnd as _,
                std::ptr::null_mut(),
                r.x.round() as i32,
                r.y.round() as i32,
                r.w.round() as i32,
                r.h.round() as i32,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER,
            );
        }
        return;
    }
    w.set_outer_position(winit::dpi::PhysicalPosition::new(
        r.x.round() as i32,
        r.y.round() as i32,
    ));
    let _ = w.request_inner_size(winit::dpi::PhysicalSize::new(
        r.w.round() as u32,
        r.h.round() as u32,
    ));
}

/// The usable part of the monitor the window is on.
pub fn work_area(w: &Window) -> Rect {
    #[cfg(windows)]
    if let Some(hwnd) = hwnd(w) {
        use windows_sys::Win32::Graphics::Gdi::*;
        unsafe {
            let mon = MonitorFromWindow(hwnd as _, MONITOR_DEFAULTTONEAREST);
            let mut info: MONITORINFO = std::mem::zeroed();
            info.cbSize = std::mem::size_of::<MONITORINFO>() as u32;
            if !mon.is_null() && GetMonitorInfoW(mon, &mut info) != 0 {
                let r = info.rcWork;
                return Rect {
                    x: r.left as f64,
                    y: r.top as f64,
                    w: (r.right - r.left) as f64,
                    h: (r.bottom - r.top) as f64,
                };
            }
        }
    }
    // macOS and Linux: winit only knows the monitor; keep clear of a typical menu bar/panel.
    match w.current_monitor() {
        Some(m) => {
            let p = m.position();
            let s = m.size();
            let top = if cfg!(target_os = "macos") {
                25.0 * m.scale_factor()
            } else {
                0.0
            };
            Rect {
                x: p.x as f64,
                y: p.y as f64 + top,
                w: s.width as f64,
                h: s.height as f64 - top,
            }
        }
        None => Rect {
            x: -32000.0,
            y: -32000.0,
            w: 64000.0,
            h: 64000.0,
        },
    }
}

/// Places a window of size `w`×`h` relative to `current`, like the Windows app: the top-left
/// corner stays put by default; an edge docked against the work area stays docked; a resize
/// that would cross an edge grows the other way; the result always stays inside the area.
pub fn place(current: Rect, w: f64, h: f64, area: Rect, dock: f64) -> Rect {
    let w = w.min(area.w);
    let h = h.min(area.h);
    let x = place_axis(current.x, current.right(), w, area.x, area.right(), dock);
    let y = place_axis(current.y, current.bottom(), h, area.y, area.bottom(), dock);
    Rect {
        x: x.round(),
        y: y.round(),
        w: w.round(),
        h: h.round(),
    }
}

fn place_axis(start: f64, end: f64, len: f64, min: f64, max: f64, dock: f64) -> f64 {
    let docked_to_end = max - end <= dock && start - min > dock;
    let mut pos = if docked_to_end { end - len } else { start };
    if pos + len > max {
        pos = end - len;
    } else if pos < min {
        pos = start;
    }
    if pos + len > max {
        pos = max - len;
    }
    if pos < min {
        pos = min;
    }
    pos
}

#[cfg(windows)]
pub fn hwnd(w: &Window) -> Option<isize> {
    use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};
    match w.window_handle().ok()?.as_raw() {
        RawWindowHandle::Win32(h) => Some(h.hwnd.get()),
        _ => None,
    }
}

/// Left mouse button held (a live resize). Only Windows can tell; elsewhere a short quiet
/// period after the last resize event stands in for the end of the drag.
pub fn primary_button_down() -> bool {
    #[cfg(windows)]
    unsafe {
        use windows_sys::Win32::UI::Input::KeyboardAndMouse::{GetAsyncKeyState, VK_LBUTTON};
        return (GetAsyncKeyState(VK_LBUTTON as i32) as u16 & 0x8000) != 0;
    }
    #[allow(unreachable_code)]
    false
}

/// Native window chrome tweaks applied once the window exists.
pub fn style_window(_w: &Window) {
    #[cfg(windows)]
    if let Some(hwnd) = hwnd(_w) {
        use windows_sys::Win32::Graphics::Dwm::*;
        unsafe {
            // Windows 11 rounded corners and a dark frame; ignored on Windows 10.
            let round: i32 = DWMWCP_ROUND;
            DwmSetWindowAttribute(
                hwnd as _,
                DWMWA_WINDOW_CORNER_PREFERENCE as u32,
                &round as *const _ as _,
                4,
            );
            let dark: i32 = 1;
            DwmSetWindowAttribute(
                hwnd as _,
                DWMWA_USE_IMMERSIVE_DARK_MODE as u32,
                &dark as *const _ as _,
                4,
            );
            let border: u32 = 0x0030_3030; // COLORREF 0x00BBGGRR: the app's #303030 border.
            DwmSetWindowAttribute(
                hwnd as _,
                DWMWA_BORDER_COLOR as u32,
                &border as *const _ as _,
                4,
            );
        }
    }
}

/// Initial position: centred on the primary work area (Windows); elsewhere the OS decides.
pub fn startup_position(_w: f64, _h: f64) -> Option<(i32, i32)> {
    #[cfg(windows)]
    unsafe {
        use windows_sys::Win32::Foundation::RECT;
        use windows_sys::Win32::UI::HiDpi::GetDpiForSystem;
        use windows_sys::Win32::UI::WindowsAndMessaging::*;
        let mut r: RECT = std::mem::zeroed();
        if SystemParametersInfoW(SPI_GETWORKAREA, 0, &mut r as *mut _ as _, 0) != 0 {
            let scale = GetDpiForSystem() as f64 / 96.0;
            let (w, h) = (_w * scale, _h * scale);
            let x = r.left as f64 + ((r.right - r.left) as f64 - w) / 2.0;
            let y = r.top as f64 + ((r.bottom - r.top) as f64 - h) / 2.0;
            return Some((x.round() as i32, y.round() as i32));
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    const AREA: Rect = Rect {
        x: 0.0,
        y: 0.0,
        w: 1920.0,
        h: 1040.0,
    };

    #[test]
    fn keeps_top_left_by_default() {
        let cur = Rect {
            x: 100.0,
            y: 100.0,
            w: 500.0,
            h: 410.0,
        };
        assert_eq!(
            place(cur, 390.0, 74.0, AREA, 16.0),
            Rect {
                x: 100.0,
                y: 100.0,
                w: 390.0,
                h: 74.0
            }
        );
    }

    #[test]
    fn grows_upward_near_the_bottom() {
        let cur = Rect {
            x: 100.0,
            y: 1040.0 - 74.0 - 4.0,
            w: 390.0,
            h: 74.0,
        };
        let r = place(cur, 500.0, 410.0, AREA, 16.0);
        assert_eq!(r.bottom(), cur.bottom());
    }

    #[test]
    fn stays_docked_right() {
        let cur = Rect {
            x: 1920.0 - 390.0,
            y: 300.0,
            w: 390.0,
            h: 74.0,
        };
        let r = place(cur, 500.0, 410.0, AREA, 16.0);
        assert_eq!(r.right(), 1920.0);
    }
}
