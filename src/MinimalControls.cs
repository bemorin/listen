using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ListenApp
{
    internal sealed class MinimalScrollBar : FrameworkElement
    {
        private static readonly Brush Rail = UiTokens.Colors.ScrollRail;
        private static readonly Brush Thumb = UiTokens.Colors.ScrollThumb;
        private static readonly Brush ThumbHover = UiTokens.Colors.ScrollThumbHover;
        private static readonly Brush ThumbPressed = UiTokens.Colors.ScrollThumbPressed;

        private double _extent;
        private double _viewport;
        private double _offset;
        private bool _dragging;
        private bool _hot;
        private double _dragStartY;
        private double _dragStartOffset;

        public event EventHandler OffsetChanged;

        public double Offset
        {
            get { return _offset; }
        }

        public MinimalScrollBar()
        {
            Cursor = Cursors.Hand;
            SnapsToDevicePixels = true;
            IsHitTestVisible = true;
            Visibility = Visibility.Collapsed;
        }

        public void UpdateMetrics(double extent, double viewport, double offset)
        {
            double nextExtent = Math.Max(0, extent);
            double nextViewport = Math.Max(0, viewport);
            double previousExtent = _extent;
            double previousViewport = _viewport;
            double previousOffset = _offset;

            _extent = nextExtent;
            _viewport = nextViewport;
            _offset = ClampOffset(offset);

            Visibility nextVisibility = _extent > _viewport + 0.5 ? Visibility.Visible : Visibility.Collapsed;
            bool visibilityChanged = Visibility != nextVisibility;
            if (visibilityChanged)
                Visibility = nextVisibility;

            if (visibilityChanged ||
                Math.Abs(previousExtent - _extent) > 0.01 ||
                Math.Abs(previousViewport - _viewport) > 0.01 ||
                Math.Abs(previousOffset - _offset) > 0.01)
            {
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (_extent <= _viewport || ActualHeight <= 0 || ActualWidth <= 0)
                return;

            double railWidth = Math.Min(UiTokens.Layout.QueueScrollRailWidth, ActualWidth);
            double railX = (ActualWidth - railWidth) / 2;
            dc.DrawRoundedRectangle(Rail, null, new Rect(railX, 0, railWidth, ActualHeight), railWidth / 2, railWidth / 2);

            Rect thumb = GetThumbRect();
            Brush brush = _dragging ? ThumbPressed : (_hot ? ThumbHover : Thumb);
            dc.DrawRoundedRectangle(brush, null, thumb, thumb.Width / 2, thumb.Width / 2);
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            _hot = true;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_dragging)
                _hot = false;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            Point point = e.GetPosition(this);
            Rect thumb = GetThumbRect();
            if (!thumb.Contains(point))
            {
                double travel = Math.Max(1, ActualHeight - thumb.Height);
                double targetTop = Math.Max(0, Math.Min(travel, point.Y - thumb.Height / 2));
                SetOffsetFromThumbTop(targetTop, travel);
                thumb = GetThumbRect();
            }
            _dragging = true;
            _dragStartY = point.Y;
            _dragStartOffset = _offset;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging)
                return;
            Point point = e.GetPosition(this);
            Rect thumb = GetThumbRectForOffset(_dragStartOffset);
            double travel = Math.Max(1, ActualHeight - thumb.Height);
            double range = Math.Max(0, _extent - _viewport);
            double delta = point.Y - _dragStartY;
            SetOffset(_dragStartOffset + (delta / travel) * range, true);
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_dragging)
                return;
            _dragging = false;
            ReleaseMouseCapture();
            _hot = IsMouseOver;
            InvalidateVisual();
            e.Handled = true;
        }

        private Rect GetThumbRect()
        {
            return GetThumbRectForOffset(_offset);
        }

        private Rect GetThumbRectForOffset(double offset)
        {
            double trackHeight = Math.Max(0, ActualHeight);
            if (_extent <= 0 || trackHeight <= 0)
                return new Rect(0, 0, ActualWidth, 0);
            double thumbHeight = Math.Max(28, trackHeight * Math.Min(1, _viewport / _extent));
            thumbHeight = Math.Min(trackHeight, thumbHeight);
            double travel = Math.Max(0, trackHeight - thumbHeight);
            double range = Math.Max(0, _extent - _viewport);
            double top = range <= 0 ? 0 : (ClampOffset(offset) / range) * travel;
            double width = Math.Min(UiTokens.Layout.QueueScrollThumbWidth, Math.Max(5, ActualWidth));
            double x = (ActualWidth - width) / 2;
            return new Rect(x, top, width, thumbHeight);
        }

        private void SetOffsetFromThumbTop(double top, double travel)
        {
            double range = Math.Max(0, _extent - _viewport);
            double value = travel <= 0 ? 0 : (top / travel) * range;
            SetOffset(value, true);
        }

        private void SetOffset(double value, bool notify)
        {
            double next = ClampOffset(value);
            if (Math.Abs(next - _offset) < 0.01)
                return;
            _offset = next;
            InvalidateVisual();
            if (notify)
            {
                EventHandler handler = OffsetChanged;
                if (handler != null)
                    handler(this, EventArgs.Empty);
            }
        }

        private double ClampOffset(double value)
        {
            double max = Math.Max(0, _extent - _viewport);
            if (value < 0) return 0;
            if (value > max) return max;
            return value;
        }

    }

    internal sealed class MinimalSlider : FrameworkElement
    {
        private double _value;
        private bool _dragging;
        private bool _hot;

        public event EventHandler ValueChanged;
        public bool ShowThumb = true;
        public double TrackThickness = 0;
        public double TrackCornerRadius = -1;
        public double EndpointInset = UiTokens.Layout.SliderEndpointInset;

        public bool IsDragging
        {
            get { return _dragging; }
        }

        public double Value
        {
            get { return _value; }
            set
            {
                double next = value;
                if (next < 0) next = 0;
                if (next > 1) next = 1;
                if (Math.Abs(next - _value) < 0.00001)
                    return;
                _value = next;
                InvalidateVisual();
                EventHandler handler = ValueChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        public void SetValueSilently(double value)
        {
            double next = value;
            if (next < 0) next = 0;
            if (next > 1) next = 1;
            if (Math.Abs(next - _value) < 0.00001)
                return;
            _value = next;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double width = Math.Max(1, ActualWidth);
            double center = ActualHeight / 2;
            double trackHeight = TrackThickness > 0 ? TrackThickness : (ShowThumb ? 3 : Math.Max(2, Math.Min(4, ActualHeight)));
            double trackRadius = TrackCornerRadius >= 0 ? TrackCornerRadius : trackHeight / 2;
            double thumbInset = ShowThumb ? Math.Max(UiTokens.Layout.SliderThumbHoverRadius + 1, EndpointInset) : 0.0;
            double trackWidth = Math.Max(1, width - (thumbInset * 2));
            Rect baseTrack = new Rect(thumbInset, center - trackHeight / 2, trackWidth, trackHeight);
            dc.DrawRoundedRectangle(UiTokens.Colors.Track, null, baseTrack, trackRadius, trackRadius);

            double fillWidth = trackWidth * _value;
            if (fillWidth > 0)
            {
                Rect fill = new Rect(thumbInset, center - trackHeight / 2, fillWidth, trackHeight);
                dc.DrawRoundedRectangle(UiTokens.Colors.Accent, null, fill, trackRadius, trackRadius);
            }

            if (ShowThumb)
            {
                double radius = _hot || _dragging ? UiTokens.Layout.SliderThumbHoverRadius : UiTokens.Layout.SliderThumbRadius;
                double x = thumbInset + (trackWidth * _value);
                dc.DrawEllipse(UiTokens.Colors.Thumb, null, new Point(x, center), radius, radius);
            }
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            _hot = true;
            InvalidateVisual();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _hot = false;
            InvalidateVisual();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            CaptureMouse();
            _dragging = true;
            SetFromPoint(e.GetPosition(this).X);
            e.Handled = true;
            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging && e.LeftButton == MouseButtonState.Pressed)
            {
                SetFromPoint(e.GetPosition(this).X);
                e.Handled = true;
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_dragging)
            {
                SetFromPoint(e.GetPosition(this).X);
                _dragging = false;
                ReleaseMouseCapture();
                InvalidateVisual();
                e.Handled = true;
            }
            base.OnMouseLeftButtonUp(e);
        }

        private void SetFromPoint(double x)
        {
            if (ActualWidth <= 1)
                return;
            double inset = ShowThumb ? Math.Max(UiTokens.Layout.SliderThumbHoverRadius + 1, EndpointInset) : 0.0;
            double usable = Math.Max(1, ActualWidth - (inset * 2));
            Value = (x - inset) / usable;
        }
    }

}
