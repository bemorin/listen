using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ListenApp
{
    internal static class AudioFormats
    {
        private static readonly HashSet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac"
        };

        public static bool IsSupported(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            return Supported.Contains(Path.GetExtension(path));
        }

        public static string[] Extensions
        {
            get { return new string[] { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac" }; }
        }

        public static string OpenDialogFilter
        {
            get
            {
                return "Audio (*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac)|*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac|Tous les fichiers (*.*)|*.*";
            }
        }
    }

    internal sealed class AppState
    {
        public double Volume = 0.82;
        public double LastAudibleVolume = 0.82;
        public bool AlwaysOnTop = false;
        public bool MiniMode = false;
        public bool QueueVisible = false;
        // Mini player width in DIPs; the mini player is resizable horizontally.
        public double MiniWidth = UiTokens.Layout.MiniWindowWidth;
        // 0 = off, 1 = repeat queue, 2 = repeat current track.
        public int LoopMode = 0;
        public readonly List<string> Queue = new List<string>();

        private static string DataDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Listen");
            }
        }

        private static string SettingsPath
        {
            get { return Path.Combine(DataDirectory, "settings.ini"); }
        }

        private static string QueuePath
        {
            get { return Path.Combine(DataDirectory, "queue.txt"); }
        }

        public static AppState Load()
        {
            AppState state = new AppState();
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string[] lines = File.ReadAllLines(SettingsPath, Encoding.UTF8);
                    int i;
                    for (i = 0; i < lines.Length; i++)
                    {
                        int equals = lines[i].IndexOf('=');
                        if (equals <= 0)
                            continue;
                        string key = lines[i].Substring(0, equals).Trim();
                        string value = lines[i].Substring(equals + 1).Trim();
                        if (string.Equals(key, "volume", StringComparison.OrdinalIgnoreCase))
                        {
                            double parsed;
                            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                                state.Volume = Clamp(parsed, 0, 1);
                        }
                        else if (string.Equals(key, "lastVolume", StringComparison.OrdinalIgnoreCase))
                        {
                            double parsed;
                            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                                state.LastAudibleVolume = Clamp(parsed, 0.01, 1);
                        }
                        else if (string.Equals(key, "topmost", StringComparison.OrdinalIgnoreCase))
                        {
                            state.AlwaysOnTop = string.Equals(value, "1", StringComparison.Ordinal);
                        }
                        else if (string.Equals(key, "mini", StringComparison.OrdinalIgnoreCase))
                        {
                            state.MiniMode = string.Equals(value, "1", StringComparison.Ordinal);
                        }
                        else if (string.Equals(key, "queueVisible", StringComparison.OrdinalIgnoreCase))
                        {
                            state.QueueVisible = string.Equals(value, "1", StringComparison.Ordinal);
                        }
                        else if (string.Equals(key, "miniWidth", StringComparison.OrdinalIgnoreCase))
                        {
                            double parsed;
                            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                                state.MiniWidth = Clamp(parsed, 0, 10000);
                        }
                        else if (string.Equals(key, "loopMode", StringComparison.OrdinalIgnoreCase))
                        {
                            int parsed;
                            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                                state.LoopMode = parsed < 0 ? 0 : (parsed > 2 ? 2 : parsed);
                        }
                    }
                }
            }
            catch { }

            if (state.Volume > 0.001)
                state.LastAudibleVolume = state.Volume;

            try
            {
                if (File.Exists(QueuePath))
                {
                    string[] lines = File.ReadAllLines(QueuePath, Encoding.ASCII);
                    int i;
                    for (i = 0; i < lines.Length; i++)
                    {
                        if (string.IsNullOrWhiteSpace(lines[i]))
                            continue;
                        try
                        {
                            string path = Encoding.UTF8.GetString(Convert.FromBase64String(lines[i].Trim()));
                            if (File.Exists(path) && AudioFormats.IsSupported(path))
                                state.Queue.Add(path);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return state;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                string settings =
                    "volume=" + Volume.ToString("0.000", CultureInfo.InvariantCulture) + Environment.NewLine +
                    "lastVolume=" + LastAudibleVolume.ToString("0.000", CultureInfo.InvariantCulture) + Environment.NewLine +
                    "topmost=" + (AlwaysOnTop ? "1" : "0") + Environment.NewLine +
                    "mini=" + (MiniMode ? "1" : "0") + Environment.NewLine +
                    "queueVisible=" + (QueueVisible ? "1" : "0") + Environment.NewLine +
                    "miniWidth=" + MiniWidth.ToString("0", CultureInfo.InvariantCulture) + Environment.NewLine +
                    "loopMode=" + LoopMode.ToString(CultureInfo.InvariantCulture) + Environment.NewLine;
                AtomicWrite(SettingsPath, settings, Encoding.UTF8);

                StringBuilder queue = new StringBuilder();
                int i;
                for (i = 0; i < Queue.Count; i++)
                {
                    string path = Queue[i];
                    if (!File.Exists(path))
                        continue;
                    queue.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(path)));
                    queue.Append(Environment.NewLine);
                }
                AtomicWrite(QueuePath, queue.ToString(), Encoding.ASCII);
            }
            catch
            {
                // State persistence should never prevent closing the app.
            }
        }

        private static void AtomicWrite(string path, string content, Encoding encoding)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, content, encoding);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
