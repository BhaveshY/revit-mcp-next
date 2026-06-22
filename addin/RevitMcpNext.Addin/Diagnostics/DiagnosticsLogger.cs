using System;
using System.IO;

namespace RevitMcpNext.Addin.Diagnostics
{
    internal static class DiagnosticsLogger
    {
        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Error(string message, Exception exception = null)
        {
            Write("ERROR", message, exception);
        }

        private static void Write(string level, string message, Exception exception)
        {
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RevitMcpNext",
                    "logs");
                Directory.CreateDirectory(root);

                string path = Path.Combine(root, "addin-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".log");
                string line = DateTime.UtcNow.ToString("O") + " [" + level + "] " + message;
                if (exception != null)
                {
                    line += Environment.NewLine + exception;
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // Diagnostics must never affect Revit automation.
            }
        }
    }
}
