using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media;
using Windows.Storage;

namespace ListenApp
{
    /// <summary>
    /// Windows system media transport controls for the player window: keyboard media keys,
    /// headset buttons and the media panel of the volume flyout. Windows routes those keys to
    /// the most recent media session, so Listen answers them without taking them from other
    /// players and without a global keyboard hook.
    /// </summary>
    internal sealed class MediaSession
    {
        // ISystemMediaTransportControls, the default interface of SystemMediaTransportControls.
        private static readonly Guid ControlsInterfaceId = new Guid("99FA3FF4-1742-42A6-902E-087D41F965EC");

        private readonly SystemMediaTransportControls _controls;
        private readonly Dispatcher _dispatcher;
        private int _trackVersion;

        public event Action PlayPressed;
        public event Action PausePressed;
        public event Action NextPressed;
        public event Action PreviousPressed;
        public event Action StopPressed;

        private MediaSession(SystemMediaTransportControls controls, Dispatcher dispatcher)
        {
            _controls = controls;
            _dispatcher = dispatcher;
            _controls.IsEnabled = true;
            _controls.IsPlayEnabled = true;
            _controls.IsPauseEnabled = true;
            _controls.IsStopEnabled = true;
            _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            _controls.ButtonPressed += OnButtonPressed;
        }

        /// <summary>Returns null where the system media controls are unavailable.</summary>
        public static MediaSession TryCreate(IntPtr hwnd, Dispatcher dispatcher)
        {
            if (hwnd == IntPtr.Zero)
                return null;
            try
            {
                ISystemMediaTransportControlsInterop interop = (ISystemMediaTransportControlsInterop)
                    WindowsRuntimeMarshal.GetActivationFactory(typeof(SystemMediaTransportControls));
                Guid iid = ControlsInterfaceId;
                SystemMediaTransportControls controls = interop.GetForWindow(hwnd, ref iid) as SystemMediaTransportControls;
                return controls != null ? new MediaSession(controls, dispatcher) : null;
            }
            catch
            {
                return null;
            }
        }

        public void SetPlayback(bool playing, bool hasTrack, int queueCount)
        {
            try
            {
                _controls.PlaybackStatus = !hasTrack
                    ? MediaPlaybackStatus.Closed
                    : (playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused);
                // Previous and Next wrap around the queue, so they apply whenever it has tracks.
                _controls.IsNextEnabled = queueCount > 0;
                _controls.IsPreviousEnabled = queueCount > 0;
            }
            catch { }
        }

        /// <summary>Shows the track (title, artist, artwork read from the file) in Windows.</summary>
        public void SetTrack(string path, string fallbackTitle)
        {
            int version = ++_trackVersion;
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    _controls.DisplayUpdater.ClearAll();
                    _controls.DisplayUpdater.Update();
                    return;
                }
                // Completion callbacks rather than await: the awaiters ship against the merged
                // Windows metadata, while Windows itself only carries the per-namespace files.
                IAsyncOperation<StorageFile> open = StorageFile.GetFileFromPathAsync(path);
                open.Completed = delegate(IAsyncOperation<StorageFile> operation, AsyncStatus status)
                {
                    StorageFile file = status == AsyncStatus.Completed ? operation.GetResults() : null;
                    _dispatcher.BeginInvoke(new Action(delegate { CopyMetadata(version, file, fallbackTitle); }));
                };
            }
            catch
            {
                ShowTitleOnly(version, fallbackTitle);
            }
        }

        private void CopyMetadata(int version, StorageFile file, string fallbackTitle)
        {
            if (version != _trackVersion)
                return;
            if (file == null)
            {
                ShowTitleOnly(version, fallbackTitle);
                return;
            }
            try
            {
                IAsyncOperation<bool> copy = _controls.DisplayUpdater.CopyFromFileAsync(MediaPlaybackType.Music, file);
                copy.Completed = delegate(IAsyncOperation<bool> operation, AsyncStatus status)
                {
                    bool copied = status == AsyncStatus.Completed && operation.GetResults();
                    _dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (copied)
                            PublishTrack(version, fallbackTitle);
                        else
                            ShowTitleOnly(version, fallbackTitle);
                    }));
                };
            }
            catch
            {
                ShowTitleOnly(version, fallbackTitle);
            }
        }

        private void ShowTitleOnly(int version, string title)
        {
            if (version != _trackVersion)
                return;
            try
            {
                _controls.DisplayUpdater.ClearAll();
                _controls.DisplayUpdater.Type = MediaPlaybackType.Music;
            }
            catch { }
            PublishTrack(version, title);
        }

        private void PublishTrack(int version, string fallbackTitle)
        {
            if (version != _trackVersion)
                return;
            try
            {
                SystemMediaTransportControlsDisplayUpdater updater = _controls.DisplayUpdater;
                // Untagged files have no title in their metadata: use the one Listen shows.
                if (string.IsNullOrEmpty(updater.MusicProperties.Title))
                    updater.MusicProperties.Title = fallbackTitle ?? string.Empty;
                updater.Update();
            }
            catch { }
        }

        private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            Action handler = null;
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play: handler = PlayPressed; break;
                case SystemMediaTransportControlsButton.Pause: handler = PausePressed; break;
                case SystemMediaTransportControlsButton.Next: handler = NextPressed; break;
                case SystemMediaTransportControlsButton.Previous: handler = PreviousPressed; break;
                case SystemMediaTransportControlsButton.Stop: handler = StopPressed; break;
            }
            // Raised on a system thread; the player lives on the UI thread.
            if (handler != null)
                _dispatcher.BeginInvoke(handler);
        }

        [ComImport]
        [Guid("DDB0472D-C911-4A1F-86D9-DC3D71A95F5A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
        private interface ISystemMediaTransportControlsInterop
        {
            [return: MarshalAs(UnmanagedType.IInspectable)]
            object GetForWindow(IntPtr appWindow, [In] ref Guid riid);
        }
    }
}
