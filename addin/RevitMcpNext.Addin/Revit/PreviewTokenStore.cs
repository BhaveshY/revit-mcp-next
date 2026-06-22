using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed class PreviewTokenStore
    {
        public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromMinutes(10);
        private const int MaxPreviewTokens = 128;

        private readonly object _gate = new object();
        private readonly Dictionary<string, PreviewToken> _tokens = new Dictionary<string, PreviewToken>(StringComparer.Ordinal);
        private readonly TimeSpan _timeToLive;

        public PreviewTokenStore()
            : this(DefaultTimeToLive)
        {
        }

        public PreviewTokenStore(TimeSpan timeToLive)
        {
            _timeToLive = timeToLive;
        }

        public PreviewToken Issue(
            string previewId,
            string documentFingerprint,
            long generation,
            string transactionName,
            string operationsHash,
            string changesHash,
            string changeSetHash,
            bool ready,
            int operationCount)
        {
            DateTimeOffset issuedAtUtc = DateTimeOffset.UtcNow;
            var token = new PreviewToken(
                previewId,
                documentFingerprint,
                generation,
                transactionName,
                operationsHash,
                changesHash,
                changeSetHash,
                ready,
                operationCount,
                issuedAtUtc,
                issuedAtUtc.Add(_timeToLive));

            lock (_gate)
            {
                RemoveExpiredUnsafe(issuedAtUtc);
                _tokens[previewId] = token;
                TrimUnsafe();
            }

            return token;
        }

        public PreviewTokenValidation Validate(
            string previewId,
            string documentFingerprint,
            long generation,
            string transactionName,
            string operationsHash,
            string changesHash,
            string changeSetHash)
        {
            if (string.IsNullOrWhiteSpace(previewId))
            {
                return PreviewTokenValidation.Failure("PREVIEW_ID_REQUIRED", "revit.apply_change_set requires a previewId returned by revit.preview_change_set.");
            }

            lock (_gate)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                RemoveExpiredUnsafe(now);

                if (!_tokens.TryGetValue(previewId, out PreviewToken token))
                {
                    return PreviewTokenValidation.Failure("PREVIEW_NOT_FOUND", "The supplied previewId was not issued by this add-in session or has already been consumed.");
                }

                if (token.ExpiresAtUtc <= now)
                {
                    _tokens.Remove(previewId);
                    return PreviewTokenValidation.Failure("PREVIEW_EXPIRED", "The preview has expired. Run revit.preview_change_set again before applying.");
                }

                if (!string.Equals(token.DocumentFingerprint, documentFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    return PreviewTokenValidation.Failure("PREVIEW_DOCUMENT_MISMATCH", "The preview was issued for a different document.");
                }

                if (token.Generation != generation)
                {
                    return PreviewTokenValidation.Failure("PREVIEW_GENERATION_MISMATCH", "The document changed after preview. Run revit.preview_change_set again before applying.");
                }

                if (!string.Equals(token.TransactionName, transactionName, StringComparison.Ordinal) ||
                    !string.Equals(token.OperationsHash, operationsHash, StringComparison.Ordinal))
                {
                    return PreviewTokenValidation.Failure("PREVIEW_ID_MISMATCH", "The supplied previewId does not match the current change set and document.");
                }

                if (!string.Equals(token.ChangesHash, changesHash, StringComparison.Ordinal))
                {
                    return PreviewTokenValidation.Failure("PREVIEW_STALE", "The current document no longer matches the reviewed preview. Run revit.preview_change_set again before applying.");
                }

                if (!string.IsNullOrWhiteSpace(changeSetHash) &&
                    !string.Equals(token.ChangeSetHash, changeSetHash, StringComparison.Ordinal))
                {
                    return PreviewTokenValidation.Failure("CHANGE_SET_HASH_MISMATCH", "The supplied changeSetHash does not match the reviewed preview.");
                }

                if (!token.Ready)
                {
                    return PreviewTokenValidation.Failure("PREVIEW_NOT_READY", "The preview contained blocked operations and cannot be applied.");
                }

                return PreviewTokenValidation.Success(token);
            }
        }

        public void Consume(string previewId)
        {
            if (string.IsNullOrWhiteSpace(previewId)) return;

            lock (_gate)
            {
                _tokens.Remove(previewId);
            }
        }

        private void RemoveExpiredUnsafe(DateTimeOffset now)
        {
            foreach (string previewId in _tokens
                .Where(pair => pair.Value.ExpiresAtUtc <= now)
                .Select(pair => pair.Key)
                .ToArray())
            {
                _tokens.Remove(previewId);
            }
        }

        private void TrimUnsafe()
        {
            int overflow = _tokens.Count - MaxPreviewTokens;
            if (overflow <= 0) return;

            foreach (string previewId in _tokens
                .OrderBy(pair => pair.Value.IssuedAtUtc)
                .Take(overflow)
                .Select(pair => pair.Key)
                .ToArray())
            {
                _tokens.Remove(previewId);
            }
        }
    }

    internal sealed class PreviewToken
    {
        public PreviewToken(
            string previewId,
            string documentFingerprint,
            long generation,
            string transactionName,
            string operationsHash,
            string changesHash,
            string changeSetHash,
            bool ready,
            int operationCount,
            DateTimeOffset issuedAtUtc,
            DateTimeOffset expiresAtUtc)
        {
            PreviewId = previewId;
            DocumentFingerprint = documentFingerprint;
            Generation = generation;
            TransactionName = transactionName;
            OperationsHash = operationsHash;
            ChangesHash = changesHash;
            ChangeSetHash = changeSetHash;
            Ready = ready;
            OperationCount = operationCount;
            IssuedAtUtc = issuedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string PreviewId { get; }
        public string DocumentFingerprint { get; }
        public long Generation { get; }
        public string TransactionName { get; }
        public string OperationsHash { get; }
        public string ChangesHash { get; }
        public string ChangeSetHash { get; }
        public bool Ready { get; }
        public int OperationCount { get; }
        public DateTimeOffset IssuedAtUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
    }

    internal sealed class PreviewTokenValidation
    {
        private PreviewTokenValidation(bool ok, PreviewToken token, string code, string message)
        {
            Ok = ok;
            Token = token;
            Code = code;
            Message = message;
        }

        public bool Ok { get; }
        public PreviewToken Token { get; }
        public string Code { get; }
        public string Message { get; }

        public static PreviewTokenValidation Success(PreviewToken token)
        {
            return new PreviewTokenValidation(true, token, null, null);
        }

        public static PreviewTokenValidation Failure(string code, string message)
        {
            return new PreviewTokenValidation(false, null, code, message);
        }
    }
}
