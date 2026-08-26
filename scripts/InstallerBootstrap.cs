using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace ListenSetup
{
    internal static class Program
    {
        private const string ProductName = "Listen";

        [STAThread]
        private static int Main()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "ListenSetup_" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(tempRoot);

                Extract("Listen.Payload.Listen.exe", Path.Combine(tempRoot, "Listen.exe"));
                Extract("Listen.Payload.LICENSE.md", Path.Combine(tempRoot, "LICENSE.md"));
                Extract("Listen.Payload.README.md", Path.Combine(tempRoot, "README.md"));
                Extract("Listen.Payload.CHANGELOG.md", Path.Combine(tempRoot, "CHANGELOG.md"));
                Extract("Listen.Payload.install.cmd", Path.Combine(tempRoot, "install.cmd"));
                Extract("Listen.Payload.uninstall.cmd", Path.Combine(tempRoot, "uninstall.cmd"));

                string command = Path.Combine(tempRoot, "install.cmd");
                string comSpec = Environment.GetEnvironmentVariable("COMSPEC");
                if (string.IsNullOrWhiteSpace(comSpec))
                    comSpec = "cmd.exe";

                var startInfo = new ProcessStartInfo
                {
                    FileName = comSpec,
                    Arguments = "/d /s /c \"\"" + command + "\"\"",
                    WorkingDirectory = tempRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                        throw new InvalidOperationException("Could not start the installer process.");

                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        MessageBox.Show(
                            "Listen could not be installed.\n\nInstaller exit code: " + process.ExitCode,
                            "Listen Setup",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return process.ExitCode;
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Listen could not be installed.\n\n" + ex.Message,
                    "Listen Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        private static void Extract(string resourceName, string destination)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream input = assembly.GetManifestResourceStream(resourceName))
            {
                if (input == null)
                    throw new InvalidOperationException("Missing installer resource: " + resourceName);

                using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
                // Temporary files are harmless if antivirus/indexing keeps one locked briefly.
            }
        }
    }
}
