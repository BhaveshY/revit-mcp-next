using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// What a handler returns: a builder for every protocol v3 response field (SPEC §4.6.3). The dispatcher fills
    /// requestId, doc (when the handler did not), metrics, and the warnings/notices collected on the RequestContext.
    /// Data must be a JSON tree (dictionaries/lists/primitives) or wire DTOs; use OutputJson helpers for numbers.
    /// </summary>
    internal sealed class OpResult
    {
        /// <summary>Ids per change array sent on the wire (totals stay exact).</summary>
        public const int MaxChangeIds = 10000;
        /// <summary>Distinct warning entries kept per response (duplicates increase the count).</summary>
        public const int MaxWarnings = 100;
        private const int MaxIdsPerWarning = 20;

        private OpResult()
        {
        }

        public bool Ok { get; private set; }
        public string Code { get; private set; }
        public string Message { get; private set; }
        public object Details { get; private set; }
        public string Summary { get; private set; }
        public object Data { get; private set; }
        public ChangeSummary Changes { get; private set; }
        public NeedsConfirm NeedsConfirm { get; private set; }
        public Dictionary<string, object> Outputs { get; private set; }
        public List<BridgeWarning> Warnings { get; } = new List<BridgeWarning>();
        public List<BridgeNotice> Notices { get; } = new List<BridgeNotice>();
        public ResponseDoc Doc { get; private set; }
        public PageInfo Page { get; private set; }
        public PartialInfo Partial { get; private set; }
        public ResultFile File { get; private set; }
        public JobInfo Job { get; private set; }
        public bool CacheHit { get; private set; }

        /// <summary>Set by JobRunner.Start: the request completes when the job does (or at its deadline with job info).</summary>
        internal string HandoffJobId { get; private set; }

        public static OpResult Success(object data = null, string summary = null)
        {
            return new OpResult { Ok = true, Data = data, Summary = summary };
        }

        public static OpResult Fail(string code, string message, object details = null)
        {
            return new OpResult
            {
                Ok = false,
                Code = string.IsNullOrWhiteSpace(code) ? ErrorCodes.InternalError : code,
                Message = message ?? code,
                Details = details
            };
        }

        public static OpResult From(OpException exception)
        {
            return Fail(exception.Code, exception.Message, exception.Details);
        }

        internal static OpResult JobHandoff(string jobId)
        {
            return new OpResult { Ok = true, HandoffJobId = jobId, Job = new JobInfo { JobId = jobId, State = JobStates.Queued } };
        }

        public OpResult WithSummary(string summary)
        {
            Summary = summary;
            return this;
        }

        public OpResult WithData(object data)
        {
            Data = data;
            return this;
        }

        public OpResult WithDetails(object details)
        {
            Details = details;
            return this;
        }

        public OpResult WithChanges(ChangeSummary changes)
        {
            Changes = changes;
            return this;
        }

        /// <summary>Sets the change arrays (capped at <see cref="MaxChangeIds"/>; totals are the full counts).</summary>
        public OpResult WithChanges(IEnumerable<long> created, IEnumerable<long> modified, IEnumerable<long> deleted)
        {
            List<long> c = Distinct(created), m = Distinct(modified), d = Distinct(deleted);
            Changes = new ChangeSummary
            {
                Created = Cap(c),
                Modified = Cap(m),
                Deleted = Cap(d),
                CreatedTotal = c.Count,
                ModifiedTotal = m.Count,
                DeletedTotal = d.Count
            };
            return this;
        }

        public OpResult WithChanges(IEnumerable<ElementId> created, IEnumerable<ElementId> modified, IEnumerable<ElementId> deleted)
        {
            return WithChanges(
                created?.Where(id => id != null).Select(id => id.Value),
                modified?.Where(id => id != null).Select(id => id.Value),
                deleted?.Where(id => id != null).Select(id => id.Value));
        }

        public OpResult WithNeedsConfirm(NeedsConfirm needsConfirm)
        {
            NeedsConfirm = needsConfirm;
            return this;
        }

        /// <summary>Named output for $refs (e.g. "type", "view", "level") and for the broker.</summary>
        public OpResult WithOutput(string name, object value)
        {
            if (string.IsNullOrWhiteSpace(name)) return this;
            if (Outputs == null) Outputs = new Dictionary<string, object>(StringComparer.Ordinal);
            Outputs[name] = value;
            return this;
        }

        public OpResult WithOutputs(IDictionary<string, object> outputs)
        {
            if (outputs == null) return this;
            foreach (KeyValuePair<string, object> pair in outputs) WithOutput(pair.Key, pair.Value);
            return this;
        }

        public OpResult Warn(string code, string text, IEnumerable<long> ids = null)
        {
            AddWarning(Warnings, code, text, ids, 1);
            return this;
        }

        public OpResult Notice(string code, string text)
        {
            AddNotice(Notices, code, text);
            return this;
        }

        public OpResult WithDoc(ResponseDoc doc)
        {
            Doc = doc;
            return this;
        }

        /// <summary>Page info for lists. Pass all ids only when the request asked for them (ctx.WantIds).</summary>
        public OpResult WithPage(int total, int offset, int count, IEnumerable<long> ids = null)
        {
            Page = new PageInfo { Total = total, Offset = offset, Count = count, Ids = ids?.ToList() };
            return this;
        }

        public OpResult WithPartial(string reason, object resume)
        {
            Partial = new PartialInfo { Reason = string.IsNullOrWhiteSpace(reason) ? "deadline" : reason, Resume = resume };
            return this;
        }

        public OpResult WithFile(string path, string mime, int? width, int? height, long bytes, object meta = null)
        {
            File = new ResultFile { Path = path, Mime = mime, W = width, H = height, Bytes = bytes, Meta = meta };
            return this;
        }

        public OpResult WithJob(JobInfo job)
        {
            Job = job;
            return this;
        }

        public OpResult WithCacheHit(bool cacheHit = true)
        {
            CacheHit = cacheHit;
            return this;
        }

        /// <summary>Builds the wire response. Context warnings/notices are merged in by the dispatcher beforehand.</summary>
        public BridgeResponse ToResponse(string requestId, ResponseMetrics metrics)
        {
            Dictionary<string, object> outputs = null;
            if (Outputs != null) outputs = new Dictionary<string, object>(Outputs, StringComparer.Ordinal);
            var response = new BridgeResponse
            {
                RequestId = requestId ?? string.Empty,
                Ok = Ok,
                Code = Ok ? null : Code,
                Message = Ok ? null : Message,
                Details = Ok ? null : Details,
                Summary = Summary,
                Data = Data,
                Changes = Changes,
                NeedsConfirm = NeedsConfirm,
                Outputs = outputs,
                Doc = Doc,
                Page = Page,
                Partial = Partial,
                File = File,
                Job = Job,
                Metrics = metrics ?? new ResponseMetrics()
            };
            response.Metrics.CacheHit = response.Metrics.CacheHit || CacheHit;
            response.Warnings.AddRange(Warnings);
            response.Notices.AddRange(Notices);
            return response;
        }

        internal static void AddWarning(List<BridgeWarning> warnings, string code, string text, IEnumerable<long> ids, int count)
        {
            if (warnings == null) return;
            code = string.IsNullOrWhiteSpace(code) ? WarningCodes.RevitWarning : code;
            text = text ?? string.Empty;
            BridgeWarning existing = warnings.FirstOrDefault(w => w.Code == code && w.Text == text);
            if (existing == null)
            {
                if (warnings.Count >= MaxWarnings)
                {
                    // Keep the count honest without growing the list without bound.
                    existing = warnings[warnings.Count - 1];
                    existing.N += Math.Max(1, count);
                    return;
                }
                existing = new BridgeWarning { Code = code, Text = text, N = 0 };
                warnings.Add(existing);
            }
            existing.N += Math.Max(1, count);
            if (ids == null) return;
            foreach (long id in ids)
            {
                if (existing.Ids.Count >= MaxIdsPerWarning) break;
                if (!existing.Ids.Contains(id)) existing.Ids.Add(id);
            }
        }

        internal static void AddNotice(List<BridgeNotice> notices, string code, string text)
        {
            if (notices == null || string.IsNullOrWhiteSpace(code)) return;
            if (notices.Any(n => n.Code == code && n.Text == (text ?? string.Empty))) return;
            notices.Add(new BridgeNotice { Code = code, Text = text ?? string.Empty });
        }

        private static List<long> Distinct(IEnumerable<long> ids)
        {
            if (ids == null) return new List<long>();
            var seen = new HashSet<long>();
            var list = new List<long>();
            foreach (long id in ids)
            {
                if (seen.Add(id)) list.Add(id);
            }
            return list;
        }

        private static List<long> Cap(List<long> ids)
        {
            return ids.Count <= MaxChangeIds ? ids : ids.GetRange(0, MaxChangeIds);
        }
    }
}
