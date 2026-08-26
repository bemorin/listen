using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ListenApp
{
    internal enum LaunchMode
    {
        None,
        Play,
        Enqueue
    }

    internal sealed class LaunchRequest
    {
        public LaunchMode Mode = LaunchMode.None;
        public readonly List<string> Files = new List<string>();
    }

    internal static class Program
    {
        private const string MutexName = "Local\\Listen.MinimalAudioPlayer.v1";
        private const string PipeName = "Listen.MinimalAudioPlayer.v1";
        private static Mutex _mutex;
        private static PlayerWindow _window;
        private static readonly List<string> _pendingPlayFiles = new List<string>();
        private static DispatcherTimer _playBatchTimer;

        [STAThread]
        public static void Main(string[] args)
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;

            if (HasArg(args, "--unregister"))
            {
                ShellRegistration.Unregister(exePath);
                return;
            }

            if (HasArg(args, "--register-only"))
            {
                ShellRegistration.Register(exePath);
                return;
            }

            bool createdNew;
            _mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                ForwardToPrimary(args);
                return;
            }

            // IMPORTANT: normal playback must not modify Explorer/file-association
            // registry keys. Shell integration is only performed after an explicit
            // user action (installer, --register-only, or the menu command).

            AppState state = AppState.Load();
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;

            _window = new PlayerWindow(state);
            app.MainWindow = _window;

            StartPipeServer(app.Dispatcher);

            LaunchRequest request = ParseLaunchRequest(args);
            _window.Show();
            if (request.Mode != LaunchMode.None && request.Files.Count > 0)
            {
                QueueLaunchRequest(request, app.Dispatcher);
            }

            app.Run();

            try
            {
                _mutex.ReleaseMutex();
            }
            catch { }
            _mutex.Dispose();
        }

        private static bool HasArg(string[] args, string expected)
        {
            int i;
            for (i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        internal static LaunchRequest ParseLaunchRequest(string[] args)
        {
            LaunchRequest request = new LaunchRequest();
            bool enqueue = false;
            bool explicitPlay = false;
            int i;

            for (i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--enqueue", StringComparison.OrdinalIgnoreCase))
                {
                    enqueue = true;
                    continue;
                }
                if (string.Equals(arg, "--play", StringComparison.OrdinalIgnoreCase))
                {
                    explicitPlay = true;
                    continue;
                }
                if (arg.StartsWith("--", StringComparison.Ordinal))
                    continue;

                string file = NormalizeAudioPath(arg);
                if (file != null)
                    request.Files.Add(file);
            }

            if (request.Files.Count == 0)
            {
                request.Mode = LaunchMode.None;
            }
            else if (enqueue && !explicitPlay)
            {
                request.Mode = LaunchMode.Enqueue;
            }
            else
            {
                request.Mode = LaunchMode.Play;
            }
            return request;
        }

        private static string NormalizeAudioPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            try
            {
                string full = Path.GetFullPath(value.Trim('"'));
                if (!File.Exists(full))
                    return null;
                if (!AudioFormats.IsSupported(full))
                    return null;
                return full;
            }
            catch
            {
                return null;
            }
        }

        private static void StartPipeServer(Dispatcher dispatcher)
        {
            Thread thread = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        using (NamedPipeServerStream server = new NamedPipeServerStream(
                            PipeName,
                            PipeDirection.In,
                            1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.None))
                        {
                            server.WaitForConnection();
                            using (BinaryReader reader = new BinaryReader(server))
                            {
                                int count = reader.ReadInt32();
                                List<string> forwarded = new List<string>();
                                int i;
                                for (i = 0; i < count; i++)
                                    forwarded.Add(reader.ReadString());

                                dispatcher.BeginInvoke(new Action(delegate()
                                {
                                    LaunchRequest request = ParseLaunchRequest(forwarded.ToArray());
                                    if (_window == null)
                                        return;
                                    if (!_window.IsVisible)
                                        _window.Show();
                                    if (_window.WindowState == WindowState.Minimized)
                                        _window.WindowState = WindowState.Normal;
                                    _window.Activate();
                                    if (request.Mode != LaunchMode.None && request.Files.Count > 0)
                                        QueueLaunchRequest(request, dispatcher);
                                }));
                            }
                        }
                    }
                    catch
                    {
                        Thread.Sleep(200);
                    }
                }
            });
            thread.IsBackground = true;
            thread.Name = "Listen single-instance pipe";
            thread.Start();
        }

        private static void QueueLaunchRequest(LaunchRequest request, Dispatcher dispatcher)
        {
            if (request == null || request.Files.Count == 0 || _window == null)
                return;

            // Explorer legacy Player verbs can launch one process per selected file.
            // Batch Play requests arriving in the same short burst so a multi-selection
            // becomes one queue and the first selected track starts only once.
            if (request.Mode != LaunchMode.Play)
            {
                _window.ApplyLaunchRequest(request);
                return;
            }

            int i;
            for (i = 0; i < request.Files.Count; i++)
            {
                string file = request.Files[i];
                bool exists = false;
                int j;
                for (j = 0; j < _pendingPlayFiles.Count; j++)
                {
                    if (string.Equals(_pendingPlayFiles[j], file, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                    _pendingPlayFiles.Add(file);
            }

            if (_playBatchTimer == null)
            {
                _playBatchTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher);
                _playBatchTimer.Interval = TimeSpan.FromMilliseconds(320);
                _playBatchTimer.Tick += delegate
                {
                    _playBatchTimer.Stop();
                    if (_window == null || _pendingPlayFiles.Count == 0)
                        return;

                    LaunchRequest merged = new LaunchRequest();
                    merged.Mode = LaunchMode.Play;
                    int k;
                    for (k = 0; k < _pendingPlayFiles.Count; k++)
                        merged.Files.Add(_pendingPlayFiles[k]);
                    _pendingPlayFiles.Clear();
                    _window.ApplyLaunchRequest(merged);
                };
            }

            _playBatchTimer.Stop();
            _playBatchTimer.Start();
        }

        private static void ForwardToPrimary(string[] args)
        {
            int attempt;
            for (attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (NamedPipeClientStream client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                    {
                        client.Connect(700);
                        using (BinaryWriter writer = new BinaryWriter(client))
                        {
                            writer.Write(args.Length);
                            int i;
                            for (i = 0; i < args.Length; i++)
                                writer.Write(args[i] ?? string.Empty);
                            writer.Flush();
                        }
                    }
                    return;
                }
                catch
                {
                    Thread.Sleep(150);
                }
            }
        }
    }
}
