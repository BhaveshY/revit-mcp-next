using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RevitMcpNext.Addin.Diagnostics;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// Pipe auth token (D2 §3.1). Source order: env REVIT_MCP_NEXT_AUTH_TOKEN (tests), then &lt;home&gt;\config\auth.env.
    /// The file is created under a named mutex when missing, ACL'd to the user + SYSTEM + Administrators. Fails
    /// closed: when the file cannot be written an in-memory token is kept, authState becomes "unwritable" and every
    /// authenticated op returns AUTH_NOT_CONFIGURED. On a mismatch the file is re-read (at most once per second).
    /// </summary>
    internal sealed class AuthTokenStore
    {
        public const string TokenEnvironmentVariable = "REVIT_MCP_NEXT_AUTH_TOKEN";
        public const string TokenKey = "REVIT_MCP_NEXT_AUTH_TOKEN";
        private static readonly Regex ValidToken = new Regex("^[A-Za-z0-9_-]{43,}$", RegexOptions.CultureInvariant);

        private readonly McpHome _home;
        private readonly object _gate = new object();
        private string _token;
        private byte[] _tokenBytes = new byte[0];
        private DateTime _fileMtimeUtc;
        private long _fileLength = -1;
        private DateTime _loadedAtUtc;
        private DateTime _lastRecheckUtc = DateTime.MinValue;

        public AuthTokenStore(McpHome home)
        {
            _home = home ?? throw new ArgumentNullException(nameof(home));
        }

        /// <summary>Absolute path of auth.env.</summary>
        public string AuthFile => _home.AuthFile;

        /// <summary>file | env | unwritable | missing.</summary>
        public string State { get; private set; } = "missing";

        /// <summary>First 8 hex chars of sha256(token); empty without a token.</summary>
        public string Fingerprint { get; private set; } = string.Empty;

        public DateTime LoadedAtUtc => _loadedAtUtc;

        /// <summary>Why the token could not be persisted (state "unwritable").</summary>
        public string Problem { get; private set; }

        /// <summary>True when authenticated ops can be served (a token exists and is shared with the broker).</summary>
        public bool IsConfigured => State == "file" || State == "env";

        /// <summary>Loads the token, creating auth.env when missing or invalid. Never throws.</summary>
        public void EnsureCreated()
        {
            string fromEnv = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                SetToken(fromEnv.Trim(), "env", DateTime.MinValue, -1);
                return;
            }

            Mutex mutex = null;
            bool owned = false;
            try
            {
                mutex = new Mutex(false, @"Local\RevitMcpNext-auth-" + Sha256Hex(_home.Root.ToLowerInvariant()).Substring(0, 8));
                try
                {
                    owned = mutex.WaitOne(TimeSpan.FromSeconds(10));
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }

                if (TryLoadFromFile()) return;

                string token = GenerateToken();
                string content = "# revit-mcp-next local pipe token. Local only; never share or paste.\r\n" +
                                 "AUTH_CONFIG_VERSION=1\r\n" +
                                 TokenKey + "=" + token + "\r\n";
                try
                {
                    WriteTokenFile(content);
                }
                catch (Exception ex)
                {
                    Problem = "Could not write " + _home.AuthFile + ": " + ex.Message;
                    DiagnosticsLogger.Error("Auth token file could not be written; failing closed.", ex);
                    SetToken(token, "unwritable", DateTime.MinValue, -1);
                    return;
                }

                if (!TryLoadFromFile())
                {
                    Problem = "The auth token file " + _home.AuthFile + " could not be read back after writing it.";
                    SetToken(token, "unwritable", DateTime.MinValue, -1);
                }
                else
                {
                    DiagnosticsLogger.Info("Created the pipe auth token file " + _home.AuthFile + " (fp " + Fingerprint + ").");
                }
            }
            catch (Exception ex)
            {
                Problem = "Auth token setup failed: " + ex.Message;
                DiagnosticsLogger.Error("Auth token setup failed; failing closed.", ex);
                if (_token == null) SetToken(GenerateToken(), "unwritable", DateTime.MinValue, -1);
            }
            finally
            {
                if (owned)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
                mutex?.Dispose();
            }
        }

        /// <summary>
        /// Checks a request token. Returns null when authorized, else an error code (AUTH_NOT_CONFIGURED | AUTH_MISMATCH)
        /// with details {authFile, addinFp, brokerFp, addinTokenLoadedAtUtc}. Constant-time comparison.
        /// </summary>
        public string Verify(string providedToken, string providedFp, string providedFile, out Dictionary<string, object> details)
        {
            details = null;
            if (!IsConfigured)
            {
                details = BuildDetails(providedToken, providedFp, providedFile);
                if (Problem != null) details["problem"] = Problem;
                return ErrorCodes.AuthNotConfigured;
            }

            if (Matches(providedToken)) return null;

            // The broker may hold a newer token (installer rotation): re-read the file at most once per second.
            if (State == "file" && RecheckFile() && Matches(providedToken)) return null;

            details = BuildDetails(providedToken, providedFp, providedFile);
            return ErrorCodes.AuthMismatch;
        }

        public static string FingerprintOf(string token)
        {
            return string.IsNullOrEmpty(token) ? string.Empty : Sha256Hex(token).Substring(0, 8);
        }

        private bool Matches(string providedToken)
        {
            byte[] expected;
            lock (_gate) expected = _tokenBytes;
            if (expected.Length == 0 || string.IsNullOrEmpty(providedToken)) return false;
            byte[] actual = Encoding.UTF8.GetBytes(providedToken);
            int diff = expected.Length ^ actual.Length;
            int length = Math.Max(expected.Length, actual.Length);
            for (int i = 0; i < length; i++)
            {
                byte e = i < expected.Length ? expected[i] : (byte)0;
                byte a = i < actual.Length ? actual[i] : (byte)0;
                diff |= e ^ a;
            }
            return diff == 0;
        }

        private bool RecheckFile()
        {
            lock (_gate)
            {
                DateTime now = DateTime.UtcNow;
                if ((now - _lastRecheckUtc).TotalMilliseconds < 1000) return false;
                _lastRecheckUtc = now;
                try
                {
                    var info = new FileInfo(_home.AuthFile);
                    if (!info.Exists) return false;
                    if (info.LastWriteTimeUtc == _fileMtimeUtc && info.Length == _fileLength) return false;
                }
                catch
                {
                    return false;
                }
            }

            string previous = Fingerprint;
            bool loaded = TryLoadFromFile();
            if (loaded && previous != Fingerprint)
            {
                DiagnosticsLogger.Info("Auth token changed on disk; reloaded (fp " + previous + " -> " + Fingerprint + ").");
            }
            return loaded;
        }

        private bool TryLoadFromFile()
        {
            try
            {
                var info = new FileInfo(_home.AuthFile);
                if (!info.Exists) return false;
                string token = null;
                foreach (string line in ReadAllLinesShared(info.FullName))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    if (!string.Equals(line.Substring(0, separator).Trim(), TokenKey, StringComparison.OrdinalIgnoreCase)) continue;
                    token = line.Substring(separator + 1).Trim().Trim('"');
                }

                if (token == null || !ValidToken.IsMatch(token)) return false;
                SetToken(token, "file", info.LastWriteTimeUtc, info.Length);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Warn("auth", "Could not read " + _home.AuthFile + ": " + ex.Message);
                return false;
            }
        }

        private void WriteTokenFile(string content)
        {
            Directory.CreateDirectory(_home.ConfigDir);
            string path = _home.AuthFile;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, content, new UTF8Encoding(false));
                ApplyPrivateAcl(temporary);
                if (File.Exists(path))
                {
                    // Present but without a valid token (checked under the mutex): replace it atomically.
                    File.Replace(temporary, path, null, true);
                }
                else
                {
                    try
                    {
                        File.Move(temporary, path);
                    }
                    catch (IOException) when (File.Exists(path))
                    {
                        // Another creator (the installer or the other Revit year) won; its token is read back.
                    }
                }
                ApplyPrivateAcl(path);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch
                {
                    // Best effort.
                }
            }
        }

        private static void ApplyPrivateAcl(string path)
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    var security = new FileSecurity();
                    security.SetAccessRuleProtection(true, false);
                    foreach (IdentityReference principal in new IdentityReference[]
                    {
                        identity.User,
                        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                    })
                    {
                        if (principal == null) continue;
                        security.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl, AccessControlType.Allow));
                    }
