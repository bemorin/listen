using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ListenApp
{
    internal enum MorphStyle
    {
        /// <summary>Old content fades out, the shape morphs, new content fades in. Never both at once.</summary>
        Fade,
        /// <summary>Content rides with the moving edge while the shape reveals or hides the rest.</summary>
        Slide
    }

    /// <summary>
    /// Animates the window between two bounds without resizing it frame by frame.
    ///
    /// A WPF window resized on every frame shakes: Windows composes the new bounds before WPF
    /// has presented matching pixels. Instead, a transparent stand-in window of fixed size shows
    /// snapshots of the window before and after, masked by a rounded shape that morphs between
    /// the two bounds; everything moves inside one GPU surface. The real window is cloaked, takes
    /// its final bounds in a single step, and reappears exactly under the stand-in when it lands.
    /// </summary>
    internal sealed class WindowMorph
    {
        // Critically damped spring (no overshoot), normalised so it lands exactly at t = 1.
        // ω·T = 7 leaves < 1 % of travel for the tail: quick to respond, soft to settle.
        private const double SpringStiffness = 7.0;
        private static readonly double SpringNormaliser = 1.0 - (1.0 + SpringStiffness) * Math.Exp(-SpringStiffness);
        private const double CornerRadius = 8; // Windows 11 window corner radius, in DIPs.
        private static readonly Brush OutlineBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));

        private enum Phase { Idle, Staging, Animating }

        private readonly Window _owner;
        private readonly Brush _surfaceBrush;
        private Window _stage;
        private Image _fromImage;
        private Image _toImage;
        private TranslateTransform _fromShift;
        private RectangleGeometry _shape;
        private Rect _from;
        private Rect _to;
        private Rect _surface;
        private double _scale = 1;
        private double _durationMs;
        private MorphStyle _style;
        private Action _beforeResize;
        private Action _afterResize;
        private readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();
        private TimeSpan _lastFrame = TimeSpan.MinValue;
        private Phase _phase;
        private bool _releasePending;

        public WindowMorph(Window owner, Brush surfaceBrush)
        {
            _owner = owner;
            _surfaceBrush = surfaceBrush;
        }

        public bool IsRunning
        {
            get { return _phase != Phase.Idle; }
        }

        /// <summary>Honors Windows' "Animation effects" accessibility setting.</summary>
        public static bool MotionEnabled
        {
            get
            {
                try { return SystemParameters.ClientAreaAnimation; }
                catch { return true; }
            }
        }

        /// <summary>
        /// Moves the window to <paramref name="target"/> (device pixels). <paramref name="beforeResize"/>
        /// switches the window to its final layout and <paramref name="afterResize"/> applies final
        /// constraints; both run while the window is hidden behind the stand-in.
        /// </summary>
        public void Run(Rect target, double durationMs, MorphStyle style, Action beforeResize, Action afterResize)
        {
            Complete();

            IntPtr hwnd = OwnerHandle;
            Rect from = Rect.Empty;
            bool animate = durationMs > 0 && MotionEnabled && hwnd != IntPtr.Zero &&
                           _owner.IsVisible && _owner.WindowState == WindowState.Normal &&
                           NativeWindowGeometry.TryGetBounds(hwnd, out from);
            BitmapSource fromShot = null;
            if (animate)
            {
                _scale = NativeWindowGeometry.DeviceScale(_owner);
                fromShot = Snapshot(from);
            }
            if (fromShot == null)
            {
                ApplyFinalState(hwnd, target, beforeResize, afterResize);
                return;
            }

            _from = from;
            _to = target;
            _surface = Rect.Union(from, target);
            _durationMs = durationMs;
            _style = style;
            _beforeResize = beforeResize;
            _afterResize = afterResize;
            _lastFrame = TimeSpan.MinValue;
            BuildStage(fromShot);

            _phase = Phase.Staging;
            _stage.Show();
        }

        /// <summary>Lands any morph in flight immediately.</summary>
        public void Complete()
        {
            if (_phase == Phase.Idle)
                return;
            if (_phase == Phase.Staging)
                ApplyFinalState(OwnerHandle, _to, _beforeResize, _afterResize);
            Finish();
        }

        private IntPtr OwnerHandle
        {
            get { return new WindowInteropHelper(_owner).Handle; }
        }

        private void OnRendering(object sender, EventArgs e)
        {
            // Rendering can be raised more than once per frame; only advance on a new frame time.
            RenderingEventArgs args = e as RenderingEventArgs;
            TimeSpan now = args != null ? args.RenderingTime : TimeSpan.FromMilliseconds(Environment.TickCount);
            if (now == _lastFrame)
                return;
            _lastFrame = now;

            double t = _clock.Elapsed.TotalMilliseconds / _durationMs;
            if (t >= 1)
            {
                ApplyFrame(1);
                Finish();
                return;
            }
            ApplyFrame(Ease(t));
        }

        // The stand-in has presented its first frame, pixel-identical to the window: swap the
        // real window out of view, give it its final layout and bounds in one step, photograph
        // the result and start moving. The stand-in was committed first, so no gap can show.
        private void OnStageRendered(object sender, EventArgs e)
        {
            if (_phase != Phase.Staging)
                return;
            IntPtr hwnd = OwnerHandle;
            NativeWindowGeometry.SetCloaked(hwnd, true);
            ApplyFinalState(hwnd, _to, _beforeResize, _afterResize);
            _owner.UpdateLayout();
            _toImage.Source = Snapshot(_to);
            _phase = Phase.Animating;
            _clock.Restart();
            CompositionTarget.Rendering += OnRendering;
        }

        private void ApplyFinalState(IntPtr hwnd, Rect target, Action beforeResize, Action afterResize)
        {
            if (beforeResize != null)
                beforeResize();
            if (hwnd != IntPtr.Zero)
            {
                NativeWindowGeometry.SetBounds(hwnd, target);
                SyncWindowProperties(target);
            }
            if (afterResize != null)
                afterResize();
        }

        private void Finish()
        {
            CompositionTarget.Rendering -= OnRendering;
            _phase = Phase.Idle;
            _beforeResize = null;
            _afterResize = null;

            IntPtr hwnd = OwnerHandle;
            if (hwnd != IntPtr.Zero)
                NativeWindowGeometry.SetCloaked(hwnd, false);
            if (_stage != null)
            {
                // Let one composition show the real window under the stand-in before removing it.
                NativeWindowGeometry.WaitForComposition();
                _fromImage.Source = null;
                _toImage.Source = null;
                _stage.Close();
                _stage = null;
                _fromImage = null;
                _toImage = null;
                ReleaseSnapshotsWhenIdle();
            }
        }

        // Snapshots live in native memory the garbage collector cannot see (two full-window
        // bitmaps plus the stand-in's surface per morph), so it would let several morphs pile
        // up before collecting. Release them once the app is idle, never during motion.
        private void ReleaseSnapshotsWhenIdle()
        {
            if (_releasePending)
                return;
            _releasePending = true;
            _owner.Dispatcher.BeginInvoke(new Action(delegate
            {
                _releasePending = false;
                if (_phase != Phase.Idle)
                    return;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private void ApplyFrame(double p)
        {
            Rect r = Lerp(_from, _to, p);
            double left = Math.Round(r.Left) - _surface.Left;
            double top = Math.Round(r.Top) - _surface.Top;
            double right = Math.Round(r.Right) - _surface.Left;
            double bottom = Math.Round(r.Bottom) - _surface.Top;
            _shape.Rect = new Rect(left / _scale, top / _scale, (right - left) / _scale, (bottom - top) / _scale);

            if (_style == MorphStyle.Slide)
            {
                // The old content rides with the window's top-left corner; whatever the shape
                // uncovers beyond it comes from the final layout.
                _fromShift.X = Math.Round((_to.Left - _from.Left) * p) / _scale;
                _fromShift.Y = Math.Round((_to.Top - _from.Top) * p) / _scale;
            }
            else
            {
                // Sequential, never overlapping: two half-transparent layouts read as blur.
                _fromImage.Opacity = 1 - Segment(p, 0, 0.22);
                _toImage.Opacity = Segment(p, 0.5, 0.86);
            }
        }

        private void BuildStage(BitmapSource fromShot)
        {
            double width = _surface.Width / _scale;
            double height = _surface.Height / _scale;

            _shape = new RectangleGeometry(Rect.Empty, CornerRadius, CornerRadius);
            Canvas card = new Canvas();
            card.Width = width;
            card.Height = height;
            card.Clip = _shape;

            Rectangle surface = new Rectangle();
            surface.Width = width;
            surface.Height = height;
            surface.Fill = _surfaceBrush;
            card.Children.Add(surface);

            _toImage = CreateImage(_to);
            _toImage.Opacity = _style == MorphStyle.Slide ? 1 : 0;
            card.Children.Add(_toImage);

            _fromImage = CreateImage(_from);
            _fromImage.Source = fromShot;
            _fromShift = new TranslateTransform();
            _fromImage.RenderTransform = _fromShift;
            card.Children.Add(_fromImage);

            Path outline = new Path();
            outline.Data = _shape;
            outline.Stroke = OutlineBrush;
            outline.StrokeThickness = 1 / _scale;

            Canvas root = new Canvas();
            root.Children.Add(card);
            root.Children.Add(outline);
            ApplyFrame(0);

            _stage = new Window();
            _stage.WindowStyle = WindowStyle.None;
            _stage.AllowsTransparency = true;
            _stage.Background = Brushes.Transparent;
            _stage.ResizeMode = ResizeMode.NoResize;
            _stage.ShowInTaskbar = false;
            _stage.ShowActivated = false;
            _stage.Focusable = false;
            // Not owned by the window: cloaking an owner cloaks its owned windows too. Topmost
            // keeps it above the window for its brief, click-through life.
            _stage.Topmost = true;
            _stage.Left = _surface.Left / _scale;
            _stage.Top = _surface.Top / _scale;
            _stage.Width = width;
            _stage.Height = height;
            _stage.Content = root;
            _stage.SourceInitialized += delegate
            {
                NativeWindowGeometry.MakeClickThrough(new WindowInteropHelper(_stage).Handle);
            };
            _stage.ContentRendered += OnStageRendered;
        }

        private Image CreateImage(Rect bounds)
        {
            Image image = new Image();
            image.Width = Math.Round(bounds.Width) / _scale;
            image.Height = Math.Round(bounds.Height) / _scale;
            image.Stretch = Stretch.Fill;
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            Canvas.SetLeft(image, (Math.Round(bounds.Left) - _surface.Left) / _scale);
            Canvas.SetTop(image, (Math.Round(bounds.Top) - _surface.Top) / _scale);
            return image;
        }

        private BitmapSource Snapshot(Rect bounds)
        {
            Visual root = _owner.Content as Visual;
            int width = (int)Math.Round(bounds.Width);
            int height = (int)Math.Round(bounds.Height);
            if (root == null || width <= 0 || height <= 0)
                return null;
            try
            {
                RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96 * _scale, 96 * _scale, PixelFormats.Pbgra32);
                bitmap.Render(root);
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        // Keep WPF's own geometry properties in step with the native window so later layout
        // or constraint changes start from the real size rather than a stale value.
        private void SyncWindowProperties(Rect bounds)
        {
            double scale = NativeWindowGeometry.DeviceScale(_owner);
            _owner.Left = Math.Round(bounds.Left) / scale;
            _owner.Top = Math.Round(bounds.Top) / scale;
            _owner.Width = Math.Round(bounds.Width) / scale;
            _owner.Height = Math.Round(bounds.Height) / scale;
        }

        public static double Ease(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            double wt = SpringStiffness * t;
            return (1.0 - (1.0 + wt) * Math.Exp(-wt)) / SpringNormaliser;
        }

        private static double Segment(double p, double from, double to)
        {
            if (p <= from) return 0;
            if (p >= to) return 1;
            return (p - from) / (to - from);
        }

        private static Rect Lerp(Rect a, Rect b, double p)
        {
            return new Rect(
                a.Left + (b.Left - a.Left) * p,
                a.Top + (b.Top - a.Top) * p,
                a.Width + (b.Width - a.Width) * p,
                a.Height + (b.Height - a.Height) * p);
        }

        private static Brush Frozen(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Device-pixel window geometry helpers and work-area aware placement.</summary>
    internal static class NativeWindowGeometry
    {
        private const uint MonitorDefaultToNearest = 0x00000002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private const int DwmwaCloak = 13;
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;

        public static double DeviceScale(Visual visual)
        {
            PresentationSource source = PresentationSource.FromVisual(visual);
            if (source != null && source.CompositionTarget != null)
            {
                double scale = source.CompositionTarget.TransformToDevice.M11;
                if (scale > 0)
                    return scale;
            }
            return 1.0;
        }

        public static bool TryGetBounds(IntPtr hwnd, out Rect bounds)
        {
            NativeRect r;
            if (GetWindowRect(hwnd, out r))
            {
                bounds = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                return true;
            }
            bounds = Rect.Empty;
            return false;
        }

        public static void SetBounds(IntPtr hwnd, Rect bounds)
        {
            SetWindowPos(hwnd, IntPtr.Zero,
                (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top),
                (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height),
                SwpNoZOrder | SwpNoOwnerZOrder | SwpNoActivate);
        }

        /// <summary>Hides or shows a window in composition only; it keeps rendering and focus.</summary>
        public static void SetCloaked(IntPtr hwnd, bool cloaked)
        {
            try
            {
                int value = cloaked ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int));
            }
            catch { }
        }

        public static void MakeClickThrough(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;
            int style = GetWindowLong(hwnd, GwlExStyle);
            SetWindowLong(hwnd, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);
        }

        /// <summary>Blocks until the compositor has presented its next frame.</summary>
        public static void WaitForComposition()
        {
            try { DwmFlush(); }
            catch { }
        }

        /// <summary>Work area (screen minus taskbar) of the monitor showing the window.</summary>
        public static Rect GetWorkArea(IntPtr hwnd)
        {
            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            MonitorInfo info = new MonitorInfo();
            info.Size = Marshal.SizeOf(typeof(MonitorInfo));
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                return new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
            // Unknown monitor: impose no constraint rather than guess at one.
            return new Rect(-32000, -32000, 64000, 64000);
        }

        /// <summary>
        /// Places a window of <paramref name="size"/> relative to <paramref name="current"/>.
        /// By default the top-left corner stays put. An edge docked against the work area stays
        /// docked, a resize that would cross an edge grows the other way instead, and the result
        /// is always kept fully inside the work area.
        /// </summary>
        public static Rect Place(Rect current, Size size, Rect workArea, double dockTolerance)
        {
            double width = Math.Min(size.Width, workArea.Width);
            double height = Math.Min(size.Height, workArea.Height);
            double x = PlaceAxis(current.Left, current.Right, width, workArea.Left, workArea.Right, dockTolerance);
            double y = PlaceAxis(current.Top, current.Bottom, height, workArea.Top, workArea.Bottom, dockTolerance);
            return new Rect(Math.Round(x), Math.Round(y), Math.Round(width), Math.Round(height));
        }

        private static double PlaceAxis(double start, double end, double length, double min, double max, double dock)
        {
            bool dockedToEnd = max - end <= dock && start - min > dock;
            double position = dockedToEnd ? end - length : start;
            if (position + length > max)
                position = end - length;
            else if (position < min)
                position = start;
            if (position + length > max)
                position = max - length;
            if (position < min)
                position = min;
            return position;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    }
}
