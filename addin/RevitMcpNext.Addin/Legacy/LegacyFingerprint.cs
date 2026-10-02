using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin.Revit
{
    /// <summary>The protocol v2 document fingerprint, kept for the legacy helpers (model delivery, change-set hashing).</summary>
    internal static class LegacyFingerprint
    {
        public static string Compute(Document document)
        {
            string raw = document.Title + "|" + document.PathName + "|" + document.GetHashCode().ToString(CultureInfo.InvariantCulture);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return BitConverter.ToString(hash).Replace("-", string.Empty).Substring(0, 16).ToLowerInvariant();
            }
        }
    }
}