#if NETFRAMEWORK
                    File.SetAccessControl(path, security);
#else
                    new FileInfo(path).SetAccessControl(security);
#endif
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Warn("auth", "Could not restrict the ACL of " + path + ": " + ex.Message);
            }
        }

        private void SetToken(string token, string state, DateTime mtimeUtc, long length)
        {
            lock (_gate)
            {
                _token = token;
                _tokenBytes = string.IsNullOrEmpty(token) ? new byte[0] : Encoding.UTF8.GetBytes(token);
                _fileMtimeUtc = mtimeUtc;
                _fileLength = length;
                _loadedAtUtc = DateTime.UtcNow;
                State = state;
                Fingerprint = FingerprintOf(token);
                if (state != "unwritable") Problem = null;
            }
        }

        private Dictionary<string, object> BuildDetails(string providedToken, string providedFp, string providedFile)
        {
            string brokerFp = !string.IsNullOrWhiteSpace(providedFp) ? providedFp : FingerprintOf(providedToken);
            var details = new Dictionary<string, object>
            {
                ["authFile"] = _home.AuthFile,
                ["addinFp"] = Fingerprint,
                ["brokerFp"] = brokerFp ?? string.Empty,
                ["addinTokenLoadedAtUtc"] = _loadedAtUtc == DateTime.MinValue ? null : _loadedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                ["authState"] = State
            };
            if (!string.IsNullOrWhiteSpace(providedFile)) details["brokerAuthFile"] = providedFile;
            return details;
        }

        private static IEnumerable<string> ReadAllLinesShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false), true))
            {
                var lines = new List<string>();
                string line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
                return lines;
            }
        }

        private static string GenerateToken()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        internal static string Sha256Hex(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }
    }
}
