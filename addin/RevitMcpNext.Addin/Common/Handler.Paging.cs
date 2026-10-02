using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed partial class RevitExternalEventHandler
    {
        private static int ParseCursor(string cursor, List<LegacyWarning> warnings)
        {
            if (string.IsNullOrWhiteSpace(cursor)) return 0;
            if (int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset) && offset >= 0) return offset;

            warnings.Add(new LegacyWarning
            {
                Code = "INVALID_CURSOR",
                Message = "Cursor '" + cursor + "' is invalid; returning the first page."
            });
            return 0;
        }

        private static PageResult<T> PageItems<T>(IEnumerable<T> items, int offset, int limit, bool includeTotalCount)
        {
            if (items == null)
            {
                return new PageResult<T>(new List<T>(), includeTotalCount ? 0 : (int?)null, false);
            }

            if (includeTotalCount)
            {
                // Single pass: count everything but only retain the requested page.
                var page = new List<T>(Math.Min(Math.Max(0, limit), 512));
                int totalCount = 0;
                foreach (T item in items)
                {
                    if (totalCount >= offset && page.Count < limit) page.Add(item);
                    totalCount++;
                }
                return new PageResult<T>(page, totalCount, offset + page.Count < totalCount);
            }

            List<T> window = items.Skip(offset).Take(limit + 1).ToList();
            bool truncated = window.Count > limit;
            if (truncated) window.RemoveAt(window.Count - 1);
            return new PageResult<T>(window, null, truncated);
        }

        private sealed class PageResult<T>
        {
            public PageResult(List<T> items, int? totalCount, bool truncated)
            {
                Items = items;
                TotalCount = totalCount;
                Truncated = truncated;
            }

            public List<T> Items { get; }
            public int? TotalCount { get; }
            public bool Truncated { get; }
        }
    }
}
