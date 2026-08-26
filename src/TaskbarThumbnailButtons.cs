using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ListenApp
{
    internal sealed class TaskbarThumbnailButtons : IDisposable
    {
        private const int WM_COMMAND = 0x0111;
        private const uint IdPrevious = 41001;
        private const uint IdPlayPause = 41002;
        private const uint IdNext = 41003;
        private const uint IdLoop = 41004;

        private readonly Window _window;
        private HwndSource _source;
        private IntPtr _hwnd;
        private ITaskbarList3 _taskbar;
        private uint _taskbarButtonCreatedMessage;
        private bool _buttonsAdded;
        private bool _isPlaying;
        private int _loopMode;
        private IntPtr _previousIcon;
        private IntPtr _playIcon;
        private IntPtr _pauseIcon;
        private IntPtr _nextIcon;
        private IntPtr _loopIcon;
        private IntPtr _loopOneIcon;

        public event EventHandler PreviousClicked;
        public event EventHandler PlayPauseClicked;
        public event EventHandler NextClicked;
        public event EventHandler LoopClicked;

        public TaskbarThumbnailButtons(Window window)
        {
            _window = window;
            _window.SourceInitialized += OnSourceInitialized;
            _window.Closed += delegate { Dispose(); };
        }

        public void SetPlaying(bool playing)
        {
            _isPlaying = playing;
            UpdatePlayPauseButton();
        }

        public void SetLoopMode(int loopMode)
        {
            _loopMode = loopMode < 0 ? 0 : (loopMode > 2 ? 2 : loopMode);
            UpdateLoopButton();
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            _source = HwndSource.FromHwnd(_hwnd);
            if (_source != null)
                _source.AddHook(WndProc);

            _taskbarButtonCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");
            CreateIcons();
            TryAddButtons();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if ((uint)msg == _taskbarButtonCreatedMessage)
            {
                _buttonsAdded = false;
                TryAddButtons();
                return IntPtr.Zero;
            }

            if (msg == WM_COMMAND)
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (id == IdPrevious)
                {
                    EventHandler handler = PreviousClicked;
                    if (handler != null) handler(this, EventArgs.Empty);
                    handled = true;
                }
                else if (id == IdPlayPause)
                {
                    EventHandler handler = PlayPauseClicked;
                    if (handler != null) handler(this, EventArgs.Empty);
                    handled = true;
                }
                else if (id == IdNext)
                {
                    EventHandler handler = NextClicked;
                    if (handler != null) handler(this, EventArgs.Empty);
                    handled = true;
                }
                else if (id == IdLoop)
                {
                    EventHandler handler = LoopClicked;
                    if (handler != null) handler(this, EventArgs.Empty);
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void TryAddButtons()
        {
            if (_buttonsAdded || _hwnd == IntPtr.Zero)
                return;

            try
            {
                if (_taskbar == null)
                {
                    _taskbar = (ITaskbarList3)new CTaskbarList();
                    _taskbar.HrInit();
                }

                THUMBBUTTON[] buttons = new THUMBBUTTON[4];
                buttons[0] = MakeButton(IdPrevious, _previousIcon, "Previous");
                buttons[1] = MakeButton(IdPlayPause, _isPlaying ? _pauseIcon : _playIcon, _isPlaying ? "Pause" : "Play");
                buttons[2] = MakeButton(IdNext, _nextIcon, "Next");
                buttons[3] = MakeButton(IdLoop, _loopMode == 2 ? _loopOneIcon : _loopIcon, LoopTooltip(_loopMode));
                _taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons);
                _buttonsAdded = true;
            }
            catch
            {
                _buttonsAdded = false;
            }
        }

        private void UpdatePlayPauseButton()
        {
            if (!_buttonsAdded || _taskbar == null || _hwnd == IntPtr.Zero)
                return;

            try
            {
                THUMBBUTTON[] buttons = new THUMBBUTTON[1];
                buttons[0] = MakeButton(IdPlayPause, _isPlaying ? _pauseIcon : _playIcon, _isPlaying ? "Pause" : "Play");
                _taskbar.ThumbBarUpdateButtons(_hwnd, 1, buttons);
            }
            catch { }
        }

        private void UpdateLoopButton()
        {
            if (!_buttonsAdded || _taskbar == null || _hwnd == IntPtr.Zero)
                return;

            try
            {
                THUMBBUTTON[] buttons = new THUMBBUTTON[1];
                buttons[0] = MakeButton(IdLoop, _loopMode == 2 ? _loopOneIcon : _loopIcon, LoopTooltip(_loopMode));
                _taskbar.ThumbBarUpdateButtons(_hwnd, 1, buttons);
            }
            catch { }
        }

        private static string LoopTooltip(int mode)
        {
            if (mode == 1) return "Repeat queue";
            if (mode == 2) return "Repeat track";
            return "Repeat off";
        }

        private static THUMBBUTTON MakeButton(uint id, IntPtr icon, string tooltip)
        {
            THUMBBUTTON button = new THUMBBUTTON();
            button.dwMask = THUMBBUTTONMASK.THB_ICON | THUMBBUTTONMASK.THB_TOOLTIP | THUMBBUTTONMASK.THB_FLAGS;
            button.iId = id;
            button.hIcon = icon;
            button.szTip = tooltip;
            button.dwFlags = THUMBBUTTONFLAGS.THBF_ENABLED;
            return button;
        }

        private void CreateIcons()
        {
            _previousIcon = CreateTransportIcon(TransportGlyph.Previous);
            _playIcon = CreateTransportIcon(TransportGlyph.Play);
            _pauseIcon = CreateTransportIcon(TransportGlyph.Pause);
            _nextIcon = CreateTransportIcon(TransportGlyph.Next);
            _loopIcon = CreateTransportIcon(TransportGlyph.Loop);
            _loopOneIcon = CreateTransportIcon(TransportGlyph.LoopOne);
        }

        private enum TransportGlyph
        {
            Previous,
            Play,
            Pause,
            Next,
            Loop,
            LoopOne
        }

        private static IntPtr CreateTransportIcon(TransportGlyph glyph)
        {
            Bitmap bitmap = new Bitmap(24, 24, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush brush = new SolidBrush(Color.White))
                {
                    if (glyph == TransportGlyph.Play)
                    {
                        PointF[] points = new PointF[] { new PointF(8, 5), new PointF(19, 12), new PointF(8, 19) };
                        g.FillPolygon(brush, points);
                    }
                    else if (glyph == TransportGlyph.Pause)
                    {
                        g.FillRectangle(brush, 7, 5, 4, 14);
                        g.FillRectangle(brush, 14, 5, 4, 14);
                    }
                    else if (glyph == TransportGlyph.Previous)
                    {
                        g.FillRectangle(brush, 5, 6, 2, 12);
                        PointF[] points = new PointF[] { new PointF(18, 6), new PointF(8, 12), new PointF(18, 18) };
                        g.FillPolygon(brush, points);
                    }
                    else if (glyph == TransportGlyph.Next)
                    {
                        g.FillRectangle(brush, 17, 6, 2, 12);
                        PointF[] points = new PointF[] { new PointF(6, 6), new PointF(16, 12), new PointF(6, 18) };
                        g.FillPolygon(brush, points);
                    }
                    else
                    {
                        float y = glyph == TransportGlyph.LoopOne ? 2.0f : 0.0f;
                        using (Pen pen = new Pen(Color.White, 2.0f))
                        using (GraphicsPath path = new GraphicsPath())
                        {
                            pen.StartCap = LineCap.Round;
                            pen.EndCap = LineCap.Round;
                            pen.LineJoin = LineJoin.Round;
                            path.StartFigure();
                            path.AddBezier(4, 12 + y, 4, 8.1f + y, 8.0f, 7.2f + y, 12, 12 + y);
                            path.AddBezier(12, 12 + y, 16.0f, 16.8f + y, 20, 15.9f + y, 20, 12 + y);
                            path.AddBezier(20, 12 + y, 20, 8.1f + y, 16.0f, 7.2f + y, 12, 12 + y);
                            path.AddBezier(12, 12 + y, 8.0f, 16.8f + y, 4, 15.9f + y, 4, 12 + y);
                            g.DrawPath(pen, path);
                        }
                        if (glyph == TransportGlyph.LoopOne)
                        {
                            using (Font font = new Font("Segoe UI", 7.0f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel))
                            {
                                SizeF size = g.MeasureString("1", font);
                                g.DrawString("1", font, brush, (24 - size.Width) / 2.0f, 0.2f);
                            }
                        }
                    }
                }
            }
            IntPtr icon = bitmap.GetHicon();
            bitmap.Dispose();
            return icon;
        }

        public void Dispose()
        {
            if (_source != null)
            {
                try { _source.RemoveHook(WndProc); } catch { }
                _source = null;
            }
            DestroyIfNeeded(_previousIcon);
            DestroyIfNeeded(_playIcon);
            DestroyIfNeeded(_pauseIcon);
            DestroyIfNeeded(_nextIcon);
            DestroyIfNeeded(_loopIcon);
            DestroyIfNeeded(_loopOneIcon);
            _previousIcon = _playIcon = _pauseIcon = _nextIcon = _loopIcon = _loopOneIcon = IntPtr.Zero;
            _taskbar = null;
        }

        private static void DestroyIfNeeded(IntPtr icon)
        {
            if (icon != IntPtr.Zero)
            {
                try { DestroyIcon(icon); } catch { }
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [ComImport]
        [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        private class CTaskbarList
        {
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEA84")]
        private interface ITaskbarList3
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
            void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
            void SetProgressState(IntPtr hwnd, TBPFLAG tbpFlags);
            void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
            void UnregisterTab(IntPtr hwndTab);
            void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
            void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
            void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] pButtons);
            void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] pButtons);
            void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
            void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
            void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
            void SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
        }

        private enum TBPFLAG
        {
            TBPF_NOPROGRESS = 0,
            TBPF_INDETERMINATE = 0x1,
            TBPF_NORMAL = 0x2,
            TBPF_ERROR = 0x4,
            TBPF_PAUSED = 0x8
        }

        [Flags]
        private enum THUMBBUTTONMASK : uint
        {
            THB_BITMAP = 0x1,
            THB_ICON = 0x2,
            THB_TOOLTIP = 0x4,
            THB_FLAGS = 0x8
        }

        [Flags]
        private enum THUMBBUTTONFLAGS : uint
        {
            THBF_ENABLED = 0,
            THBF_DISABLED = 0x1,
            THBF_DISMISSONCLICK = 0x2,
            THBF_NOBACKGROUND = 0x4,
            THBF_HIDDEN = 0x8,
            THBF_NONINTERACTIVE = 0x10
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct THUMBBUTTON
        {
            public THUMBBUTTONMASK dwMask;
            public uint iId;
            public uint iBitmap;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szTip;
            public THUMBBUTTONFLAGS dwFlags;
        }
    }
}
