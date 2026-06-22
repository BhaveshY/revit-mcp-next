using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed class DocumentGenerationTracker
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, long> _generations = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public long GetGeneration(Document document)
        {
            if (document == null) return 0;

            string fingerprint = ComputeDocumentFingerprint(document);
            lock (_gate)
            {
                if (!_generations.TryGetValue(fingerprint, out long generation))
                {
                    _generations[fingerprint] = 0;
                    return 0;
                }

                return generation;
            }
        }

        public long MarkChanged(Document document)
        {
            if (document == null) return 0;

            string fingerprint = ComputeDocumentFingerprint(document);
            lock (_gate)
            {
                _generations.TryGetValue(fingerprint, out long current);
                long next = current + 1;
                _generations[fingerprint] = next;
                return next;
            }
        }

        public static string ComputeDocumentFingerprint(Document document)
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
