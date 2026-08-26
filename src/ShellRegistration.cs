using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace ListenApp
{
    internal static class ShellRegistration
    {
        private const string ApplicationName = "Listen";
        private const string ProgId = "Listen.Audio";
        private const string RegisteredApplicationValue = "Listen";
        private const string CapabilitiesPath = @"Software\Listen\Capabilities";

        public static void Register(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return;

            exePath = Path.GetFullPath(exePath);
            string exeName = Path.GetFileName(exePath);
            string quotedExe = Quote(exePath);
            string iconValue = exePath + ",0";
            bool french = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase);
            string playLabel = french ? "Lire avec Listen" : "Play with Listen";
            string queueLabel = french ? "Ajouter à la file d’attente Listen" : "Add to Listen play queue";

            using (RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
            {
                if (classes == null)
                    return;

                // Keep one visible source for Explorer verbs. Older Listen builds
                // registered the same actions in several shell locations, so a file
                // could show duplicate Play / Enqueue entries. Clean those legacy
                // locations before writing the canonical registration below.
                CleanupLegacyVisibleVerbs(classes, exeName);

                // A real ProgID is what Windows uses for Open With recommendations.
                using (RegistryKey prog = classes.CreateSubKey(ProgId))
                {
                    prog.SetValue(string.Empty, "Listen audio file");
                    prog.SetValue("FriendlyTypeName", "Listen audio file");
                }
                SetValue(classes, ProgId + @"\DefaultIcon", null, iconValue);
                SetValue(classes, ProgId + @"\Application", "ApplicationName", ApplicationName);
                SetValue(classes, ProgId + @"\Application", "ApplicationDescription", "Minimal audio player");
                SetValue(classes, ProgId + @"\Application", "ApplicationIcon", iconValue);
                SetValue(classes, ProgId + @"\shell", null, "open");
                // The default open verb must remain a normal shell verb. Marking it
                // ProgrammaticAccessOnly prevents ordinary file activation from using it
                // reliably and can make Windows reopen the app picker on every double-click.
                SetValue(classes, ProgId + @"\shell\open", "Icon", iconValue);
                SetValue(classes, ProgId + @"\shell\open", "MultiSelectModel", "Player");
                SetValue(classes, ProgId + @"\shell\open\command", null, quotedExe + " --play \"%1\"");

                // Register the executable itself under HKCR\Applications. SupportedTypes is
                // specifically used by Windows to populate Open with / Choose another app.
                string applicationsRoot = @"Applications\" + exeName;
                SetValue(classes, applicationsRoot, "FriendlyAppName", ApplicationName);
                SetValue(classes, applicationsRoot, "ApplicationDescription", "Minimal audio player");
                SetValue(classes, applicationsRoot + @"\DefaultIcon", null, iconValue);
                SetValue(classes, applicationsRoot + @"\shell", null, "open");
                SetValue(classes, applicationsRoot + @"\shell\open", "Icon", iconValue);
                SetValue(classes, applicationsRoot + @"\shell\open", "MultiSelectModel", "Player");
                SetValue(classes, applicationsRoot + @"\shell\open\command", null, quotedExe + " --play \"%1\"");

                string[] extensions = AudioFormats.Extensions;
                int i;
                for (i = 0; i < extensions.Length; i++)
                {
                    string ext = extensions[i];
                    SetValue(classes, applicationsRoot + @"\SupportedTypes", ext, string.Empty);
                    SetValue(classes, ext + @"\OpenWithProgids", ProgId, string.Empty);
                }

                // Classic Explorer verbs. Windows 11 may surface them in "Show more options".
                string audioAssociationBase = @"SystemFileAssociations\audio\shell";
                string audioPlayKey = audioAssociationBase + @"\Listen.Play";
                SetValue(classes, audioPlayKey, "MUIVerb", playLabel);
                SetValue(classes, audioPlayKey, "Icon", iconValue);
                SetValue(classes, audioPlayKey, "MultiSelectModel", "Player");
                SetValue(classes, audioPlayKey + @"\command", null, quotedExe + " --play \"%1\"");

                string audioQueueKey = audioAssociationBase + @"\Listen.Enqueue";
                SetValue(classes, audioQueueKey, "MUIVerb", queueLabel);
                SetValue(classes, audioQueueKey, "Icon", iconValue);
                SetValue(classes, audioQueueKey, "MultiSelectModel", "Player");
                SetValue(classes, audioQueueKey + @"\command", null, quotedExe + " --enqueue \"%1\"");
            }

            // App Paths gives the shell a stable executable lookup for this per-user app.
            using (RegistryKey appPath = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exeName))
            {
                if (appPath != null)
                {
                    appPath.SetValue(string.Empty, exePath);
                    appPath.SetValue("Path", Path.GetDirectoryName(exePath) ?? string.Empty);
                }
            }

            // Windows keeps a per-user Open With cache in Explorer\FileExts. We only add
            // our ProgID; we never touch the protected UserChoice value.
            string[] supported = AudioFormats.Extensions;
            int j;
            for (j = 0; j < supported.Length; j++)
            {
                try
                {
                    using (RegistryKey openWith = Registry.CurrentUser.CreateSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + supported[j] + @"\OpenWithProgids"))
                    {
                        if (openWith != null)
                            openWith.SetValue(ProgId, string.Empty, RegistryValueKind.String);
                    }
                }
                catch { }
            }

            using (RegistryKey capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
            {
                if (capabilities != null)
                {
                    capabilities.SetValue("ApplicationName", ApplicationName);
                    capabilities.SetValue("ApplicationDescription", "A tiny audio-only player for Windows");
                    capabilities.SetValue("ApplicationIcon", iconValue);
                    using (RegistryKey fileAssociations = capabilities.CreateSubKey("FileAssociations"))
                    {
                        if (fileAssociations != null)
                        {
                            string[] extensions = AudioFormats.Extensions;
                            int i;
                            for (i = 0; i < extensions.Length; i++)
                                fileAssociations.SetValue(extensions[i], ProgId);
                        }
                    }
                }
            }

            using (RegistryKey registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            {
                if (registered != null)
                    registered.SetValue(RegisteredApplicationValue, CapabilitiesPath);
            }

            NotifyAssociationChanged();
        }

        public static bool IsRegistered(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath))
                return false;
            try
            {
                string exeName = Path.GetFileName(exePath);
                using (RegistryKey classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes"))
                {
                    if (classes == null)
                        return false;
                    using (RegistryKey app = classes.OpenSubKey(@"Applications\" + exeName + @"\shell\open\command"))
                    using (RegistryKey play = classes.OpenSubKey(@"SystemFileAssociations\audio\shell\Listen.Play\command"))
                    using (RegistryKey enqueue = classes.OpenSubKey(@"SystemFileAssociations\audio\shell\Listen.Enqueue\command"))
                    using (RegistryKey supported = classes.OpenSubKey(@"Applications\" + exeName + @"\SupportedTypes"))
                    {
                        if (app == null || play == null || enqueue == null || supported == null)
                            return false;
                        string appCommand = Convert.ToString(app.GetValue(string.Empty)) ?? string.Empty;
                        string playCommand = Convert.ToString(play.GetValue(string.Empty)) ?? string.Empty;
                        string enqueueCommand = Convert.ToString(enqueue.GetValue(string.Empty)) ?? string.Empty;
                        return appCommand.IndexOf(exePath, StringComparison.OrdinalIgnoreCase) >= 0 &&
                               playCommand.IndexOf(exePath, StringComparison.OrdinalIgnoreCase) >= 0 &&
                               enqueueCommand.IndexOf(exePath, StringComparison.OrdinalIgnoreCase) >= 0 &&
                               supported.GetValue(".wav") != null;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        public static void Unregister(string exePath)
        {
            string exeName = Path.GetFileName(exePath);
            try
            {
                using (RegistryKey classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", true))
                {
                    if (classes != null)
                    {
                        SafeDelete(classes, ProgId);
                        SafeDelete(classes, @"Applications\" + exeName);
                        SafeDelete(classes, @"SystemFileAssociations\audio\shell\Listen.Play");
                        SafeDelete(classes, @"SystemFileAssociations\audio\shell\Listen.Enqueue");

                        string[] extensions = AudioFormats.Extensions;
                        int i;
                        for (i = 0; i < extensions.Length; i++)
                        {
                            DeleteValueIfPresent(classes, extensions[i] + @"\OpenWithProgids", ProgId);
                            SafeDelete(classes, @"SystemFileAssociations\" + extensions[i] + @"\shell\Listen.Play");
                            SafeDelete(classes, @"SystemFileAssociations\" + extensions[i] + @"\shell\Listen.Enqueue");
                        }
                    }
                }

                using (RegistryKey appPaths = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths", true))
                {
                    if (appPaths != null)
                        SafeDelete(appPaths, exeName);
                }

                string[] supported = AudioFormats.Extensions;
                int j;
                for (j = 0; j < supported.Length; j++)
                {
                    try
                    {
                        using (RegistryKey openWith = Registry.CurrentUser.OpenSubKey(
                            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + supported[j] + @"\OpenWithProgids", true))
                        {
                            if (openWith != null)
                                openWith.DeleteValue(ProgId, false);
                        }
                    }
                    catch { }
                }

                using (RegistryKey software = Registry.CurrentUser.OpenSubKey("Software", true))
                {
                    if (software != null)
                        SafeDelete(software, @"Listen\Capabilities");
                }

                using (RegistryKey registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true))
                {
                    if (registered != null)
                    {
                        try { registered.DeleteValue(RegisteredApplicationValue, false); } catch { }
                    }
                }
            }
            catch { }

            NotifyAssociationChanged();
        }

        public static void OpenDefaultAppsSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=Listen") { UseShellExecute = true });
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); } catch { }
            }
        }

        private static void CleanupLegacyVisibleVerbs(RegistryKey classes, string exeName)
        {
            // Early development builds used per-extension SystemFileAssociations verbs.
            // Some builds also exposed Enqueue through ProgID/Applications. Keep
            // only SystemFileAssociations\audio as the user-visible verb source.
            SafeDelete(classes, ProgId + @"\shell\enqueue");
            SafeDelete(classes, @"Applications\" + exeName + @"\shell\enqueue");

            DeleteValueIfPresent(classes, ProgId + @"\shell\open", string.Empty);
            DeleteValueIfPresent(classes, ProgId + @"\shell\open", "MUIVerb");
            DeleteValueIfPresent(classes, ProgId + @"\shell\open", "ProgrammaticAccessOnly");
            DeleteValueIfPresent(classes, @"Applications\" + exeName + @"\shell\open", string.Empty);
            DeleteValueIfPresent(classes, @"Applications\" + exeName + @"\shell\open", "MUIVerb");
            DeleteValueIfPresent(classes, @"Applications\" + exeName + @"\shell\open", "ProgrammaticAccessOnly");

            string[] extensions = AudioFormats.Extensions;
            int i;
            for (i = 0; i < extensions.Length; i++)
            {
                SafeDelete(classes, @"SystemFileAssociations\" + extensions[i] + @"\shell\Listen.Play");
                SafeDelete(classes, @"SystemFileAssociations\" + extensions[i] + @"\shell\Listen.Enqueue");
            }
        }

        private static void SetValue(RegistryKey parent, string subKeyPath, string name, object value)
        {
            using (RegistryKey key = parent.CreateSubKey(subKeyPath))
            {
                if (key != null)
                    key.SetValue(name ?? string.Empty, value);
            }
        }

        private static void SafeDelete(RegistryKey parent, string subKeyPath)
        {
            try { parent.DeleteSubKeyTree(subKeyPath, false); } catch { }
        }

        private static void DeleteValueIfPresent(RegistryKey parent, string subKeyPath, string valueName)
        {
            try
            {
                using (RegistryKey key = parent.OpenSubKey(subKeyPath, true))
                {
                    if (key != null)
                        key.DeleteValue(valueName, false);
                }
            }
            catch { }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", string.Empty) + "\"";
        }

        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private static void NotifyAssociationChanged()
        {
            try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); } catch { }
        }
    }
}
