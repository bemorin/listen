using System.Windows.Media;

namespace ListenApp
{
    /// <summary>
    /// Shared visual tokens. Keeping immutable brushes and geometry here avoids
    /// per-window allocations and version-specific pixel patches in PlayerWindow.
    /// </summary>
    internal static class UiTokens
    {
        internal static class Colors
        {
            public static readonly Brush Background = FrozenBrush("#171717");
            public static readonly Brush Elevated = FrozenBrush("#202020");
            public static readonly Brush Hover = FrozenBrush("#2A2A2A");
            public static readonly Brush Pressed = FrozenBrush("#323232");
            public static readonly Brush Border = FrozenBrush("#303030");
            public static readonly Brush StrongBorder = FrozenBrush("#3B3B3B");
            public static readonly Brush Text = FrozenBrush("#F1F1F1");
            public static readonly Brush SecondaryText = FrozenBrush("#D2D2D2");
            public static readonly Brush IconMuted = FrozenBrush("#D0D0D0");
            public static readonly Brush ArtworkText = FrozenBrush("#D4D4D4");
            public static readonly Brush Muted = FrozenBrush("#989898");
            public static readonly Brush Time = FrozenBrush("#B9B9B9");
            public static readonly Brush Accent = FrozenBrush("#F47A3A");
            public static readonly Brush Track = FrozenBrush("#4A4A4A");
            public static readonly Brush Thumb = FrozenBrush("#DDDDDD");

            public static readonly Brush Panel = FrozenBrush("#1B1B1B");
            public static readonly Brush MenuSeparator = FrozenBrush("#3A3A3A");
            public static readonly Brush MenuHover = FrozenBrush("#2D2D2D");
            public static readonly Brush MenuPressed = FrozenBrush("#373737");
            public static readonly Brush ListHover = FrozenBrush("#232323");
            public static readonly Brush ListSelected = FrozenBrush("#2A2A2A");
            public static readonly Brush TooltipBackground = FrozenBrush("#242424");

            public static readonly Brush DropBadge = FrozenBrush("#232323");
            public static readonly Brush DropBadgeBorder = FrozenBrush("#343434");
            public static readonly Brush DropHoverText = FrozenBrush("#B8B8B8");
            public static readonly Brush DropHoverBadge = FrozenBrush("#2A2A2A");
            public static readonly Brush DropHoverBadgeBorder = FrozenBrush("#454545");
            public static readonly Brush DropActive = FrozenBrush("#251D19");
            public static readonly Brush DropActiveBadge = FrozenBrush("#33241D");
            public static readonly Brush DropOverlay = FrozenBrush("#211C19");
            public static readonly Brush DropOverlayBadge = FrozenBrush("#2B211C");
            public static readonly Brush DropOverlayBadgeBorder = FrozenBrush("#6B3B24");

            public static readonly Brush ScrollRail = FrozenBrush("#2B2B2B");
            public static readonly Brush ScrollThumb = FrozenBrush("#5E5E5E");
            public static readonly Brush ScrollThumbHover = FrozenBrush("#777777");
            public static readonly Brush ScrollThumbPressed = FrozenBrush("#8A8A8A");
        }

        internal static class Layout
        {
            public const double WindowWidth = 500;
            public const double WindowHeight = 410;
            // Normal and compact layouts; below VolumeSliderMinWindowWidth the slider hides.
            public const double WindowMinWidth = 360;
            public const double VolumeSliderMinWindowWidth = 440;
            // Responsive layout (heights in DIPs, measured from rows unless listed here). Changes
            // between layouts are spread over a band of height so a resize drag plays them.
            public const double LayoutFadeBand = 32;
            // Artwork height at which it starts fading as it shrinks towards nothing.
            public const double ArtworkFadeStart = 12;
            public const double TopBarHeight = 42;
            public const double MiniWindowWidth = 390;
            public const double MiniWindowHeight = 74;
            public const double MiniLeftInset = 12;
            // At its narrowest the mini shows only its buttons; the skip-back glyph has more
            // built-in padding than the expand glyph, so a smaller left inset centres them optically.
            public const double MiniTightLeftInset = 4;
            // Width beyond the five buttons below which the title is hidden rather than reduced to "…".
            public const double MiniTitleMinSpace = 58;
            // The mini player's minimum width is measured from its transport buttons.
            public const double MiniResizeBorder = 6;
            public const double QueuePanelHeight = 190;
            // An edge within this distance of the work-area edge counts as docked to it.
            public const double ScreenEdgeDockTolerance = 16;

            public const double ControlStripHorizontalMargin = 18;
            public const double ControlStripTopMargin = 4;
            public const double ControlStripBottomMargin = 12;
            public const double UtilityButtonSize = 32;
            public const double VolumeSliderWidth = 88;
            public const double VolumeControlGap = 5;
            public const double TransportGap = 1;

            public const double SliderThumbRadius = 4;
            public const double SliderThumbHoverRadius = 5;
            public const double SliderEndpointInset = 7;

            public const double QueueScrollColumnWidth = 12;
            public const double QueueScrollBarWidth = 8;
            public const double QueueScrollThumbWidth = 6;
            public const double QueueScrollRailWidth = 3;

            public const double MiniExpandButtonSize = 24;
            public const double MiniExpandIconSize = 16;
        }

        internal static class Motion
        {
            // Spring-eased (see WindowMorph.Ease). Large morphs get a little more time.
            public const double ModeMorphMs = 420;
            public const double QueueRevealMs = 340;
            public const double LayoutSwapFadeMs = 160;
        }

        internal static class Icons
        {
            // Two complete, optically balanced arrows in a local 8x8 geometry.
            // UiPrimitives places this geometry at (4,4) in a 16x16 canvas, making
            // the visual bounds symmetric around the exact button center.
            public const string ExpandNorthEastSouthWest =
                "M2.7,5.3 L8,0 M5,0 L8,0 L8,3 M5.3,2.7 L0,8 M0,5 L0,8 L3,8";
        }

        private static Brush FrozenBrush(string value)
        {
            Brush brush = (Brush)new BrushConverter().ConvertFromString(value);
            brush.Freeze();
            return brush;
        }
    }
}
