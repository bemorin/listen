using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ListenApp
{
    /// <summary>
    /// Small, shared UI primitives. Styles are created once and reused instead of
    /// rebuilding ControlTemplates for every button instance.
    /// </summary>
    internal static class UiPrimitives
    {
        private static readonly Style SmallButton = BuildButtonStyle(7, Brushes.Transparent);
        private static readonly Style UtilityButton = BuildButtonStyle(8, Brushes.Transparent);
        private static readonly Style MediumButton = BuildButtonStyle(10, Brushes.Transparent);
        private static readonly Style LargeButton = BuildButtonStyle(11, Brushes.Transparent);
        private static readonly Style MiniTransportButton = BuildButtonStyle(10, UiTokens.Colors.Elevated);
        private static readonly Style PrimaryTransportButton = BuildButtonStyle(13, UiTokens.Colors.Elevated);
        private static readonly ControlTemplate SharedToolTipTemplate = BuildToolTipTemplate();

        public static readonly Style MenuButtonStyle = BuildMenuButtonStyle();
        public static readonly Style ListItemStyle = BuildListItemStyle();
        public static readonly Style TextButtonStyle = SmallButton;

        public static Style IconButtonStyle(double size)
        {
            if (size <= 28) return SmallButton;
            if (size <= 34) return UtilityButton;
            if (size <= 42) return MediumButton;
            return LargeButton;
        }

        public static ToolTip ToolTip(string text)
        {
            ToolTip tip = new ToolTip();
            tip.Content = text;
            tip.Foreground = UiTokens.Colors.Text;
            tip.Background = UiTokens.Colors.TooltipBackground;
            tip.BorderBrush = UiTokens.Colors.StrongBorder;
            tip.BorderThickness = new Thickness(1);
            tip.Padding = new Thickness(8, 5, 8, 5);
            tip.FontFamily = new FontFamily("Segoe UI Variable Text");
            tip.FontSize = 11;
            tip.Placement = PlacementMode.Top;
            tip.HasDropShadow = false;
            tip.Template = SharedToolTipTemplate;
            return tip;
        }

        private static ControlTemplate BuildToolTipTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(ToolTip));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        public static Path PathIcon(string geometryData, double width, double height, Brush brush, double strokeThickness)
        {
            Path path = new Path();
            path.Data = Geometry.Parse(geometryData);
            path.Stretch = Stretch.Uniform;
            path.Width = width > 0 ? width : double.NaN;
            path.Height = height > 0 ? height : double.NaN;
            path.Stroke = brush;
            path.Fill = Brushes.Transparent;
            path.StrokeThickness = strokeThickness;
            path.StrokeStartLineCap = PenLineCap.Round;
            path.StrokeEndLineCap = PenLineCap.Round;
            path.StrokeLineJoin = PenLineJoin.Round;
            path.VerticalAlignment = VerticalAlignment.Center;
            path.HorizontalAlignment = HorizontalAlignment.Center;
            return path;
        }

        public static Button PathButton(string geometryData, double size, double iconSize, string tooltip, bool filled)
        {
            Button button = BaseIconButton(size, tooltip, IconButtonStyle(size));
            button.Padding = new Thickness(7);
            Path path = PathIcon(geometryData, iconSize, iconSize, UiTokens.Colors.Text, filled ? 0.8 : 1.55);
            if (filled)
                path.Fill = UiTokens.Colors.Text;
            button.Content = path;
            return button;
        }

        public static Button PathButton(string geometryData, double size, string tooltip, bool filled)
        {
            return PathButton(geometryData, size, Math.Max(12, size - 14), tooltip, filled);
        }

        public static Button TextButton(string text)
        {
            Button button = new Button();
            button.Content = text;
            button.Foreground = UiTokens.Colors.Muted;
            button.BorderBrush = UiTokens.Colors.Border;
            button.BorderThickness = new Thickness(1);
            button.Padding = new Thickness(9, 4, 9, 4);
            button.FontSize = 10;
            button.Cursor = Cursors.Hand;
            button.FocusVisualStyle = null;
            button.Style = TextButtonStyle;
            return button;
        }

        public static Button RepeatButton(double size, string tooltip)
        {
            Button button = BaseIconButton(size, tooltip, IconButtonStyle(size));
            Grid icon = new Grid();
            icon.Width = size <= 24 ? 17 : 19;
            icon.Height = size <= 24 ? 19 : 21;

            Path infinity = PathIcon(
                "M1.5,8 C1.5,4.6 5.0,3.9 8.8,8 C12.6,12.1 16.5,11.4 16.5,8 C16.5,4.6 13.0,3.9 9.2,8 C5.4,12.1 1.5,11.4 1.5,8",
                size <= 24 ? 16 : 18,
                size <= 24 ? 9.5 : 10.5,
                UiTokens.Colors.Text,
                1.65);
            infinity.Tag = "infinity";
            infinity.VerticalAlignment = VerticalAlignment.Center;
            icon.Children.Add(infinity);

            TextBlock one = new TextBlock();
            one.Text = "1";
            one.Tag = "one";
            one.Foreground = UiTokens.Colors.Accent;
            one.FontFamily = new FontFamily("Bahnschrift");
            one.FontSize = size <= 24 ? 6.8 : 7.4;
            one.FontWeight = FontWeights.SemiBold;
            one.HorizontalAlignment = HorizontalAlignment.Center;
            one.VerticalAlignment = VerticalAlignment.Top;
            one.Margin = new Thickness(0, -2, 0, 0);
            one.Visibility = Visibility.Collapsed;
            icon.Children.Add(one);

            button.Content = icon;
            return button;
        }

        public static void SetRepeatButtonState(Button button, int mode, string tooltip)
        {
            if (button == null)
                return;
            Brush brush = mode == 0 ? UiTokens.Colors.Text : UiTokens.Colors.Accent;
            button.Foreground = brush;
            button.ToolTip = ToolTip(tooltip);
            Grid icon = button.Content as Grid;
            if (icon == null)
                return;

            int i;
            for (i = 0; i < icon.Children.Count; i++)
            {
                Path path = icon.Children[i] as Path;
                if (path != null && Convert.ToString(path.Tag) == "infinity")
                {
                    path.Stroke = brush;
                    path.Margin = mode == 2 ? new Thickness(0, 4, 0, 0) : new Thickness(0);
                }
                TextBlock one = icon.Children[i] as TextBlock;
                if (one != null && Convert.ToString(one.Tag) == "one")
                {
                    one.Foreground = UiTokens.Colors.Accent;
                    one.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        public static void SetPathButtonBrush(Button button, Brush brush)
        {
            if (button == null)
                return;
            Path path = button.Content as Path;
            if (path == null)
                return;
            if (path.Fill != null && path.Fill != Brushes.Transparent)
                path.Fill = brush;
            path.Stroke = brush;
        }

        public static Button VerticalDotsButton(double size, string tooltip)
        {
            Button button = BaseIconButton(size, tooltip, IconButtonStyle(size));
            StackPanel dots = new StackPanel();
            dots.Orientation = Orientation.Vertical;
            dots.HorizontalAlignment = HorizontalAlignment.Center;
            dots.VerticalAlignment = VerticalAlignment.Center;
            int i;
            for (i = 0; i < 3; i++)
            {
                Ellipse dot = new Ellipse();
                dot.Width = 2.2;
                dot.Height = 2.2;
                dot.Fill = UiTokens.Colors.Text;
                dot.Margin = new Thickness(0, 1.45, 0, 1.45);
                dots.Children.Add(dot);
            }
            button.Content = dots;
            return button;
        }

        public static Button TransportButton(double size, double iconSize, string tooltip)
        {
            Style style = size <= 42 ? MiniTransportButton : PrimaryTransportButton;
            Button button = BaseIconButton(size, tooltip, style);
            button.BorderBrush = UiTokens.Colors.Accent;
            button.BorderThickness = new Thickness(1);
            button.Content = TransportGlyph(false, iconSize);
            return button;
        }

        public static UIElement TransportGlyph(bool paused, double iconSize)
        {
            if (paused)
            {
                Grid bars = new Grid();
                bars.Width = iconSize;
                bars.Height = iconSize;
                bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
                bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Border left = new Border { Width = Math.Max(2.2, iconSize * 0.17), Height = iconSize * 0.72, Background = UiTokens.Colors.Text, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Border right = new Border { Width = Math.Max(2.2, iconSize * 0.17), Height = iconSize * 0.72, Background = UiTokens.Colors.Text, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(left, 0);
                Grid.SetColumn(right, 2);
                bars.Children.Add(left);
                bars.Children.Add(right);
                return bars;
            }

            Path play = new Path();
            play.Data = Geometry.Parse("M4,2 L16,10 L4,18 Z");
            play.Stretch = Stretch.Uniform;
            play.Width = iconSize;
            play.Height = iconSize;
            play.Fill = UiTokens.Colors.Text;
            play.StrokeThickness = 0;
            play.HorizontalAlignment = HorizontalAlignment.Center;
            play.VerticalAlignment = VerticalAlignment.Center;
            play.Margin = new Thickness(Math.Max(0.5, iconSize * 0.05), 0, 0, 0);
            return play;
        }

        public static Button SkipButton(bool next, double size, double iconSize, string tooltip)
        {
            Button button = BaseIconButton(size, tooltip, IconButtonStyle(size));
            Viewbox viewbox = new Viewbox();
            viewbox.Width = iconSize;
            viewbox.Height = iconSize;
            viewbox.Stretch = Stretch.Uniform;
            viewbox.HorizontalAlignment = HorizontalAlignment.Center;
            viewbox.VerticalAlignment = VerticalAlignment.Center;

            Canvas canvas = new Canvas();
            canvas.Width = 20;
            canvas.Height = 20;
            Rectangle bar = new Rectangle();
            bar.Width = 1.8;
            bar.Height = 12;
            bar.RadiusX = 0.9;
            bar.RadiusY = 0.9;
            bar.Fill = UiTokens.Colors.Text;
            Canvas.SetTop(bar, 4);
            Canvas.SetLeft(bar, next ? 16.2 : 2);
            canvas.Children.Add(bar);

            Path triangle = new Path();
            triangle.Data = Geometry.Parse(next ? "M5,4 L14.5,10 L5,16 Z" : "M15,4 L5.5,10 L15,16 Z");
            triangle.Fill = UiTokens.Colors.Text;
            triangle.StrokeThickness = 0;
            canvas.Children.Add(triangle);

            viewbox.Child = canvas;
            button.Content = viewbox;
            return button;
        }

        public static Button ExpandButton(string tooltip)
        {
            Button button = BaseIconButton(UiTokens.Layout.MiniExpandButtonSize, tooltip, SmallButton);

            // Draw directly on a symmetric design canvas. Avoiding an extra Viewbox here
            // keeps the optical center stable at fractional DPI scales and prevents the
            // two arrowheads from being shifted by independent stretch rounding.
            Canvas canvas = new Canvas();
            canvas.Width = UiTokens.Layout.MiniExpandIconSize;
            canvas.Height = UiTokens.Layout.MiniExpandIconSize;
            canvas.HorizontalAlignment = HorizontalAlignment.Center;
            canvas.VerticalAlignment = VerticalAlignment.Center;

            Path arrows = PathIcon(UiTokens.Icons.ExpandNorthEastSouthWest, 0, 0, UiTokens.Colors.Text, 1.65);
            arrows.Stretch = Stretch.None;
            arrows.HorizontalAlignment = HorizontalAlignment.Left;
            arrows.VerticalAlignment = VerticalAlignment.Top;
            Canvas.SetLeft(arrows, 4);
            Canvas.SetTop(arrows, 4);
            canvas.Children.Add(arrows);

            button.Content = canvas;
            return button;
        }

        private static Button BaseIconButton(double size, string tooltip, Style style)
        {
            Button button = new Button();
            button.Width = size;
            button.Height = size;
            button.Padding = new Thickness(0);
            button.Margin = new Thickness(1);
            button.Foreground = UiTokens.Colors.Text;
            button.Cursor = Cursors.Hand;
            button.FocusVisualStyle = null;
            button.ToolTip = ToolTip(tooltip);
            button.Style = style;
            return button;
        }

        private static Style BuildButtonStyle(double radius, Brush normalBackground)
        {
            Style style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, normalBackground));
            style.Setters.Add(new Setter(Control.ForegroundProperty, UiTokens.Colors.Text));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));

            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            presenter.SetBinding(ContentPresenter.MarginProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.AppendChild(presenter);
            template.VisualTree = border;

            Trigger hover = new Trigger();
            hover.Property = UIElement.IsMouseOverProperty;
            hover.Value = true;
            hover.Setters.Add(new Setter(Control.BackgroundProperty, UiTokens.Colors.Hover));
            template.Triggers.Add(hover);
            Trigger pressed = new Trigger();
            pressed.Property = ButtonBase.IsPressedProperty;
            pressed.Value = true;
            pressed.Setters.Add(new Setter(Control.BackgroundProperty, UiTokens.Colors.Pressed));
            template.Triggers.Add(pressed);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private static Style BuildMenuButtonStyle()
        {
            Style style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.ForegroundProperty, UiTokens.Colors.Text));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            presenter.SetBinding(ContentPresenter.MarginProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.AppendChild(presenter);
            template.VisualTree = border;
            Trigger hover = new Trigger();
            hover.Property = UIElement.IsMouseOverProperty;
            hover.Value = true;
            hover.Setters.Add(new Setter(Control.BackgroundProperty, UiTokens.Colors.MenuHover));
            template.Triggers.Add(hover);
            Trigger pressed = new Trigger();
            pressed.Property = ButtonBase.IsPressedProperty;
            pressed.Value = true;
            pressed.Setters.Add(new Setter(Control.BackgroundProperty, UiTokens.Colors.MenuPressed));
            template.Triggers.Add(pressed);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private static Style BuildListItemStyle()
        {
            Style style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 0, 6, 0)));
            style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 1, 0, 1)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            ControlTemplate template = new ControlTemplate(typeof(ListBoxItem));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            presenter.SetBinding(ContentPresenter.MarginProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.AppendChild(presenter);
            template.VisualTree = border;
            Trigger hover = new Trigger();
            hover.Property = UIElement.IsMouseOverProperty;
            hover.Value = true;
            hover.Setters.Add(new Setter(Control.BackgroundProperty, UiTokens.Colors.ListHover));
            template.Triggers.Add(hover);
            Trigger selected = new Trigger();
            selected.Property = Selector.IsSelectedProperty;
            selected.Value = true;
            selected.Setters.Add(new Setter(Control.BackgroundProperty, UiTokens.Colors.ListSelected));
            template.Triggers.Add(selected);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }
    }
}
