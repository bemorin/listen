using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace ListenApp
{
    internal sealed class PlayerWindow : Window
    {
        private static readonly Brush Bg = UiTokens.Colors.Background;
        private static readonly Brush BgElevated = UiTokens.Colors.Elevated;
        private static readonly Brush UiBorderBrush = UiTokens.Colors.Border;
        private static readonly Brush Text = UiTokens.Colors.Text;
        private static readonly Brush Muted = UiTokens.Colors.Muted;
        private static readonly Brush Accent = UiTokens.Colors.Accent;

        private readonly AppState _state;
        private readonly MediaPlayer _player;
        private readonly DispatcherTimer _timer;
        private readonly TaskbarThumbnailButtons _taskbarButtons;
        private readonly WindowMorph _morph;
        private MediaSession _media;
        private readonly bool _isFrench;

        private Grid _fullRoot;
        private Grid _miniRoot;
        private TextBlock _titleText;
        private TextBlock _subtitleText;
        private TextBlock _timeText;
        private TextBlock _durationText;
        private TextBlock _miniTitleText;
        private TextBlock _miniElapsedText;
        private TextBlock _miniDurationText;
        private TextBlock _queueCountText;
        private TextBlock _emptyQueueText;
        private Image _artImage;
        private TextBlock _artNote;
        private Border _heroDropHint;
        private Border _queueDropHint;
        private Grid _heroArea;
        private FrameworkElement _artworkSurface;
        private FrameworkElement _queueDropArea;
        private Border _heroDropOverlay;
        private Border _queueDropOverlay;
        private Button _playButton;
        private Button _miniPlayButton;
        private Button _topmostButton;
        private Button _queueButton;
        private Button _loopButton;
        private Button _volumeButton;
        private Button _miniLoopButton;
        private MinimalSlider _seek;
        private MinimalSlider _miniSeek;
        private MinimalSlider _volume;
        private Border _queuePanel;
        private Grid _miniContent;
        private Grid _miniProgressRow;
        private Grid _seekRow;
        private Grid _controlsRow;
        private FrameworkElement _logo;
        private FrameworkElement _appName;
        private StackPanel _compactTitle;
        private StackPanel _miniControls;
        private ListBox _queueList;
        private ScrollViewer _queueScrollViewer;
        private MinimalScrollBar _queueScrollBar;
        private Popup _menuPopup;
        private Button _menuAnchor;
        private bool _menuPressedWhileOpen;

        private int _currentIndex = -1;
        private bool _isPlaying;
        private bool _playWhenOpened;
        private double _normalWidth = UiTokens.Layout.WindowWidth;
        private double _normalHeight = UiTokens.Layout.WindowHeight;
        private double _heightBeforeQueueOpen = double.NaN;
        private double _miniMinWidth = double.NaN;
        private double _compactHeight = double.NaN;
        private double _heroMinHeight = double.NaN;
        private bool _userResizing;
        private int _miniResizeEdge;
        private Point _miniResizeOrigin;
        private Rect _miniResizeStart;
        private Size _sizeMoveStart;
        // Mode the UI shows or is morphing toward (the persisted flag may say mini before the UI does).
        private bool _miniApplied;
        // Last placement, so the inverse resize returns exactly to where the window came from.
        private Rect _placedFrom = Rect.Empty;
        private Rect _placedTo = Rect.Empty;
        private bool _closing;
        private double _volumeBeforeMute = 0.82;
        private readonly Dictionary<string, string> _durationCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public PlayerWindow(AppState state)
        {
            _state = state;
            _isFrench = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase);

            Title = "Listen";
            Width = UiTokens.Layout.WindowWidth;
            Height = UiTokens.Layout.WindowHeight;
            MinWidth = UiTokens.Layout.WindowMinWidth;
            // Low enough for an edge drag to reach the compact and mini layouts (see SnapSizingRect).
            MinHeight = UiTokens.Layout.MiniWindowHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            Background = Bg;
            Foreground = Text;
            AllowsTransparency = false;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            AllowDrop = true;

            WindowChrome chrome = new WindowChrome();
            chrome.CaptionHeight = 0;
            chrome.ResizeBorderThickness = new Thickness(6);
            chrome.GlassFrameThickness = new Thickness(0);
            chrome.UseAeroCaptionButtons = false;
            WindowChrome.SetWindowChrome(this, chrome);

            _player = new MediaPlayer();
            _player.Volume = _state.Volume;
            _volumeBeforeMute = _state.LastAudibleVolume > 0.001 ? _state.LastAudibleVolume : 0.82;
            if (_state.Volume > 0.001)
                _volumeBeforeMute = _state.Volume;
            _player.MediaOpened += OnMediaOpened;
            _player.MediaEnded += OnMediaEnded;
            _player.MediaFailed += OnMediaFailed;

            _timer = new DispatcherTimer(DispatcherPriority.Background);
            _timer.Interval = TimeSpan.FromMilliseconds(250);
            _timer.Tick += OnTimerTick;

            _morph = new WindowMorph(this, Bg);
            BuildUi();
            Content = BuildRoot();

            _taskbarButtons = new TaskbarThumbnailButtons(this);
            _taskbarButtons.PreviousClicked += delegate { Previous(); };
            _taskbarButtons.PlayPauseClicked += delegate { TogglePlayPause(); };
            _taskbarButtons.NextClicked += delegate { Next(false); };
            _taskbarButtons.LoopClicked += delegate { CycleLoopMode(); };

            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            Closing += OnClosing;
            PreviewKeyDown += OnPreviewKeyDown;
            SizeChanged += delegate { ApplyResponsiveLayout(ActualWidth, ActualHeight, _userResizing || _miniResizeEdge != 0); };
            PreviewMouseDown += OnPreviewMouseDownCloseMenu;
            PreviewMouseLeftButtonDown += OnFullSurfaceDrag;
            PreviewMouseMove += OnWindowPreviewMouseMove;
            PreviewMouseLeftButtonUp += OnWindowPreviewMouseUp;
            LostMouseCapture += OnWindowLostMouseCapture;
            Deactivated += delegate { if (_menuPopup != null) _menuPopup.IsOpen = false; };
            DragOver += OnDragOver;
            DragLeave += OnRootDragLeave;
            Drop += OnDrop;

            RefreshQueueList();
            if (_state.Queue.Count > 0)
            {
                _currentIndex = 0;
                UpdateTrackLabels(_state.Queue[0]);
            }
            else
            {
                UpdateTrackLabels(null);
            }

            Topmost = _state.AlwaysOnTop;
            SetQueueVisible(_state.QueueVisible, false);
            if (_state.MiniMode)
                Dispatcher.BeginInvoke(new Action(delegate { SetMiniMode(true, false); }), DispatcherPriority.Loaded);
        }

        private UIElement BuildRoot()
        {
            Grid root = new Grid();
            root.Background = Bg;
            root.Children.Add(_fullRoot);
            root.Children.Add(_miniRoot);
            return root;
        }

        private void BuildUi()
        {
            BuildFullUi();
            BuildMiniUi();
            BuildMenu();
        }

        private void BuildFullUi()
        {
            _fullRoot = new Grid();
            _fullRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(UiTokens.Layout.TopBarHeight) });
            _fullRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _fullRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _fullRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _fullRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid top = new Grid();
            top.Margin = new Thickness(10, 6, 8, 4);
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border logo = new Border();
            logo.Width = 28;
            logo.Height = 28;
            logo.CornerRadius = new CornerRadius(9);
            logo.Background = BgElevated;
            logo.BorderBrush = UiBorderBrush;
            logo.BorderThickness = new Thickness(1);
            TextBlock note = new TextBlock();
            note.Text = "♪";
            note.Foreground = Accent;
            note.FontSize = 17;
            note.FontWeight = FontWeights.SemiBold;
            note.HorizontalAlignment = HorizontalAlignment.Center;
            note.VerticalAlignment = VerticalAlignment.Center;
            logo.Child = note;
            Grid.SetColumn(logo, 0);
            top.Children.Add(logo);
            _logo = logo;

            TextBlock appName = new TextBlock();
            appName.Text = "Listen";
            appName.Foreground = Muted;
            appName.FontSize = 12;
            appName.VerticalAlignment = VerticalAlignment.Center;
            appName.Margin = new Thickness(9, 0, 0, 0);
            Grid.SetColumn(appName, 1);
            top.Children.Add(appName);
            _appName = appName;

            // Compact layout: the track takes the place of the app name, the artwork goes away.
            _compactTitle = new StackPanel();
            _compactTitle.VerticalAlignment = VerticalAlignment.Center;
            _compactTitle.Margin = new Thickness(4, 0, 10, 0);
            _compactTitle.Visibility = Visibility.Collapsed;
            TextBlock compactTitleText = new TextBlock();
            compactTitleText.Foreground = Text;
            compactTitleText.FontSize = 14;
            compactTitleText.FontFamily = new FontFamily("Segoe UI Variable Text");
            compactTitleText.FontWeight = FontWeights.SemiBold;
            compactTitleText.TextTrimming = TextTrimming.CharacterEllipsis;
            _compactTitle.Children.Add(compactTitleText);
            TextBlock compactSubtitleText = new TextBlock();
            compactSubtitleText.Foreground = Muted;
            compactSubtitleText.FontSize = 11;
            compactSubtitleText.TextTrimming = TextTrimming.CharacterEllipsis;
            _compactTitle.Children.Add(compactSubtitleText);
            Grid.SetColumnSpan(_compactTitle, 2);
            top.Children.Add(_compactTitle);

            StackPanel windowButtons = new StackPanel();
            windowButtons.Orientation = Orientation.Horizontal;
            windowButtons.VerticalAlignment = VerticalAlignment.Center;

            _topmostButton = UiPrimitives.PathButton("M5,7 L14,7 L14,16 L5,16 Z M8,4 L17,4 L17,13", 30, _isFrench ? "Toujours au-dessus" : "Always on top", false);
            _topmostButton.Click += delegate { ToggleTopmost(); };
            windowButtons.Children.Add(_topmostButton);

            Button mini = UiPrimitives.PathButton("M4,6 L16,6 L16,14 L4,14 Z M7,11 L13,11", 30, _isFrench ? "Mode mini" : "Mini mode", false);
            mini.Click += delegate { SetMiniMode(true); };
            windowButtons.Children.Add(mini);

            Button minimize = UiPrimitives.PathButton("M5,10 L15,10", 30, 11, _isFrench ? "Réduire" : "Minimize", false);
            minimize.Click += delegate { WindowState = WindowState.Minimized; };
            windowButtons.Children.Add(minimize);

            Button close = UiPrimitives.PathButton("M6,6 L14,14 M14,6 L6,14", 30, 11, _isFrench ? "Fermer" : "Close", false);
            close.Click += delegate { Close(); };
            windowButtons.Children.Add(close);

            Grid.SetColumn(windowButtons, 2);
            top.Children.Add(windowButtons);
            Grid.SetRow(top, 0);
            _fullRoot.Children.Add(top);

            Grid hero = new Grid();
            hero.Margin = new Thickness(22, 14, 22, 4);
            hero.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            hero.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            hero.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _heroArea = hero;

            // Keep the artwork fully visible even when the window is vertically tight.
            // The Viewbox only scales down; it never stretches the artwork beyond 116 px.
            Viewbox artScaler = new Viewbox();
            artScaler.Stretch = Stretch.Uniform;
            artScaler.StretchDirection = StretchDirection.DownOnly;
            artScaler.MaxWidth = 116;
            artScaler.MaxHeight = 116;
            artScaler.HorizontalAlignment = HorizontalAlignment.Center;
            artScaler.VerticalAlignment = VerticalAlignment.Center;
            _artworkSurface = artScaler;

            Border art = new Border();
            art.Width = 116;
            art.Height = 116;
            art.CornerRadius = new CornerRadius(20);
            art.Background = BgElevated;
            art.BorderBrush = UiBorderBrush;
            art.BorderThickness = new Thickness(1);
            Grid artContent = new Grid();
            _artImage = new Image();
            _artImage.Stretch = Stretch.Uniform;
            _artImage.Visibility = Visibility.Collapsed;
            _artImage.HorizontalAlignment = HorizontalAlignment.Stretch;
            _artImage.VerticalAlignment = VerticalAlignment.Stretch;
            artContent.Children.Add(_artImage);

            _artNote = new TextBlock();
            _artNote.Text = "♪";
            _artNote.Foreground = UiTokens.Colors.ArtworkText;
            _artNote.FontSize = 54;
            _artNote.HorizontalAlignment = HorizontalAlignment.Center;
            _artNote.VerticalAlignment = VerticalAlignment.Center;
            artContent.Children.Add(_artNote);
            art.Child = artContent;
            art.Clip = new RectangleGeometry(new Rect(0, 0, 116, 116), 20, 20);
            artScaler.Child = art;
            Grid.SetRow(artScaler, 0);
            hero.Children.Add(artScaler);

            _titleText = new TextBlock();
            _titleText.Foreground = Text;
            _titleText.FontSize = 22;
            _titleText.FontWeight = FontWeights.SemiBold;
            _titleText.TextAlignment = TextAlignment.Center;
            _titleText.TextTrimming = TextTrimming.CharacterEllipsis;
            _titleText.Margin = new Thickness(0, 10, 0, 2);
            Grid.SetRow(_titleText, 1);
            hero.Children.Add(_titleText);

            _subtitleText = new TextBlock();
            _subtitleText.Foreground = Muted;
            _subtitleText.FontSize = 12;
            _subtitleText.TextAlignment = TextAlignment.Center;
            _subtitleText.TextTrimming = TextTrimming.CharacterEllipsis;
            _subtitleText.VerticalAlignment = VerticalAlignment.Center;

            _heroDropHint = CreateDropHint(_subtitleText, false);
            _heroDropHint.HorizontalAlignment = HorizontalAlignment.Center;
            _heroDropHint.MouseLeftButtonUp += delegate
            {
                if (_currentIndex < 0) AddFilesFromDialog();
            };
            _heroDropHint.MouseEnter += delegate { SetDropHintHover(_heroDropHint, _currentIndex < 0); };
            _heroDropHint.MouseLeave += delegate { ResetDropHint(_heroDropHint); };
            Grid.SetRow(_heroDropHint, 2);
            hero.Children.Add(_heroDropHint);

            _heroDropOverlay = CreateDropOverlay(
                _isFrench ? "Déposez l’audio ici" : "Drop audio here",
                _isFrench ? "Ajout à la file · lecture immédiate si vide" : "Adds to queue · plays immediately when empty");
            _heroDropOverlay.Visibility = Visibility.Collapsed;
            _heroDropOverlay.IsHitTestVisible = false;
            Grid.SetRowSpan(_heroDropOverlay, 3);
            hero.Children.Add(_heroDropOverlay);

            Grid.SetRow(hero, 1);
            _fullRoot.Children.Add(hero);

            Grid seekRow = new Grid();
            seekRow.Margin = new Thickness(22, 12, 22, 0);
            seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _timeText = MakeTimeText(false, TextAlignment.Left);
            _durationText = MakeTimeText(false, TextAlignment.Right);
            _durationText.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(_timeText, 0);
            Grid.SetColumn(_durationText, 2);
            seekRow.Children.Add(_timeText);
            seekRow.Children.Add(_durationText);

            _seek = new MinimalSlider();
            _seek.Height = 26;
            _seek.Margin = new Thickness(10, 0, 10, 0);
            _seek.ValueChanged += OnSeekChanged;
            Grid.SetColumn(_seek, 1);
            seekRow.Children.Add(_seek);

            Grid.SetRow(seekRow, 2);
            _fullRoot.Children.Add(seekRow);
            _seekRow = seekRow;

            Grid controls = new Grid();
            controls.Margin = new Thickness(
                UiTokens.Layout.ControlStripHorizontalMargin,
                UiTokens.Layout.ControlStripTopMargin,
                UiTokens.Layout.ControlStripHorizontalMargin,
                UiTokens.Layout.ControlStripBottomMargin);
            // Fixed edge groups + a flexible middle region. This prevents the volume
            // slider from being squeezed/overlapped at minimum window width and keeps
            // transport optically centered between the left and right action groups.
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Volume stays visually anchored on the far left.
            StackPanel leftControls = new StackPanel();
            leftControls.Orientation = Orientation.Horizontal;
            leftControls.VerticalAlignment = VerticalAlignment.Center;
            leftControls.HorizontalAlignment = HorizontalAlignment.Left;

            _volumeButton = UiPrimitives.PathButton("M4,8 L8,8 L12,4 L12,16 L8,12 L4,12 Z M15,8 C16.5,9 16.5,11 15,12 M17,6 C20,8 20,12 17,14", UiTokens.Layout.UtilityButtonSize, 18, _isFrench ? "Muet" : "Mute", false);
            _volumeButton.Tag = "volume";
            _volumeButton.Margin = new Thickness(0, 0, UiTokens.Layout.VolumeControlGap, 0);
            _volumeButton.Click += delegate { ToggleMute(); };
            leftControls.Children.Add(_volumeButton);

            _volume = new MinimalSlider();
            _volume.Width = UiTokens.Layout.VolumeSliderWidth;
            _volume.EndpointInset = UiTokens.Layout.SliderEndpointInset;
            _volume.Height = 24;
            _volume.Value = _state.Volume;
            _volume.ValueChanged += delegate(object sender, EventArgs e)
            {
                _state.Volume = _volume.Value;
                _player.Volume = _state.Volume;
                if (_state.Volume > 0.001)
                {
                    _volumeBeforeMute = _state.Volume;
                    _state.LastAudibleVolume = _state.Volume;
                }
                UpdateVolumeVisuals();
            };
            leftControls.Children.Add(_volume);
            Grid.SetColumn(leftControls, 0);
            controls.Children.Add(leftControls);

            StackPanel transport = new StackPanel();
            transport.Orientation = Orientation.Horizontal;
            transport.VerticalAlignment = VerticalAlignment.Center;
            transport.HorizontalAlignment = HorizontalAlignment.Center;
            Button previous = UiPrimitives.SkipButton(false, 42, 18, _isFrench ? "Précédent" : "Previous");
            previous.Click += delegate { Previous(); };
            transport.Children.Add(previous);

            _playButton = UiPrimitives.TransportButton(52, 19, _isFrench ? "Lecture / pause" : "Play / pause");
            _playButton.Margin = new Thickness(5, 0, 5, 0);
            _playButton.Click += delegate { TogglePlayPause(); };
            transport.Children.Add(_playButton);

            Button next = UiPrimitives.SkipButton(true, 42, 18, _isFrench ? "Suivant" : "Next");
            next.Click += delegate { Next(false); };
            transport.Children.Add(next);

            _loopButton = UiPrimitives.RepeatButton(28, LoopTooltip());
            _loopButton.Margin = new Thickness(4, 1, 1, 1);
            _loopButton.Click += delegate { CycleLoopMode(); };
            transport.Children.Add(_loopButton);

            Grid.SetColumn(transport, 1);
            controls.Children.Add(transport);

            // Utility actions remain grouped on the far right: queue, more.
            StackPanel rightControls = new StackPanel();
            rightControls.Orientation = Orientation.Horizontal;
            rightControls.HorizontalAlignment = HorizontalAlignment.Right;
            rightControls.VerticalAlignment = VerticalAlignment.Center;

            _queueButton = UiPrimitives.PathButton("M5,6 L15,6 M5,10 L15,10 M5,14 L15,14", UiTokens.Layout.UtilityButtonSize, 15, _isFrench ? "File d’attente" : "Play queue", false);
            _queueButton.Click += delegate { SetQueueVisible(!_state.QueueVisible); };
            rightControls.Children.Add(_queueButton);

            Button more = UiPrimitives.VerticalDotsButton(UiTokens.Layout.UtilityButtonSize, _isFrench ? "Plus" : "More");
            ConfigureMenuButton(more, true, true);
            rightControls.Children.Add(more);
            Grid.SetColumn(rightControls, 2);
            controls.Children.Add(rightControls);

            Grid.SetRow(controls, 3);
            _fullRoot.Children.Add(controls);
            _controlsRow = controls;
            compactTitleText.SetBinding(TextBlock.TextProperty, new Binding("Text") { Source = _titleText });
            compactSubtitleText.SetBinding(TextBlock.TextProperty, new Binding("Text") { Source = _subtitleText });

            _queuePanel = BuildQueuePanel();
            Grid.SetRow(_queuePanel, 4);
            _fullRoot.Children.Add(_queuePanel);
        }

        private void BuildMiniUi()
        {
            _miniRoot = new Grid();
            _miniRoot.Visibility = Visibility.Collapsed;
            _miniRoot.Background = Bg;
            _miniRoot.PreviewMouseLeftButtonDown += OnMiniDrag;

            Border shell = new Border();
            shell.Margin = new Thickness(0);
            shell.Background = Bg;
            shell.BorderThickness = new Thickness(0);
            shell.CornerRadius = new CornerRadius(12);

            Grid content = new Grid();
            _miniContent = content;
            content.Margin = new Thickness(UiTokens.Layout.MiniLeftInset, 5, 8, 5);
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });

            _miniTitleText = new TextBlock();
            _miniTitleText.Foreground = Text;
            _miniTitleText.FontSize = 13;
            _miniTitleText.FontFamily = new FontFamily("Segoe UI Variable Text");
            _miniTitleText.FontWeight = FontWeights.SemiBold;
            _miniTitleText.TextTrimming = TextTrimming.CharacterEllipsis;
            _miniTitleText.VerticalAlignment = VerticalAlignment.Center;
            _miniTitleText.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(_miniTitleText, 0);
            Grid.SetRow(_miniTitleText, 0);
            content.Children.Add(_miniTitleText);

            StackPanel miniControls = new StackPanel();
            _miniControls = miniControls;
            miniControls.Orientation = Orientation.Horizontal;
            miniControls.VerticalAlignment = VerticalAlignment.Center;
            miniControls.Margin = new Thickness(0);

            Button prev = UiPrimitives.SkipButton(false, 34, 17, _isFrench ? "Précédent" : "Previous");
            prev.Margin = new Thickness(2, 1, 2, 1);
            prev.Click += delegate { Previous(); };
            miniControls.Children.Add(prev);

            _miniPlayButton = UiPrimitives.TransportButton(40, 20, _isFrench ? "Lecture / pause" : "Play / pause");
            _miniPlayButton.Margin = new Thickness(2, 1, 2, 1);
            _miniPlayButton.Click += delegate { TogglePlayPause(); };
            miniControls.Children.Add(_miniPlayButton);

            Button next = UiPrimitives.SkipButton(true, 34, 17, _isFrench ? "Suivant" : "Next");
            next.Margin = new Thickness(2, 1, 2, 1);
            next.Click += delegate { Next(false); };
            miniControls.Children.Add(next);

            _miniLoopButton = UiPrimitives.RepeatButton(24, LoopTooltip());
            _miniLoopButton.Margin = new Thickness(2, 1, 2, 1);
            _miniLoopButton.Click += delegate { CycleLoopMode(); };
            miniControls.Children.Add(_miniLoopButton);

            Button expand = UiPrimitives.ExpandButton(_isFrench ? "Agrandir" : "Expand");
            expand.Margin = new Thickness(2, 1, 1, 1);
            expand.Click += delegate { SetMiniMode(false); };
            miniControls.Children.Add(expand);

            Grid.SetColumn(miniControls, 1);
            Grid.SetRow(miniControls, 0);
            content.Children.Add(miniControls);

            Grid progressRow = new Grid();
            _miniProgressRow = progressRow;
            progressRow.Margin = new Thickness(0, 0, 1, 0);
            progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(39) });
            progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(39) });

            _miniElapsedText = MakeTimeText(true, TextAlignment.Left);
            _miniDurationText = MakeTimeText(true, TextAlignment.Right);
            Grid.SetColumn(_miniElapsedText, 0);
            Grid.SetColumn(_miniDurationText, 2);
            progressRow.Children.Add(_miniElapsedText);
            progressRow.Children.Add(_miniDurationText);

            _miniSeek = new MinimalSlider();
            _miniSeek.Margin = new Thickness(3, 0, 3, 0);
            _miniSeek.ShowThumb = false;
            _miniSeek.TrackThickness = 3.0;
            _miniSeek.TrackCornerRadius = 1.1;
            _miniSeek.ValueChanged += OnMiniSeekChanged;
            Grid.SetColumn(_miniSeek, 1);
            progressRow.Children.Add(_miniSeek);

            Grid.SetRow(progressRow, 1);
            Grid.SetColumnSpan(progressRow, 2);
            content.Children.Add(progressRow);

            shell.Child = content;
            _miniRoot.Children.Add(shell);
        }

        private Border BuildQueuePanel()
        {
            Border panel = new Border();
            panel.Background = UiTokens.Colors.Panel;
            panel.BorderBrush = UiBorderBrush;
            panel.BorderThickness = new Thickness(0, 1, 0, 0);
            panel.Padding = new Thickness(18, 10, 18, 12);
            panel.Height = UiTokens.Layout.QueuePanelHeight;

            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel heading = new StackPanel();
            heading.Orientation = Orientation.Horizontal;
            TextBlock queueTitle = new TextBlock();
            queueTitle.Text = _isFrench ? "File d’attente" : "Play queue";
            queueTitle.Foreground = Text;
            queueTitle.FontWeight = FontWeights.SemiBold;
            queueTitle.FontSize = 12;
            heading.Children.Add(queueTitle);
            _queueCountText = new TextBlock();
            _queueCountText.Foreground = Muted;
            _queueCountText.FontSize = 11;
            _queueCountText.Margin = new Thickness(8, 0, 0, 0);
            heading.Children.Add(_queueCountText);
            header.Children.Add(heading);

            StackPanel queueActions = new StackPanel();
            queueActions.Orientation = Orientation.Horizontal;
            Button add = UiPrimitives.TextButton(_isFrench ? "Ajouter" : "Add");
            add.Click += delegate { AddFilesFromDialog(); };
            queueActions.Children.Add(add);
            Button remove = UiPrimitives.TextButton(_isFrench ? "Retirer" : "Remove");
            remove.Margin = new Thickness(6, 0, 0, 0);
            remove.Click += delegate { RemoveSelectedQueueItem(); };
            queueActions.Children.Add(remove);
            Button clear = UiPrimitives.TextButton(_isFrench ? "Vider" : "Clear");
            clear.Margin = new Thickness(6, 0, 0, 0);
            clear.Click += delegate { ClearQueue(); };
            queueActions.Children.Add(clear);
            Grid.SetColumn(queueActions, 1);
            header.Children.Add(queueActions);
            grid.Children.Add(header);

            Grid listHost = new Grid();
            listHost.Margin = new Thickness(0, 8, 0, 0);
            _queueList = new ListBox();
            _queueList.Background = Brushes.Transparent;
            _queueList.BorderThickness = new Thickness(0);
            _queueList.Foreground = Text;
            _queueList.SelectionMode = SelectionMode.Single;
            _queueList.ItemContainerStyle = UiPrimitives.ListItemStyle;
            _queueList.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            _queueList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            _queueList.SetValue(ScrollViewer.CanContentScrollProperty, false);
            _queueList.Loaded += delegate
            {
                AttachQueueScrollViewer();
                Dispatcher.BeginInvoke(new Action(delegate { AttachQueueScrollViewer(); }), DispatcherPriority.ContextIdle);
            };
            _queueList.LayoutUpdated += delegate
            {
                if (_queueScrollViewer == null)
                    AttachQueueScrollViewer();
                else
                    UpdateQueueScrollBar();
            };
            _queueList.MouseDoubleClick += delegate
            {
                if (_queueList.SelectedIndex >= 0)
                    PlayIndex(_queueList.SelectedIndex);
            };

            listHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            listHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(UiTokens.Layout.QueueScrollColumnWidth) });
            Grid.SetColumn(_queueList, 0);
            listHost.Children.Add(_queueList);

            _queueScrollBar = new MinimalScrollBar();
            _queueScrollBar.Width = UiTokens.Layout.QueueScrollBarWidth;
            _queueScrollBar.Margin = new Thickness(3, 2, 1, 2);
            _queueScrollBar.HorizontalAlignment = HorizontalAlignment.Right;
            _queueScrollBar.OffsetChanged += delegate
            {
                if (_queueScrollViewer != null)
                    _queueScrollViewer.ScrollToVerticalOffset(_queueScrollBar.Offset);
            };
            Grid.SetColumn(_queueScrollBar, 1);
            listHost.Children.Add(_queueScrollBar);

            _emptyQueueText = new TextBlock();
            _emptyQueueText.Text = _isFrench ? "Glissez des fichiers audio ici" : "Drop audio files here";
            _emptyQueueText.Foreground = Muted;
            _emptyQueueText.FontSize = 11;
            _emptyQueueText.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyQueueText.VerticalAlignment = VerticalAlignment.Center;

            _queueDropHint = CreateDropHint(_emptyQueueText, true);
            _queueDropHint.HorizontalAlignment = HorizontalAlignment.Center;
            _queueDropHint.VerticalAlignment = VerticalAlignment.Center;
            _queueDropHint.MouseLeftButtonUp += delegate { AddFilesFromDialog(); };
            _queueDropHint.MouseEnter += delegate { SetDropHintHover(_queueDropHint, _state.Queue.Count == 0); };
            _queueDropHint.MouseLeave += delegate { ResetDropHint(_queueDropHint); };
            listHost.Children.Add(_queueDropHint);

            Grid.SetRow(listHost, 1);
            grid.Children.Add(listHost);

            _queueDropArea = grid;
            _queueDropOverlay = CreateDropOverlay(
                _isFrench ? "Déposez pour ajouter" : "Drop to add",
                _isFrench ? "Ajouter à la file d’attente" : "Add to play queue");
            _queueDropOverlay.Visibility = Visibility.Collapsed;
            _queueDropOverlay.IsHitTestVisible = false;
            Grid.SetRowSpan(_queueDropOverlay, 2);
            grid.Children.Add(_queueDropOverlay);

            panel.Child = grid;
            return panel;
        }

        private void BuildMenu()
        {
            _menuPopup = new Popup();
            _menuPopup.AllowsTransparency = true;
            _menuPopup.PopupAnimation = PopupAnimation.Fade;
            _menuPopup.StaysOpen = true;
            _menuPopup.Closed += delegate { _menuAnchor = null; };
        }

        private void OnPreviewMouseDownCloseMenu(object sender, MouseButtonEventArgs e)
        {
            if (_menuPopup == null || !_menuPopup.IsOpen)
                return;
            DependencyObject source = e.OriginalSource as DependencyObject;
            Button clickedButton = FindParent<Button>(source);
            if (clickedButton != _menuAnchor)
                _menuPopup.IsOpen = false;
        }

        private void ConfigureMenuButton(Button button, bool above, bool alignRight)
        {
            button.PreviewMouseLeftButtonDown += delegate
            {
                _menuPressedWhileOpen = _menuPopup != null && _menuPopup.IsOpen && _menuAnchor == button;
            };
            button.Click += delegate
            {
                if (_menuPressedWhileOpen)
                {
                    _menuPopup.IsOpen = false;
                    _menuPressedWhileOpen = false;
                    return;
                }
                _menuPressedWhileOpen = false;
                OpenMenu(button, above, alignRight);
            };
        }

        private void OpenMenu(Button anchor, bool above, bool alignRight)
        {
            if (_menuPopup == null)
                return;

            _menuAnchor = anchor;
            _menuPopup.Child = BuildMenuCard();
            _menuPopup.PlacementTarget = anchor;
            _menuPopup.Placement = above ? PlacementMode.Top : PlacementMode.Bottom;
            _menuPopup.HorizontalOffset = alignRight ? anchor.ActualWidth - 286 : 0;
            _menuPopup.VerticalOffset = above ? -7 : 7;
            _menuPopup.IsOpen = true;
        }

        private Border BuildMenuCard()
        {
            Border card = new Border();
            card.Width = 286;
            card.Background = UiTokens.Colors.Elevated;
            card.BorderBrush = UiTokens.Colors.StrongBorder;
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(12);
            card.Padding = new Thickness(6);
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 8,
                Opacity = 0.34,
                Color = System.Windows.Media.Colors.Black
            };

            StackPanel stack = new StackPanel();
            stack.Children.Add(CreateMenuRow("topmost", _isFrench ? "Toujours au-dessus" : "Always on top", _state.AlwaysOnTop, delegate { ToggleTopmost(); }));
            stack.Children.Add(CreateMenuRow("mini", _isFrench ? "Mode mini" : "Mini mode", _state.MiniMode, delegate { SetMiniMode(!_state.MiniMode); }));
            stack.Children.Add(CreateMenuRow("queue", _isFrench ? "Afficher la file d’attente" : "Show play queue", _state.QueueVisible, delegate { SetQueueVisible(!_state.QueueVisible); }));
            stack.Children.Add(CreateMenuSeparator());

            stack.Children.Add(CreateMenuRow("default", _isFrench ? "Choisir Listen par défaut…" : "Choose Listen as default…", false, delegate
            {
                try
                {
                    string exe = Process.GetCurrentProcess().MainModule.FileName;
                    ShellRegistration.Register(exe);
                }
                catch { }
                ShellRegistration.OpenDefaultAppsSettings();
            }));

            stack.Children.Add(CreateMenuSeparator());
            stack.Children.Add(CreateMenuRow("quit", _isFrench ? "Quitter" : "Quit", false, delegate { Close(); }));
            card.Child = stack;
            return card;
        }

        private Button CreateMenuRow(string iconKind, string label, bool active, Action action)
        {
            Button button = new Button();
            button.Width = 272;
            button.Height = 38;
            button.Padding = new Thickness(10, 0, 9, 0);
            button.Margin = new Thickness(0, 1, 0, 1);
            button.Foreground = Text;
            button.Cursor = Cursors.Hand;
            button.FocusVisualStyle = null;
            button.Style = UiPrimitives.MenuButtonStyle;

            Grid row = new Grid();
            row.Width = 252;
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(29) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });

            UIElement glyph = CreateMenuGlyph(iconKind, active ? Accent : UiTokens.Colors.IconMuted);
            row.Children.Add(glyph);

            TextBlock text = new TextBlock();
            text.Text = label;
            text.Foreground = Text;
            text.FontFamily = new FontFamily("Segoe UI Variable Text");
            text.FontSize = 12.5;
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            if (active)
            {
                TextBlock check = new TextBlock();
                check.Text = "✓";
                check.Foreground = Accent;
                check.FontSize = 13;
                check.FontWeight = FontWeights.SemiBold;
                check.HorizontalAlignment = HorizontalAlignment.Right;
                check.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(check, 2);
                row.Children.Add(check);
            }

            button.Content = row;
            button.Click += delegate
            {
                if (_menuPopup != null) _menuPopup.IsOpen = false;
                if (action != null) action();
            };
            return button;
        }

        private static UIElement CreateMenuGlyph(string kind, Brush brush)
        {
            string data;
            double size = 16;
            if (string.Equals(kind, "topmost", StringComparison.Ordinal))
                data = "M5,7 L14,7 L14,16 L5,16 Z M8,4 L17,4 L17,13";
            else if (string.Equals(kind, "mini", StringComparison.Ordinal))
                data = "M4,6 L16,6 L16,14 L4,14 Z M7,11 L13,11";
            else if (string.Equals(kind, "queue", StringComparison.Ordinal))
                data = "M5,6 L15,6 M5,10 L15,10 M5,14 L15,14";
            else if (string.Equals(kind, "default", StringComparison.Ordinal))
                data = "M9,3 C12.3,3 15,5.7 15,9 C15,12.3 12.3,15 9,15 C5.7,15 3,12.3 3,9 C3,5.7 5.7,3 9,3 M9,6 C10.7,6 12,7.3 12,9 C12,10.7 10.7,12 9,12 C7.3,12 6,10.7 6,9 C6,7.3 7.3,6 9,6";
            else
                data = "M4,4 L14,14 M14,4 L4,14";

            System.Windows.Shapes.Path path = UiPrimitives.PathIcon(data, size, size, brush, 1.45);
            path.HorizontalAlignment = HorizontalAlignment.Left;
            path.VerticalAlignment = VerticalAlignment.Center;
            return path;
        }

        private static Border CreateMenuSeparator()
        {
            return new Border
            {
                Height = 1,
                Background = UiTokens.Colors.MenuSeparator,
                Margin = new Thickness(8, 5, 8, 5)
            };
        }

        public void ApplyLaunchRequest(LaunchRequest request)
        {
            if (request == null || request.Files.Count == 0)
                return;

            if (request.Mode == LaunchMode.Play)
            {
                _state.Queue.Clear();
                int i;
                for (i = 0; i < request.Files.Count; i++)
                    _state.Queue.Add(request.Files[i]);
                _currentIndex = 0;
                RefreshQueueList();
                PlayIndex(0);
            }
            else if (request.Mode == LaunchMode.Enqueue)
            {
                int firstNew = _state.Queue.Count;
                int i;
                for (i = 0; i < request.Files.Count; i++)
                    _state.Queue.Add(request.Files[i]);

                if (_currentIndex < 0 && _state.Queue.Count > 0)
                {
                    _currentIndex = firstNew < _state.Queue.Count ? firstNew : 0;
                    UpdateTrackLabels(_state.Queue[_currentIndex]);
                }
                RefreshQueueList();
            }
            _state.Save();
        }

        private void AddFilesFromDialog()
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Title = _isFrench ? "Ajouter des fichiers audio" : "Add audio files";
            dialog.Filter = AudioFormats.OpenDialogFilter;
            dialog.Multiselect = true;
            bool? result = dialog.ShowDialog(this);
            if (result != true || dialog.FileNames.Length == 0)
                return;

            int start = _state.Queue.Count;
            int i;
            for (i = 0; i < dialog.FileNames.Length; i++)
            {
                if (File.Exists(dialog.FileNames[i]) && AudioFormats.IsSupported(dialog.FileNames[i]))
                    _state.Queue.Add(dialog.FileNames[i]);
            }
            if (_currentIndex < 0 && _state.Queue.Count > 0)
            {
                _currentIndex = start < _state.Queue.Count ? start : 0;
                PlayIndex(_currentIndex);
            }
            else
            {
                RefreshQueueList();
                _state.Save();
            }
        }

        private void PlayIndex(int index)
        {
            if (index < 0 || index >= _state.Queue.Count)
                return;
            string path = _state.Queue[index];
            if (!File.Exists(path))
            {
                _state.Queue.RemoveAt(index);
                if (_state.Queue.Count == 0)
                {
                    StopPlayback();
                    _currentIndex = -1;
                    UpdateTrackLabels(null);
                    RefreshQueueList();
                    return;
                }
                if (index >= _state.Queue.Count) index = _state.Queue.Count - 1;
                PlayIndex(index);
                return;
            }

            _currentIndex = index;
            _playWhenOpened = true;
            _isPlaying = false;
            try
            {
                _player.Close();
                _player.Open(new Uri(path, UriKind.Absolute));
            }
            catch
            {
                _playWhenOpened = false;
                _isPlaying = false;
            }
            UpdateTrackLabels(path);
            RefreshQueueList();
            UpdatePlaybackVisuals();
            _state.Save();
        }

        private void TogglePlayPause()
        {
            if (_state.Queue.Count == 0)
            {
                AddFilesFromDialog();
                if (_state.Queue.Count == 0)
                    return;
            }

            if (_currentIndex < 0 || _currentIndex >= _state.Queue.Count)
                _currentIndex = 0;

            if (_isPlaying)
            {
                try { _player.Pause(); } catch { }
                _isPlaying = false;
                _timer.Stop();
                UpdatePlaybackVisuals();
                return;
            }

            if (_player.Source == null)
            {
                PlayIndex(_currentIndex);
                return;
            }

            try
            {
                _player.Play();
                _isPlaying = true;
                _timer.Start();
            }
            catch { _isPlaying = false; }
            UpdatePlaybackVisuals();
        }

        private void Previous()
        {
            if (_state.Queue.Count == 0)
                return;

            try
            {
                if (_player.Position.TotalSeconds > 4)
                {
                    _player.Position = TimeSpan.Zero;
                    return;
                }
            }
            catch { }

            // Before the first track, wrap to the last one (a single track simply restarts).
            int target = _currentIndex - 1;
            if (target < 0) target = _state.Queue.Count - 1;
            PlayIndex(target);
        }

        private void Next(bool fromEnded)
        {
            if (_state.Queue.Count == 0)
                return;
            int target = _currentIndex + 1;
            if (target < _state.Queue.Count)
            {
                PlayIndex(target);
                return;
            }

            if (!fromEnded)
            {
                // A deliberate skip past the last track wraps to the first, mirroring Previous.
                if (_state.Queue.Count > 1)
                    PlayIndex(0);
                return;
            }

            // Natural end of the queue: continue only when repeating the queue.
            if (_state.LoopMode == 1)
            {
                PlayIndex(0);
                return;
            }
            StopPlayback();
            _seek.Value = 1;
            _miniSeek.Value = 1;
        }

        private void CycleLoopMode()
        {
            _state.LoopMode++;
            if (_state.LoopMode > 2)
                _state.LoopMode = 0;
            UpdateLoopVisuals();
            _state.Save();
        }

        private string LoopTooltip()
        {
            if (_state.LoopMode == 1)
                return _isFrench ? "Répéter la file d’attente" : "Repeat play queue";
            if (_state.LoopMode == 2)
                return _isFrench ? "Répéter ce titre" : "Repeat this track";
            return _isFrench ? "Répétition désactivée" : "Repeat off";
        }

        private void UpdateLoopVisuals()
        {
            UiPrimitives.SetRepeatButtonState(_loopButton, _state.LoopMode, LoopTooltip());
            UiPrimitives.SetRepeatButtonState(_miniLoopButton, _state.LoopMode, LoopTooltip());
            if (_taskbarButtons != null)
                _taskbarButtons.SetLoopMode(_state.LoopMode);
        }

        private void StopPlayback()
        {
            try { _player.Stop(); } catch { }
            _isPlaying = false;
            _timer.Stop();
            UpdatePlaybackVisuals();
        }

        private void OnMediaOpened(object sender, EventArgs e)
        {
            if (_playWhenOpened)
            {
                _playWhenOpened = false;
                try
                {
                    _player.Play();
                    _isPlaying = true;
                    _timer.Start();
                }
                catch { _isPlaying = false; }
            }
            UpdatePlaybackVisuals();
            UpdateTimeLabels();
        }

        private void OnMediaEnded(object sender, EventArgs e)
        {
            _isPlaying = false;
            _timer.Stop();
            UpdatePlaybackVisuals();

            if (_state.LoopMode == 2 && _currentIndex >= 0 && _currentIndex < _state.Queue.Count)
            {
                PlayIndex(_currentIndex);
                return;
            }
            Next(true);
        }

        private void OnMediaFailed(object sender, ExceptionEventArgs e)
        {
            _isPlaying = false;
            _timer.Stop();
            UpdatePlaybackVisuals();
            _subtitleText.Text = _isFrench ? "Format ou codec non pris en charge" : "Unsupported format or codec";
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            UpdateTimeLabels();
        }

        private void UpdateTimeLabels()
        {
            TimeSpan position = TimeSpan.Zero;
            TimeSpan duration = TimeSpan.Zero;
            try { position = _player.Position; } catch { }
            try
            {
                if (_player.NaturalDuration.HasTimeSpan)
                    duration = _player.NaturalDuration.TimeSpan;
            }
            catch { }

            _timeText.Text = FormatTime(position);
            _durationText.Text = FormatTime(duration);
            _miniElapsedText.Text = FormatTime(position);
            _miniDurationText.Text = FormatTime(duration);

            if (!_seek.IsDragging && !_miniSeek.IsDragging && duration.TotalMilliseconds > 1)
            {
                double value = position.TotalMilliseconds / duration.TotalMilliseconds;
                if (value < 0) value = 0;
                if (value > 1) value = 1;
                _seek.SetValueSilently(value);
                _miniSeek.SetValueSilently(value);
            }
        }

        private void OnSeekChanged(object sender, EventArgs e)
        {
            if (_seek.IsDragging)
                SeekTo(_seek.Value);
            _miniSeek.SetValueSilently(_seek.Value);
        }

        private void OnMiniSeekChanged(object sender, EventArgs e)
        {
            if (_miniSeek.IsDragging)
                SeekTo(_miniSeek.Value);
            _seek.SetValueSilently(_miniSeek.Value);
        }

        private void SeekTo(double fraction)
        {
            try
            {
                if (_player.NaturalDuration.HasTimeSpan)
                {
                    TimeSpan duration = _player.NaturalDuration.TimeSpan;
                    _player.Position = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * fraction);
                    UpdateTimeLabels();
                }
            }
            catch { }
        }

        private void UpdatePlaybackVisuals()
        {
            if (_playButton != null)
                _playButton.Content = UiPrimitives.TransportGlyph(_isPlaying, 19);
            if (_miniPlayButton != null)
                _miniPlayButton.Content = UiPrimitives.TransportGlyph(_isPlaying, 20);
            _taskbarButtons.SetPlaying(_isPlaying);
            if (_media != null)
                _media.SetPlayback(_isPlaying, _currentIndex >= 0 && _currentIndex < _state.Queue.Count, _state.Queue.Count);
        }

        private void UpdateArtwork(string path)
        {
            if (_artImage == null || _artNote == null)
                return;

            BitmapSource artwork = null;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                artwork = TryGetShellArtwork(path, 256);

            if (artwork != null)
            {
                _artImage.Source = artwork;
                _artImage.Visibility = Visibility.Visible;
                _artNote.Visibility = Visibility.Collapsed;
            }
            else
            {
                _artImage.Source = null;
                _artImage.Visibility = Visibility.Collapsed;
                _artNote.Visibility = Visibility.Visible;
            }
        }

        private static BitmapSource TryGetShellArtwork(string path, int pixels)
        {
            IShellItemImageFactory factory = null;
            IntPtr hBitmap = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(path, IntPtr.Zero, iid, out factory);
                if (factory == null)
                    return null;

                NativeSize size = new NativeSize();
                size.cx = pixels;
                size.cy = pixels;
                int hr = factory.GetImage(size,
                    ShellImageFlags.ThumbnailOnly | ShellImageFlags.BiggerSizeOk | ShellImageFlags.ScaleUp,
                    out hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero)
                    return null;

                BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                if (source != null && source.CanFreeze)
                    source.Freeze();
                return source;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    try { DeleteObject(hBitmap); } catch { }
                }
                if (factory != null)
                {
                    try { Marshal.ReleaseComObject(factory); } catch { }
                }
            }
        }

        private void UpdateTrackLabels(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                _titleText.Text = _isFrench ? "Aucun titre" : "Nothing playing";
                _subtitleText.Text = _isFrench ? "Ajouter un fichier ou le glisser ici" : "Add a file or drop it here";
                if (_heroDropHint != null)
                {
                    _heroDropHint.Cursor = Cursors.Hand;
                    SetDropHintInteractive(_heroDropHint, true);
                    ResetDropHint(_heroDropHint);
                }
                UpdateArtwork(null);
                _miniTitleText.Text = "Listen";
                Title = "Listen";
                if (_media != null)
                    _media.SetTrack(null, null);
                return;
            }

            string fileName = Path.GetFileNameWithoutExtension(path);
            string title = fileName;
            string subtitle = Path.GetFileName(Path.GetDirectoryName(path));
            int separator = fileName.IndexOf(" - ", StringComparison.Ordinal);
            if (separator > 0 && separator + 3 < fileName.Length)
            {
                subtitle = fileName.Substring(0, separator).Trim();
                title = fileName.Substring(separator + 3).Trim();
            }

            _titleText.Text = title;
            _subtitleText.Text = string.IsNullOrWhiteSpace(subtitle) ? Path.GetExtension(path).TrimStart('.').ToUpperInvariant() : subtitle;
            if (_heroDropHint != null)
            {
                _heroDropHint.Cursor = Cursors.Arrow;
                SetDropHintInteractive(_heroDropHint, false);
                ResetDropHint(_heroDropHint);
            }
            UpdateArtwork(path);
            _miniTitleText.Text = title;
            Title = "Listen — " + title;
            if (_media != null)
                _media.SetTrack(path, title);
        }

        private string GetCachedDurationText(string path)
        {
            string cached;
            if (_durationCache.TryGetValue(path, out cached))
                return cached;

            string durationText = string.Empty;
            try
            {
                ulong duration100Ns;
                if (TryGetShellMediaDuration(path, out duration100Ns) && duration100Ns > 0)
                    durationText = FormatTime(TimeSpan.FromTicks((long)Math.Min(duration100Ns, (ulong)long.MaxValue)));
            }
            catch { }

            _durationCache[path] = durationText;
            return durationText;
        }

        private static bool TryGetShellMediaDuration(string path, out ulong duration100Ns)
        {
            duration100Ns = 0;
            IPropertyStore store = null;
            PropVariant value = new PropVariant();
            try
            {
                Guid iid = typeof(IPropertyStore).GUID;
                int hr = SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out store);
                if (hr != 0 || store == null)
                    return false;

                PropertyKey key = new PropertyKey
                {
                    fmtid = new Guid("64440490-4C8B-11D1-8B70-080036B11A03"),
                    pid = 3
                };
                hr = store.GetValue(ref key, out value);
                if (hr != 0)
                    return false;

                const ushort VT_UI8 = 21;
                const ushort VT_I8 = 20;
                if (value.vt == VT_UI8)
                {
                    duration100Ns = value.ulongValue;
                    return true;
                }
                if (value.vt == VT_I8 && value.longValue > 0)
                {
                    duration100Ns = (ulong)value.longValue;
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { PropVariantClear(ref value); } catch { }
                if (store != null)
                {
                    try { Marshal.ReleaseComObject(store); } catch { }
                }
            }
        }

        private void RefreshQueueList()
        {
            if (_queueList == null)
                return;

            int selected = _queueList.SelectedIndex;
            _queueList.Items.Clear();
            int i;
            for (i = 0; i < _state.Queue.Count; i++)
            {
                string path = _state.Queue[i];
                Grid row = new Grid();
                row.Height = 30;
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                TextBlock marker = new TextBlock();
                marker.Text = i == _currentIndex ? (_isPlaying ? "▶" : "•") : string.Empty;
                marker.Foreground = i == _currentIndex ? Accent : Muted;
                marker.FontSize = 10;
                marker.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(marker);

                TextBlock name = new TextBlock();
                name.Foreground = i == _currentIndex ? Text : UiTokens.Colors.SecondaryText;
                name.FontSize = 11;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.VerticalAlignment = VerticalAlignment.Center;
                name.Inlines.Add(new System.Windows.Documents.Run(Path.GetFileNameWithoutExtension(path)));
                string queueDuration = GetCachedDurationText(path);
                if (!string.IsNullOrEmpty(queueDuration))
                {
                    System.Windows.Documents.Run durationRun = new System.Windows.Documents.Run("  " + queueDuration);
                    durationRun.Foreground = Muted;
                    durationRun.FontSize = 10;
                    name.Inlines.Add(durationRun);
                }
                Grid.SetColumn(name, 1);
                row.Children.Add(name);

                TextBlock ext = new TextBlock();
                ext.Text = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
                ext.Foreground = Muted;
                ext.FontSize = 9;
                ext.Margin = new Thickness(8, 0, 4, 0);
                ext.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(ext, 2);
                row.Children.Add(ext);
                _queueList.Items.Add(row);
            }

            if (selected >= 0 && selected < _queueList.Items.Count)
                _queueList.SelectedIndex = selected;
            else if (_currentIndex >= 0 && _currentIndex < _queueList.Items.Count)
                _queueList.SelectedIndex = _currentIndex;

            _queueCountText.Text = _state.Queue.Count.ToString(CultureInfo.InvariantCulture);
            if (_queueDropHint != null)
                _queueDropHint.Visibility = _state.Queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Dispatcher.BeginInvoke(new Action(delegate { UpdateQueueScrollBar(); }), DispatcherPriority.Background);
        }

        private void RemoveSelectedQueueItem()
        {
            int index = _queueList.SelectedIndex;
            if (index < 0 || index >= _state.Queue.Count)
                return;

            bool removingCurrent = index == _currentIndex;
            _state.Queue.RemoveAt(index);
            if (_state.Queue.Count == 0)
            {
                StopPlayback();
                _player.Close();
                _currentIndex = -1;
                UpdateTrackLabels(null);
            }
            else if (removingCurrent)
            {
                if (index >= _state.Queue.Count) index = _state.Queue.Count - 1;
                _currentIndex = index;
                PlayIndex(_currentIndex);
            }
            else if (index < _currentIndex)
            {
                _currentIndex--;
            }
            RefreshQueueList();
            _state.Save();
        }

        private void ClearQueue()
        {
            _state.Queue.Clear();
            _currentIndex = -1;
            try { _player.Close(); } catch { }
            _isPlaying = false;
            _timer.Stop();
            _seek.Value = 0;
            _miniSeek.Value = 0;
            UpdateTrackLabels(null);
            UpdatePlaybackVisuals();
            RefreshQueueList();
            _state.Save();
        }

        private void ToggleTopmost()
        {
            _state.AlwaysOnTop = !_state.AlwaysOnTop;
            if (!_state.MiniMode)
                Topmost = _state.AlwaysOnTop;
            UpdateTopmostVisual();
            _state.Save();
        }

        private void UpdateTopmostVisual()
        {
            if (_topmostButton == null)
                return;
            Brush brush = _state.AlwaysOnTop ? Accent : Text;
            _topmostButton.Foreground = brush;
            UiPrimitives.SetPathButtonBrush(_topmostButton, brush);
        }

        private void SetMiniMode(bool mini)
        {
            SetMiniMode(mini, true);
        }

        private void SetMiniMode(bool mini, bool animate)
        {
            if (_miniApplied == mini)
                return;

            if (_menuPopup != null)
                _menuPopup.IsOpen = false;
            _morph.Complete();

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            Rect current;
            if (hwnd == IntPtr.Zero || !NativeWindowGeometry.TryGetBounds(hwnd, out current))
            {
                ApplyModeWithoutWindow(mini);
                return;
            }

            double scale = NativeWindowGeometry.DeviceScale(this);
            if (mini)
            {
                _normalWidth = current.Width / scale;
                _normalHeight = current.Height / scale;
            }
            else
            {
                _state.MiniWidth = current.Width / scale;
            }
            _miniApplied = mini;
            _state.MiniMode = mini;

            Size size = mini
                ? new Size(ClampMiniWidth(_state.MiniWidth), UiTokens.Layout.MiniWindowHeight)
                : new Size(Math.Max(UiTokens.Layout.WindowMinWidth, _normalWidth), Math.Max(CompactHeight(), _normalHeight));
            Rect target = PlanBounds(hwnd, current, new Size(size.Width * scale, size.Height * scale), scale);

            _morph.Run(target, animate ? UiTokens.Motion.ModeMorphMs : 0, MorphStyle.Fade,
                delegate
                {
                    ApplyResponsiveLayout(target.Width / scale, target.Height / scale);
                    // Constraints of the mode being left would block the single resize.
                    MinWidth = 0;
                    MinHeight = 0;
                    MaxWidth = double.PositiveInfinity;
                    MaxHeight = double.PositiveInfinity;
                    Topmost = mini || _state.AlwaysOnTop;
                },
                delegate { ApplyModeConstraints(mini); });
            _state.Save();
        }

        // Only reachable before the native window exists; there is nothing to animate yet.
        private void ApplyModeWithoutWindow(bool mini)
        {
            if (mini)
            {
                _normalWidth = ActualWidth > 200 ? ActualWidth : Width;
                _normalHeight = ActualHeight > 200 ? ActualHeight : Height;
            }
            _miniApplied = mini;
            _state.MiniMode = mini;
            ApplyModeConstraints(mini);
            Width = mini ? ClampMiniWidth(_state.MiniWidth) : Math.Max(UiTokens.Layout.WindowMinWidth, _normalWidth);
            Height = mini ? UiTokens.Layout.MiniWindowHeight : Math.Max(CompactHeight(), _normalHeight);
            ApplyResponsiveLayout(Width, Height);
            Topmost = mini || _state.AlwaysOnTop;
            _state.Save();
        }

        private void ApplyModeConstraints(bool mini)
        {
            if (mini)
            {
                // NoResize keeps Windows from snapping or maximizing the mini player while it is
                // dragged around; it resizes itself (TryBeginMiniResize).
                ResizeMode = ResizeMode.NoResize;
                MinWidth = MiniMinWidth();
            }
            else
            {
                ResizeMode = ResizeMode.CanResize;
                MinWidth = UiTokens.Layout.WindowMinWidth;
            }
            // Heights are governed by the resting layouts (SnapSizingRect), not by constraints.
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
            MinHeight = UiTokens.Layout.MiniWindowHeight;
        }

        /// <summary>Narrowest mini player: the five transport buttons and nothing else.</summary>
        private double MiniMinWidth()
        {
            if (double.IsNaN(_miniMinWidth))
            {
                _miniControls.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double controls = _miniControls.DesiredSize.Width;
                if (controls <= 0)
                    return UiTokens.Layout.MiniWindowWidth;
                _miniMinWidth = Math.Ceiling(controls + UiTokens.Layout.MiniTightLeftInset + _miniContent.Margin.Right);
            }
            return _miniMinWidth;
        }

        private double ClampMiniWidth(double width)
        {
            return Math.Max(MiniMinWidth(), width);
        }

        /// <summary>
        /// Target bounds (device pixels) for resizing the window to <paramref name="size"/>.
        /// Undoing the previous resize returns exactly to where it started; otherwise the
        /// window is placed to stay fully on screen (see NativeWindowGeometry.Place).
        /// </summary>
        private Rect PlanBounds(IntPtr hwnd, Rect current, Size size, double scale)
        {
            Rect target;
            if (!_placedTo.IsEmpty && RoughlyEqual(current, _placedTo) &&
                Math.Abs(_placedFrom.Width - size.Width) <= 2 && Math.Abs(_placedFrom.Height - size.Height) <= 2)
            {
                target = _placedFrom;
            }
            else
            {
                target = NativeWindowGeometry.Place(current, size, NativeWindowGeometry.GetWorkArea(hwnd),
                    UiTokens.Layout.ScreenEdgeDockTolerance * scale);
            }
            _placedFrom = current;
            _placedTo = target;
            return target;
        }

        private static bool RoughlyEqual(Rect a, Rect b)
        {
            return Math.Abs(a.Left - b.Left) <= 2 && Math.Abs(a.Top - b.Top) <= 2 &&
                   Math.Abs(a.Right - b.Right) <= 2 && Math.Abs(a.Bottom - b.Bottom) <= 2;
        }

        private void SetQueueVisible(bool visible)
        {
            SetQueueVisible(visible, true);
        }

        private void SetQueueVisible(bool visible, bool animate)
        {
            _state.QueueVisible = visible;
            if (_queueButton != null)
            {
                Brush queueBrush = visible ? Accent : Text;
                _queueButton.Foreground = queueBrush;
                UiPrimitives.SetPathButtonBrush(_queueButton, queueBrush);
            }

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            Rect current = Rect.Empty;
            bool hasWindow = hwnd != IntPtr.Zero && NativeWindowGeometry.TryGetBounds(hwnd, out current);
            if (!hasWindow || _miniApplied)
            {
                // The full player is not on screen: update its layout and remembered height only.
                _queuePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (!hasWindow)
                    Height = QueueWindowHeight(visible, Height);
                else
                    _normalHeight = QueueWindowHeight(visible, _normalHeight);
                _state.Save();
                return;
            }

            _morph.Complete();
            NativeWindowGeometry.TryGetBounds(hwnd, out current);
            double scale = NativeWindowGeometry.DeviceScale(this);
            double height = QueueWindowHeight(visible, current.Height / scale) * scale;
            Rect target = Math.Abs(height - current.Height) < 1
                ? current
                : PlanBounds(hwnd, current, new Size(current.Width, height), scale);

            // When the window changes by exactly the panel, the player above it is untouched and
            // the queue opens like a drawer. If the screen forced a different size, fade instead.
            double change = Math.Abs(target.Height - current.Height);
            bool drawer = Math.Abs(change - UiTokens.Layout.QueuePanelHeight * scale) <= 1.5 &&
                          Math.Abs(target.Width - current.Width) < 1;
            _morph.Run(target, animate ? UiTokens.Motion.QueueRevealMs : 0, drawer ? MorphStyle.Slide : MorphStyle.Fade,
                delegate { ApplyResponsiveLayout(target.Width / scale, target.Height / scale); },
                null);
            _state.Save();
        }

        /// <summary>
        /// Window height (DIPs) for the queue state. The queue adds its own height below the
        /// player instead of squeezing it, and closing restores the height from before.
        /// </summary>
        private double QueueWindowHeight(bool visible, double height)
        {
            if (visible)
            {
                if (!double.IsNaN(_heightBeforeQueueOpen))
                    return height;
                _heightBeforeQueueOpen = height;
                return _heightBeforeQueueOpen + UiTokens.Layout.QueuePanelHeight;
            }
            if (double.IsNaN(_heightBeforeQueueOpen))
                return height;
            double restore = _heightBeforeQueueOpen;
            _heightBeforeQueueOpen = double.NaN;
            return restore;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateVolumeVisuals();
            UpdateTopmostVisual();
            UpdateLoopVisuals();
            UpdatePlaybackVisuals();
            UpdateTimeLabels();
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closing)
                return;
            _closing = true;
            try
            {
                _morph.Complete();
                if (_miniApplied && ActualWidth > 0)
                    _state.MiniWidth = ActualWidth;
                _state.Volume = _player.Volume;
                _state.Save();
                _timer.Stop();
                _player.Close();
            }
            catch { }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (ctrl && e.Key == Key.O)
            {
                AddFilesFromDialog();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.M)
            {
                SetMiniMode(!_state.MiniMode);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.T)
            {
                ToggleTopmost();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.L)
            {
                SetQueueVisible(!_state.QueueVisible);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.R)
            {
                CycleLoopMode();
                e.Handled = true;
            }
            else if (e.Key == Key.Space)
            {
                TogglePlayPause();
                e.Handled = true;
            }
            else if (e.Key == Key.Left)
            {
                Nudge(-5);
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                Nudge(5);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && _queuePanel.IsVisible)
            {
                RemoveSelectedQueueItem();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _state.MiniMode)
            {
                SetMiniMode(false);
                e.Handled = true;
            }
        }

        private void Nudge(double seconds)
        {
            try
            {
                if (!_player.NaturalDuration.HasTimeSpan)
                    return;
                TimeSpan duration = _player.NaturalDuration.TimeSpan;
                double next = _player.Position.TotalSeconds + seconds;
                if (next < 0) next = 0;
                if (next > duration.TotalSeconds) next = duration.TotalSeconds;
                _player.Position = TimeSpan.FromSeconds(next);
                UpdateTimeLabels();
            }
            catch { }
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            bool valid = HasAudioFiles(e.Data);
            e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;

            if (!valid)
            {
                HideDropOverlays();
                e.Handled = true;
                return;
            }

            if (_state.MiniMode)
            {
                HideDropOverlays();
                e.Handled = true;
                return;
            }

            Point position = e.GetPosition(this);
            bool overQueue = _state.QueueVisible && IsPointInside(_queueDropArea, position);
            bool overHero = IsPointInside(_heroArea, position);

            SetDropOverlayVisible(_queueDropOverlay, overQueue);
            SetDropOverlayVisible(_heroDropOverlay, !overQueue && overHero);
            e.Handled = true;
        }

        private void OnRootDragLeave(object sender, DragEventArgs e)
        {
            // DragLeave can be raised while moving between nested WPF children.
            // Only hide when the pointer has genuinely left the window bounds;
            // otherwise the next DragOver keeps the full-zone overlay stable.
            Point p = e.GetPosition(this);
            if (p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight)
                HideDropOverlays();
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            string[] files = GetAudioFiles(e.Data);
            Point position = e.GetPosition(this);
            bool overQueue = !_state.MiniMode && _state.QueueVisible && IsPointInside(_queueDropArea, position);
            bool overHero = !_state.MiniMode && IsPointInside(_heroArea, position);
            HideDropOverlays();

            if (files.Length == 0)
                return;

            if (overQueue)
                ApplyDroppedFiles(files, LaunchMode.Enqueue);
            else if (overHero)
                ApplyDroppedFiles(files, _currentIndex < 0 ? LaunchMode.Play : LaunchMode.Enqueue);
            else
                ApplyDroppedFiles(files, LaunchMode.Enqueue);

            e.Handled = true;
        }

        private void SetDropOverlayVisible(Border overlay, bool visible)
        {
            if (overlay == null)
                return;
            Visibility next = visible ? Visibility.Visible : Visibility.Collapsed;
            if (overlay.Visibility != next)
                overlay.Visibility = next;
        }

        private void HideDropOverlays()
        {
            SetDropOverlayVisible(_heroDropOverlay, false);
            SetDropOverlayVisible(_queueDropOverlay, false);
            ResetDropHint(_heroDropHint);
            ResetDropHint(_queueDropHint);
        }

        private bool IsPointInside(FrameworkElement element, Point windowPoint)
        {
            if (element == null || !element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                return false;
            try
            {
                Point topLeft = element.TransformToAncestor(this).Transform(new Point(0, 0));
                Rect bounds = new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
                return bounds.Contains(windowPoint);
            }
            catch
            {
                return false;
            }
        }

        private void ApplyDroppedFiles(string[] files, LaunchMode mode)
        {
            if (files == null || files.Length == 0) return;
            LaunchRequest request = new LaunchRequest();
            request.Mode = mode;
            int i;
            for (i = 0; i < files.Length; i++) request.Files.Add(files[i]);
            ApplyLaunchRequest(request);
        }

        private static bool HasAudioFiles(IDataObject data)
        {
            return GetAudioFiles(data).Length > 0;
        }

        private static string[] GetAudioFiles(IDataObject data)
        {
            List<string> result = new List<string>();
            try
            {
                if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null)
                    {
                        int i;
                        for (i = 0; i < files.Length; i++)
                        {
                            if (File.Exists(files[i]) && AudioFormats.IsSupported(files[i]))
                                result.Add(Path.GetFullPath(files[i]));
                        }
                    }
                }
            }
            catch { }
            return result.ToArray();
        }

        private void OnFullSurfaceDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1 || _fullRoot == null || _seek == null ||
                _fullRoot.Visibility != Visibility.Visible)
                return;

            // Listen at Window preview level, not on the inner Grid. A WPF Panel
            // with no Background is not a hit target in its empty areas, which meant
            // the old handler never saw the exact clicks we wanted to use for dragging.
            // Geometry + control filtering below keeps interactive content untouched.
            Point p = e.GetPosition(_fullRoot);
            Point seekTop = _seek.TranslatePoint(new Point(0, 0), _fullRoot);
            if (p.Y >= seekTop.Y)
                return;

            DependencyObject original = e.OriginalSource as DependencyObject;
            if (FindParent<Button>(original) != null || FindParent<MinimalSlider>(original) != null)
                return;

            // Artwork is intentionally never a drag handle. In the empty state,
            // preserve the title/add affordance as a click target.
            Point windowPoint = e.GetPosition(this);
            if (IsPointInside(_artworkSurface, windowPoint) ||
                (_currentIndex < 0 && (IsPointInside(_titleText, windowPoint) || IsPointInside(_heroDropHint, windowPoint))))
                return;

            if (BeginNativeWindowDrag())
                e.Handled = true;
        }

        private void OnMiniDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;
            if (e.ClickCount == 1 && TryBeginMiniResize(e))
            {
                e.Handled = true;
                return;
            }
            DependencyObject original = e.OriginalSource as DependencyObject;
            if (FindParent<Button>(original) != null || FindParent<MinimalSlider>(original) != null)
                return;
            if (e.ClickCount == 2)
            {
                SetMiniMode(false);
                e.Handled = true;
                return;
            }

            if (BeginNativeWindowDrag())
                e.Handled = true;
        }

        private bool BeginNativeWindowDrag()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero)
                    return false;
                ReleaseCapture();
                SendMessage(hwnd, 0x00A1, new IntPtr(0x0002), IntPtr.Zero); // WM_NCLBUTTONDOWN / HTCAPTION
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject current = child;
            while (current != null)
            {
                T match = current as T;
                if (match != null)
                    return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            TryApplyDwm(hwnd);
            // Registered after WindowChrome's own hook, so it is consulted first.
            HwndSource source = HwndSource.FromHwnd(hwnd);
            if (source != null)
                source.AddHook(WindowMessageHook);

            // Keyboard media keys, headset buttons and the volume flyout's media panel.
            _media = MediaSession.TryCreate(hwnd, Dispatcher);
            if (_media != null)
            {
                _media.PlayPressed += delegate { if (!_isPlaying) TogglePlayPause(); };
                _media.PausePressed += delegate { if (_isPlaying) TogglePlayPause(); };
                _media.NextPressed += delegate { Next(false); };
                _media.PreviousPressed += delegate { Previous(); };
                _media.StopPressed += delegate { if (_isPlaying) TogglePlayPause(); };
                if (_currentIndex >= 0 && _currentIndex < _state.Queue.Count)
                    _media.SetTrack(_state.Queue[_currentIndex], Path.GetFileNameWithoutExtension(_state.Queue[_currentIndex]));
                UpdatePlaybackVisuals();
            }
        }

        private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WmNcHitTest = 0x0084;
            const int WmEnterSizeMove = 0x0231;
            const int WmExitSizeMove = 0x0232;
            const int HtClient = 1;
            switch (msg)
            {
                case WmNcHitTest:
                    // The mini player resizes itself (BeginMiniResize); keep WindowChrome's
                    // native edges out of its way.
                    if (_miniApplied)
                    {
                        handled = true;
                        return new IntPtr(HtClient);
                    }
                    break;
                case WmEnterSizeMove:
                    _sizeMoveStart = new Size(ActualWidth, ActualHeight);
                    _userResizing = true;
                    break;
                case WmExitSizeMove:
                    _userResizing = false;
                    SyncModeWithSize();
                    break;
            }
            return IntPtr.Zero;
        }

        // ----- Mini player resizing -------------------------------------------------------
        // The mini player keeps ResizeMode.NoResize so Windows never snaps or maximizes it, and
        // Windows will not run its sizing loop for such a window. It therefore resizes itself:
        // every edge and corner, with the height free so it can be pulled open into the
        // compact or normal layout.

        private const int EdgeLeft = 1, EdgeRight = 2, EdgeTop = 4, EdgeBottom = 8;

        private int MiniEdgeAt(Point point)
        {
            double grip = UiTokens.Layout.MiniResizeBorder;
            int edge = 0;
            if (point.X < grip) edge |= EdgeLeft;
            else if (point.X >= ActualWidth - grip) edge |= EdgeRight;
            if (point.Y < grip) edge |= EdgeTop;
            else if (point.Y >= ActualHeight - grip) edge |= EdgeBottom;
            return edge;
        }

        private static Cursor CursorForEdge(int edge)
        {
            switch (edge)
            {
                case EdgeLeft:
                case EdgeRight:
                    return Cursors.SizeWE;
                case EdgeTop:
                case EdgeBottom:
                    return Cursors.SizeNS;
                case EdgeLeft | EdgeTop:
                case EdgeRight | EdgeBottom:
                    return Cursors.SizeNWSE;
                case EdgeRight | EdgeTop:
                case EdgeLeft | EdgeBottom:
                    return Cursors.SizeNESW;
                default:
                    return null;
            }
        }

        private bool TryBeginMiniResize(MouseButtonEventArgs e)
        {
            if (!_miniApplied || _morph.IsRunning)
                return false;
            int edge = MiniEdgeAt(e.GetPosition(this));
            NativePoint cursor;
            Rect bounds;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (edge == 0 || hwnd == IntPtr.Zero || !GetCursorPos(out cursor) || !NativeWindowGeometry.TryGetBounds(hwnd, out bounds))
                return false;
            _miniResizeEdge = edge;
            _miniResizeOrigin = new Point(cursor.X, cursor.Y);
            _miniResizeStart = bounds;
            _sizeMoveStart = new Size(ActualWidth, ActualHeight);
            CaptureMouse();
            return true;
        }

        private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_miniResizeEdge != 0)
            {
                ResizeMiniToCursor();
                e.Handled = true;
                return;
            }
            Cursor cursor = _miniApplied && !_morph.IsRunning ? CursorForEdge(MiniEdgeAt(e.GetPosition(this))) : null;
            if (Cursor != cursor)
                Cursor = cursor;
        }

        private void ResizeMiniToCursor()
        {
            NativePoint cursor;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetCursorPos(out cursor))
                return;
            double scale = NativeWindowGeometry.DeviceScale(this);
            double dx = cursor.X - _miniResizeOrigin.X;
            double dy = cursor.Y - _miniResizeOrigin.Y;
            double left = _miniResizeStart.Left, top = _miniResizeStart.Top;
            double right = _miniResizeStart.Right, bottom = _miniResizeStart.Bottom;
            if ((_miniResizeEdge & EdgeLeft) != 0) left += dx;
            if ((_miniResizeEdge & EdgeRight) != 0) right += dx;
            if ((_miniResizeEdge & EdgeTop) != 0) top += dy;
            if ((_miniResizeEdge & EdgeBottom) != 0) bottom += dy;

            double minHeight = Math.Ceiling(UiTokens.Layout.MiniWindowHeight * scale);
            if (bottom - top < minHeight)
            {
                if ((_miniResizeEdge & EdgeTop) != 0) top = bottom - minHeight;
                else bottom = top + minHeight;
            }
            // Pulled open past the mini layout, the window needs the full layout's width.
            double minWidth = Math.Ceiling((IsMiniHeight((bottom - top) / scale) ? MiniMinWidth() : UiTokens.Layout.WindowMinWidth) * scale);
            if (right - left < minWidth)
            {
                if ((_miniResizeEdge & EdgeLeft) != 0) left = right - minWidth;
                else right = left + minWidth;
            }
            NativeWindowGeometry.SetBounds(hwnd, new Rect(left, top, right - left, bottom - top));
        }

        private void OnWindowPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_miniResizeEdge == 0)
                return;
            ReleaseMouseCapture();
            e.Handled = true;
        }

        private void OnWindowLostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_miniResizeEdge == 0)
                return;
            _miniResizeEdge = 0;
            SyncModeWithSize();
        }

        /// <summary>
        /// After a resize, the window's mode follows the layout it was left in: shrunk into the
        /// mini layout it becomes the mini player (and Expand returns to the size from before);
        /// pulled open from the mini player it becomes the full player again.
        /// </summary>
        private void SyncModeWithSize()
        {
            bool mini = IsMiniHeight(ActualHeight);
            if (mini == _miniApplied)
                return;
            _miniApplied = mini;
            _state.MiniMode = mini;
            if (mini)
            {
                _normalWidth = _sizeMoveStart.Width;
                _normalHeight = _sizeMoveStart.Height;
            }
            else
            {
                _state.MiniWidth = _sizeMoveStart.Width;
                Cursor = null;
            }
            _placedTo = Rect.Empty;
            ApplyModeConstraints(mini);
            Topmost = mini || _state.AlwaysOnTop;
            _state.Save();
        }

        // ----- Responsive layout ----------------------------------------------------------
        // The height is free; the layout adapts as it shrinks: the artwork scales down and then
        // goes, the title recentres, then moves into the title bar (compact), and below the
        // compact layout's own height the window shows the mini layout.

        /// <summary>Natural height of the compact layout: title bar, progress and transport.</summary>
        private double CompactHeight()
        {
            if (double.IsNaN(_compactHeight))
            {
                Size unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
                _seekRow.Measure(unbounded);
                _controlsRow.Measure(unbounded);
                double rows = _seekRow.DesiredSize.Height + _controlsRow.DesiredSize.Height;
                if (rows <= 0)
                    return UiTokens.Layout.TopBarHeight + 110;
                _compactHeight = Math.Ceiling(UiTokens.Layout.TopBarHeight + rows);
            }
            return _compactHeight;
        }

        /// <summary>Smallest content height that still shows the title and subtitle centred.</summary>
        private double HeroMinHeight()
        {
            if (double.IsNaN(_heroMinHeight))
            {
                Size unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
                _titleText.Measure(unbounded);
                _heroDropHint.Measure(unbounded);
                double rows = _titleText.DesiredSize.Height + _heroDropHint.DesiredSize.Height;
                if (rows <= 0)
                    return CompactHeight() + 90;
                Thickness margin = _heroArea.Margin;
                _heroMinHeight = Math.Ceiling(CompactHeight() + rows + margin.Top + margin.Bottom);
            }
            return _heroMinHeight;
        }

        private bool QueueFits(double height)
        {
            return _state.QueueVisible && height - UiTokens.Layout.QueuePanelHeight >= CompactHeight();
        }

        private bool IsMiniHeight(double height)
        {
            double content = QueueFits(height) ? height - UiTokens.Layout.QueuePanelHeight : height;
            return content < CompactHeight();
        }

        private void ApplyResponsiveLayout(double width, double height)
        {
            ApplyResponsiveLayout(width, height, false);
        }

        /// <summary>
        /// Lays the window out for its size. Every change is a function of the height, so a
        /// resize drag plays it forwards and backwards: the artwork shrinks to nothing and fades,
        /// the centred title fades out and only then does the title-bar title fade in (never two
        /// titles at once), and the transport never moves. Only the switch to or from the mini
        /// layout is a swap; while the user resizes it fades the incoming layout in.
        /// </summary>
        private void ApplyResponsiveLayout(double width, double height, bool userResizing)
        {
            if (_fullRoot == null || _miniRoot == null || width <= 0 || height <= 0)
                return;
            bool queue = QueueFits(height);
            double content = queue ? height - UiTokens.Layout.QueuePanelHeight : height;
            bool mini = content < CompactHeight();
            double heroMin = HeroMinHeight();
            // 0 at the hero's minimum height, 1 a fade band above it.
            double toHero = Clamp01((content - heroMin) / UiTokens.Layout.LayoutFadeBand);
            double heroOpacity = Clamp01(toHero * 2 - 1);
            double compactOpacity = content < heroMin ? 1 : Clamp01(1 - toHero * 2);
            double artworkOpacity = Clamp01((content - heroMin - UiTokens.Layout.ArtworkFadeStart) / UiTokens.Layout.LayoutFadeBand);

            bool wasMini = _miniRoot.Visibility == Visibility.Visible;
            SetVisible(_miniRoot, mini);
            SetVisible(_fullRoot, !mini);
            if (mini != wasMini)
                RevealLayout(mini ? (UIElement)_miniRoot : _fullRoot, userResizing);
            SetVisible(_queuePanel, queue);
            SetVisible(_heroArea, !mini && content >= heroMin);
            SetOpacity(_heroArea, heroOpacity);
            SetOpacity(_artworkSurface, artworkOpacity);
            SetVisible(_compactTitle, compactOpacity > 0);
            SetOpacity(_compactTitle, compactOpacity);
            SetOpacity(_logo, heroOpacity);
            SetOpacity(_appName, heroOpacity);
            SetVisible(_volume, width >= UiTokens.Layout.VolumeSliderMinWindowWidth);

            if (mini)
            {
                // Narrowed down to its buttons, the mini player drops the title and the inset kept
                // for it, so the buttons sit optically centred between the two edges.
                bool tight = width - MiniMinWidth() < UiTokens.Layout.MiniTitleMinSpace;
                double left = tight ? UiTokens.Layout.MiniTightLeftInset : UiTokens.Layout.MiniLeftInset;
                Thickness margin = _miniContent.Margin;
                if (margin.Left != left)
                    _miniContent.Margin = new Thickness(left, margin.Top, margin.Right, margin.Bottom);
                _miniTitleText.Opacity = tight ? 0 : 1;
                // Keep the elapsed time mirroring the duration's distance from the right edge.
                double timeInset = tight ? margin.Right + 1 - left : 0;
                if (_miniProgressRow.Margin.Left != timeInset)
                    _miniProgressRow.Margin = new Thickness(timeInset, 0, 1, 0);
            }
        }

        // A layout swap during a resize drag fades in from the background instead of popping.
        // Outside a drag (mode button, startup) the morph or the plain swap is already right,
        // and the morph photographs the layout, so it must be fully opaque at once.
        private static void RevealLayout(UIElement layout, bool animate)
        {
            if (!animate || !WindowMorph.MotionEnabled)
            {
                layout.BeginAnimation(UIElement.OpacityProperty, null);
                return;
            }
            DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(UiTokens.Motion.LayoutSwapFadeMs));
            fade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            fade.FillBehavior = FillBehavior.Stop;
            layout.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private static void SetOpacity(UIElement element, double opacity)
        {
            if (element.Opacity != opacity)
                element.Opacity = opacity;
        }

        private static double Clamp01(double value)
        {
            return value < 0 ? 0 : (value > 1 ? 1 : value);
        }

        private static void SetVisible(UIElement element, bool visible)
        {
            Visibility value = visible ? Visibility.Visible : Visibility.Collapsed;
            if (element.Visibility != value)
                element.Visibility = value;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out NativePoint point);

        private static void TryApplyDwm(IntPtr hwnd)
        {
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
                int rounded = 2;
                DwmSetWindowAttribute(hwnd, 33, ref rounded, sizeof(int));
            }
            catch { }
        }

        [Flags]
        private enum ShellImageFlags : uint
        {
            BiggerSizeOk = 0x00000001,
            ThumbnailOnly = 0x00000008,
            ScaleUp = 0x00000100
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public long longValue;
            [FieldOffset(8)] public ulong ulongValue;
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint cProps);
            [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
            [PreserveSig] int Commit();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHGetPropertyStoreFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            uint flags,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);

        [ComImport]
        [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(NativeSize size, ShellImageFlags flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private void AttachQueueScrollViewer()
        {
            if (_queueList == null || _queueScrollBar == null)
                return;
            _queueList.ApplyTemplate();
            ScrollViewer viewer = FindVisualChild<ScrollViewer>(_queueList);
            if (viewer == null)
                return;
            if (_queueScrollViewer != null)
                _queueScrollViewer.ScrollChanged -= OnQueueScrollChanged;
            _queueScrollViewer = viewer;
            _queueScrollViewer.ScrollChanged += OnQueueScrollChanged;
            UpdateQueueScrollBar();
        }

        private void OnQueueScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdateQueueScrollBar();
        }

        private void UpdateQueueScrollBar()
        {
            if (_queueScrollViewer == null || _queueScrollBar == null)
                return;
            _queueScrollBar.UpdateMetrics(
                _queueScrollViewer.ExtentHeight,
                _queueScrollViewer.ViewportHeight,
                _queueScrollViewer.VerticalOffset);
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
                return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            int i;
            for (i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                T match = child as T;
                if (match != null)
                    return match;
                T nested = FindVisualChild<T>(child);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        private void ToggleMute()
        {
            if (_state.Volume > 0.001)
            {
                _volumeBeforeMute = _state.Volume;
                _state.LastAudibleVolume = _state.Volume;
                _state.Volume = 0;
            }
            else
            {
                _state.Volume = _volumeBeforeMute > 0.001 ? _volumeBeforeMute : _state.LastAudibleVolume;
                if (_state.Volume <= 0.001)
                    _state.Volume = 0.82;
            }

            _player.Volume = _state.Volume;
            if (_volume != null)
                _volume.SetValueSilently(_state.Volume);
            UpdateVolumeVisuals();
            _state.Save();
        }

        private void UpdateVolumeVisuals()
        {
            if (_volumeButton == null)
                return;

            bool muted = _state.Volume <= 0.001;
            System.Windows.Shapes.Path path = _volumeButton.Content as System.Windows.Shapes.Path;
            if (path != null)
            {
                path.Data = Geometry.Parse(muted
                    ? "M4,8 L8,8 L12,4 L12,16 L8,12 L4,12 Z M15,8 L20,13 M20,8 L15,13"
                    : "M4,8 L8,8 L12,4 L12,16 L8,12 L4,12 Z M15,8 C16.5,9 16.5,11 15,12 M17,6 C20,8 20,12 17,14");
                path.Stroke = Text;
            }
            _volumeButton.ToolTip = UiPrimitives.ToolTip(muted ? (_isFrench ? "Réactiver le son" : "Unmute") : (_isFrench ? "Muet" : "Mute"));
        }

        private static TextBlock MakeTimeText(bool mini, TextAlignment alignment)
        {
            TextBlock text = new TextBlock();
            text.Text = "00:00";
            text.Foreground = UiTokens.Colors.Time;
            text.FontSize = mini ? 11.5 : 13;
            text.FontFamily = new FontFamily("Bahnschrift");
            text.FontWeight = FontWeights.Medium;
            text.TextAlignment = alignment;
            text.VerticalAlignment = VerticalAlignment.Center;
            if (!mini) text.MinWidth = 48;
            return text;
        }

        private static Border CreateDropHint(TextBlock text, bool compact)
        {
            Border border = new Border();
            border.Background = Brushes.Transparent;
            border.BorderBrush = Brushes.Transparent;
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(compact ? 9 : 10);
            border.Padding = compact ? new Thickness(12, 7, 12, 7) : new Thickness(13, 6, 13, 6);
            border.Cursor = Cursors.Hand;
            border.AllowDrop = true;

            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.HorizontalAlignment = HorizontalAlignment.Center;
            row.VerticalAlignment = VerticalAlignment.Center;

            Border plusBadge = new Border();
            plusBadge.Width = compact ? 18 : 19;
            plusBadge.Height = compact ? 18 : 19;
            plusBadge.CornerRadius = new CornerRadius(6);
            plusBadge.Background = UiTokens.Colors.DropBadge;
            plusBadge.BorderBrush = UiTokens.Colors.DropBadgeBorder;
            plusBadge.BorderThickness = new Thickness(1);
            plusBadge.Tag = "drop-plus";
            System.Windows.Shapes.Path plus = UiPrimitives.PathIcon("M10,6 L10,14 M6,10 L14,10", 10, 10, Muted, 1.45);
            plusBadge.Child = plus;
            row.Children.Add(plusBadge);

            text.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(text);
            border.Child = row;
            return border;
        }

        private static Border CreateDropOverlay(string title, string subtitle)
        {
            Border overlay = new Border();
            overlay.Background = UiTokens.Colors.DropOverlay;
            overlay.BorderBrush = Accent;
            overlay.BorderThickness = new Thickness(1);
            overlay.CornerRadius = new CornerRadius(14);
            overlay.Margin = new Thickness(0);
            overlay.Padding = new Thickness(18);

            StackPanel content = new StackPanel();
            content.HorizontalAlignment = HorizontalAlignment.Center;
            content.VerticalAlignment = VerticalAlignment.Center;

            Border badge = new Border();
            badge.Width = 38;
            badge.Height = 38;
            badge.CornerRadius = new CornerRadius(12);
            badge.Background = UiTokens.Colors.DropOverlayBadge;
            badge.BorderBrush = UiTokens.Colors.DropOverlayBadgeBorder;
            badge.BorderThickness = new Thickness(1);
            badge.HorizontalAlignment = HorizontalAlignment.Center;
            System.Windows.Shapes.Path plus = UiPrimitives.PathIcon("M10,4 L10,16 M4,10 L16,10", 17, 17, Accent, 1.7);
            badge.Child = plus;
            content.Children.Add(badge);

            TextBlock titleText = new TextBlock();
            titleText.Text = title;
            titleText.Foreground = Text;
            titleText.FontFamily = new FontFamily("Segoe UI Variable Text");
            titleText.FontSize = 14;
            titleText.FontWeight = FontWeights.SemiBold;
            titleText.TextAlignment = TextAlignment.Center;
            titleText.Margin = new Thickness(0, 10, 0, 0);
            content.Children.Add(titleText);

            TextBlock subtitleText = new TextBlock();
            subtitleText.Text = subtitle;
            subtitleText.Foreground = Muted;
            subtitleText.FontFamily = new FontFamily("Segoe UI Variable Text");
            subtitleText.FontSize = 10.5;
            subtitleText.TextAlignment = TextAlignment.Center;
            subtitleText.Margin = new Thickness(0, 3, 0, 0);
            content.Children.Add(subtitleText);

            overlay.Child = content;
            return overlay;
        }

        private static void SetDropHintInteractive(Border border, bool interactive)
        {
            if (border == null) return;
            StackPanel row = border.Child as StackPanel;
            if (row == null) return;
            int i;
            for (i = 0; i < row.Children.Count; i++)
            {
                Border badge = row.Children[i] as Border;
                if (badge != null && Convert.ToString(badge.Tag) == "drop-plus")
                    badge.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;
                TextBlock text = row.Children[i] as TextBlock;
                if (text != null)
                    text.Margin = interactive ? new Thickness(8, 0, 0, 0) : new Thickness(0);
            }
            border.Padding = interactive ? new Thickness(13, 6, 13, 6) : new Thickness(0);
        }

        private static void SetDropHintHover(Border border, bool enabled)
        {
            if (border == null || !enabled) return;
            border.Background = UiTokens.Colors.Elevated;
            border.BorderBrush = UiTokens.Colors.DropBadgeBorder;
            SetDropHintContent(border, UiTokens.Colors.DropHoverText, UiTokens.Colors.DropHoverBadge, UiTokens.Colors.DropHoverBadgeBorder);
        }

        private static void ResetDropHint(Border border)
        {
            if (border == null) return;
            border.Background = Brushes.Transparent;
            border.BorderBrush = Brushes.Transparent;
            SetDropHintContent(border, Muted, UiTokens.Colors.DropBadge, UiTokens.Colors.DropBadgeBorder);
        }

        private static void SetDropHintContent(Border border, Brush textBrush, Brush badgeBrush, Brush badgeBorder)
        {
            StackPanel row = border.Child as StackPanel;
            if (row == null) return;
            int i;
            for (i = 0; i < row.Children.Count; i++)
            {
                TextBlock text = row.Children[i] as TextBlock;
                if (text != null) text.Foreground = textBrush;
                Border badge = row.Children[i] as Border;
                if (badge != null && Convert.ToString(badge.Tag) == "drop-plus")
                {
                    badge.Background = badgeBrush;
                    badge.BorderBrush = badgeBorder;
                    System.Windows.Shapes.Path plus = badge.Child as System.Windows.Shapes.Path;
                    if (plus != null) plus.Stroke = badgeBorder == Accent ? Accent : Muted;
                }
            }
        }

        private static string FormatTime(TimeSpan value)
        {
            if (value.TotalHours >= 1)
                return ((int)value.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" + value.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + value.Seconds.ToString("00", CultureInfo.InvariantCulture);
            return ((int)value.TotalMinutes).ToString("00", CultureInfo.InvariantCulture) + ":" + value.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

    }
}
