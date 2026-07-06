using System;
using System.IO;
using System.Text;

namespace RevitMcpNext.Addin.Ipc
{
    internal sealed class PipeAuthOptions
    {
        public const string AuthTokenEnvironmentVariable = "REVIT_MCP_NEXT_AUTH_TOKEN";
        public const string AuthConfigEnvironmentVariable = "REVIT_MCP_NEXT_AUTH_CONFIG";

        private readonly string _requiredToken;
        private readonly byte[] _requiredTokenBytes;

        private PipeAuthOptions(string requiredToken)
        {
            _requiredToken = requiredToken;
            _requiredTokenBytes = string.IsNullOrEmpty(requiredToken)
                ? new byte[0]
                : Encoding.UTF8.GetBytes(requiredToken);
        }

        public bool IsRequired
        {
            get { return !string.IsNullOrEmpty(_requiredToken); }
        }

        public static PipeAuthOptions FromEnvironment()
        {
            string configured = Environment.GetEnvironmentVariable(AuthTokenEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = ReadTokenConfig(Environment.GetEnvironmentVariable(AuthConfigEnvironmentVariable));
            }

            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = ReadTokenConfig(GetAssemblyRelativeAuthConfigPath());
            }

            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = ReadTokenConfig(GetDefaultAuthConfigPath());
            }

            return new PipeAuthOptions(string.IsNullOrWhiteSpace(configured) ? null : configured.Trim());
        }

        public bool IsAuthorized(string providedToken)
        {
            if (!IsRequired)
            {
                return true;
            }

            if (string.IsNullOrEmpty(providedToken))
            {
                return false;
            }

            byte[] providedTokenBytes = Encoding.UTF8.GetBytes(providedToken);
            return FixedTimeEquals(_requiredTokenBytes, providedTokenBytes);
        }

        private static bool FixedTimeEquals(byte[] expected, byte[] actual)
        {
            int diff = expected.Length ^ actual.Length;
            int length = Math.Max(expected.Length, actual.Length);

            for (int i = 0; i < length; i++)
            {
                byte expectedByte = i < expected.Length ? expected[i] : (byte)0;
                byte actualByte = i < actual.Length ? actual[i] : (byte)0;
                diff |= expectedByte ^ actualByte;
            }

            return diff == 0;
        }

        private static string GetAssemblyRelativeAuthConfigPath()
        {
            try
            {
                string assemblyPath = typeof(PipeAuthOptions).Assembly.Location;
                string addinDirectory = string.IsNullOrWhiteSpace(assemblyPath) ? null : Path.GetDirectoryName(assemblyPath);
                string installRoot = string.IsNullOrWhiteSpace(addinDirectory) ? null : Path.GetDirectoryName(addinDirectory);
                return string.IsNullOrWhiteSpace(installRoot)
                    ? null
                    : Path.Combine(installRoot, "config", "auth.env");
            }
            catch
            {
                return null;
            }
        }

        private static string GetDefaultAuthConfigPath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localAppData)
                ? null
                : Path.Combine(localAppData, "RevitMcpNext", "config", "auth.env");
        }

        private static string ReadTokenConfig(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            foreach (string line in File.ReadAllLines(path))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;

                string key = line.Substring(0, separator).Trim();
                if (!string.Equals(key, AuthTokenEnvironmentVariable, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return line.Substring(separator + 1).Trim().Trim('"');
            }

            return null;
        }
    }
}
