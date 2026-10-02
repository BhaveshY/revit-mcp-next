using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin
{
    /// <summary>Who caused a transaction: the MCP request executing when it happened.</summary>
    internal sealed class OursInfo
    {
        public string RequestId { get; set; }
        public string WriteTag { get; set; }
        public string ClientKey { get; set; }
        public string Key { get; set; }
    }

    /// <summary>One DocumentChanged record of a document's transaction journal (SPEC §6.9).</summary>
    internal sealed class JournalEntry
    {
        public long Generation { get; set; }
        public IReadOnlyList<string> Names { get; set; } = new List<string>();
        /// <summary>committed | undone | redone | rolled_back | group_rolled_back.</summary>
        public string Operation { get; set; } = "committed";
        public NameClass Class { get; set; }
        /// <summary>Set when the change happened while an MCP request executed and its names are write/ui; else null.</summary>
        public OursInfo Ours { get; set; }
        public DateTime AtUtc { get; set; }
        public int Added { get; set; }
        public int Modified { get; set; }
        public int Deleted { get; set; }
    }

    /// <summary>Raised after every non-temp DocumentChanged (UI thread). Subscribers must be fast and must not modify docs.</summary>
    internal sealed class DocumentChangeRecord
    {
        public long Rid { get; set; }
        public string DocKey { get; set; }
        public long Generation { get; set; }
        public JournalEntry Entry { get; set; }
        public ICollection<ElementId> AddedIds { get; set; } = new List<ElementId>();
        public ICollection<ElementId> ModifiedIds { get; set; } = new List<ElementId>();
        public ICollection<ElementId> DeletedIds { get; set; } = new List<ElementId>();
    }

    /// <summary>get_changes source data: net element changes since a generation (SPEC §6.6 m#, D1 get_changes).</summary>
    internal sealed class ChangeQuery
    {
        public long Now { get; set; }
        public long Since { get; set; }
        /// <summary>True when the ring dropped entries newer than Since (or stamps were reset): the lists are incomplete.</summary>
        public bool Incomplete { get; set; }
        public List<long> Added { get; set; } = new List<long>();
        public List<long> Modified { get; set; } = new List<long>();
        public List<long> Deleted { get; set; } = new List<long>();
        public List<JournalEntry> Transactions { get; set; } = new List<JournalEntry>();
    }

    /// <summary>Last known UI selection of a document.</summary>
    internal sealed class SelectionState
    {
        public int Count { get; set; }
        /// <summary>At most 10,000 ids (memory only; never in the snapshot).</summary>
        public IReadOnlyList<long> Ids { get; set; } = new List<long>();
        public DateTime? AtUtc { get; set; }
        /// <summary>True when the document is the UI-active one (otherwise the selection may be stale).</summary>
        public bool IsActiveDoc { get; set; }
        /// <summary>event | sample.</summary>
        public string Source { get; set; } = "none";
    }

    /// <summary>
    /// Event-driven per-instance document state (SPEC §4.6.5, §8.6, D2 §8-§9): rids and documentKeys (+ Save As aliases),
    /// generations (temp transactions ignored), per-element stamps, a change ring, the transaction journal, levels,
    /// selection, native busy markers and the DocSnapshot served on the control pipe without the UI thread.
    /// UI-thread APIs are marked; snapshot reads are thread-safe. Public signatures are frozen for wave 2.
    /// </summary>
    internal sealed class DocumentRegistry
    {
        public const int MaxDocs = 50;
        public const int MaxLevels = 30;
        public const int MaxSelectionIds = 10000;
        public const int ChangeRingCapacity = 20000;
        public const int StampCapacity = 500000;
        public const int JournalCapacity = 200;
        public const int MaxAliases = 5;
        private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(10);

        private readonly object _gate = new object();
        private readonly InstanceIdentity _identity;
        private readonly Dictionary<int, long> _ridByHash = new Dictionary<int, long>();
        private readonly Dictionary<long, DocState> _docs = new Dictionary<long, DocState>();
        private volatile DocSnapshot _current;
        private long _nextRid = 1;
        private long _seq;
        private string _state = "starting";
        private SnapshotUi _ui = new SnapshotUi();
        private NativeActivity _native;
        private ExecutingInfo _executing;
        private OursInfo _ours;
        private long? _activeRid;
        private long? _lastActiveProjectRid;
        private CodeExecutionState _codeExecution = new CodeExecutionState();
        private DateTime _lastIdlingUtc = DateTime.MinValue;
        private DateTime _lastIdlingPublishedUtc = DateTime.MinValue;
        private DateTime _lastRebuildUtc = DateTime.MinValue;
        private DateTime _lastProgressPublishUtc = DateTime.MinValue;
        private UIControlledApplication _attachedTo;
        private Application _worksharingSource;

        public DocumentRegistry(InstanceIdentity identity)
        {
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _current = BuildSnapshotUnsafe(DateTime.UtcNow);
        }

        /// <summary>The current snapshot (immutable by convention; thread-safe).</summary>
        public DocSnapshot Current => _current;

        public long Seq => _current.Seq;

        /// <summary>UTC time of the last Idling event (DateTime.MinValue when none yet).</summary>
        public DateTime LastIdlingUtc => _lastIdlingUtc;

        /// <summary>Raised after every snapshot swap (any thread). Handlers must not block or call the Revit API.</summary>
        public event Action<DocSnapshot> SnapshotChanged;

        /// <summary>Raised after every non-temp DocumentChanged (UI thread).</summary>
        public event Action<DocumentChangeRecord> DocumentChangeRecorded;

        /// <summary>
        /// Raised for every DocumentChanged before any filtering, temp transactions included (UI thread); the bool is
        /// true when the change belongs to an "MCP temp" transaction or a TempScope (e.g. run_csharp dry runs).
        /// </summary>
        public event Action<DocumentChangedEventArgs, bool> RawDocumentChanged;

        /// <summary>Raised once when Revit finished initializing (UI thread).</summary>
        public event Action Initialized;

        // ---------------------------------------------------------------------------------------------------------
        // Event wiring (UI thread, API context)
        // ---------------------------------------------------------------------------------------------------------

        public void Attach(UIControlledApplication application)
        {
            if (application == null) throw new ArgumentNullException(nameof(application));
            ControlledApplication app = application.ControlledApplication;
            app.ApplicationInitialized += OnApplicationInitialized;
            app.DocumentOpening += OnDocumentOpening;
            app.DocumentOpened += OnDocumentOpened;
            app.DocumentCreating += OnDocumentCreating;
            app.DocumentCreated += OnDocumentCreated;
            app.DocumentClosing += OnDocumentClosing;
            app.DocumentClosed += OnDocumentClosed;
            app.DocumentSavingAs += OnDocumentSavingAs;
            app.DocumentSavedAs += OnDocumentSavedAs;
            app.DocumentSaving += OnDocumentSaving;
            app.DocumentSaved += OnDocumentSaved;
            app.DocumentSynchronizingWithCentral += OnSynchronizing;
            app.DocumentSynchronizedWithCentral += OnSynchronized;
            app.DocumentReloadingLatest += OnReloadingLatest;
            app.DocumentReloadedLatest += OnReloadedLatest;
            app.FileExporting += OnFileExporting;
            app.FileExported += OnFileExported;
            app.DocumentPrinting += OnPrinting;
            app.DocumentPrinted += OnPrinted;
            app.ProgressChanged += OnProgressChanged;
            app.DocumentChanged += OnDocumentChanged;
            application.ViewActivated += OnViewActivated;
            application.SelectionChanged += OnSelectionChanged;
            _attachedTo = application;
        }

        public void Detach(UIControlledApplication application)
        {
            application = application ?? _attachedTo;
            if (application == null) return;
            try
            {
                ControlledApplication app = application.ControlledApplication;
                app.ApplicationInitialized -= OnApplicationInitialized;
                app.DocumentOpening -= OnDocumentOpening;
                app.DocumentOpened -= OnDocumentOpened;
                app.DocumentCreating -= OnDocumentCreating;
                app.DocumentCreated -= OnDocumentCreated;
                app.DocumentClosing -= OnDocumentClosing;
                app.DocumentClosed -= OnDocumentClosed;
                app.DocumentSavingAs -= OnDocumentSavingAs;
                app.DocumentSavedAs -= OnDocumentSavedAs;
                app.DocumentSaving -= OnDocumentSaving;
                app.DocumentSaved -= OnDocumentSaved;
                app.DocumentSynchronizingWithCentral -= OnSynchronizing;
                app.DocumentSynchronizedWithCentral -= OnSynchronized;
                app.DocumentReloadingLatest -= OnReloadingLatest;
                app.DocumentReloadedLatest -= OnReloadedLatest;
                app.FileExporting -= OnFileExporting;
                app.FileExported -= OnFileExported;
                app.DocumentPrinting -= OnPrinting;
                app.DocumentPrinted -= OnPrinted;
                app.ProgressChanged -= OnProgressChanged;
                app.DocumentChanged -= OnDocumentChanged;
                if (_worksharingSource != null) _worksharingSource.DocumentWorksharingEnabled -= OnWorksharingEnabled;
                _worksharingSource = null;
                application.ViewActivated -= OnViewActivated;
                application.SelectionChanged -= OnSelectionChanged;
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("registry", "Detaching document events failed.", ex);
            }
            _attachedTo = null;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Documents (UI thread)
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>The runtime id of a document (assigned on first sight; stable while open, survives Save As). UI thread.</summary>
        public long GetRid(Document document)
        {
            if (document == null) return 0;
            lock (_gate)
            {
                return EnsureStateUnsafe(document).Rid;
            }
        }

        /// <summary>True when the document already has a rid (does not assign one).</summary>
        public bool TryGetRid(Document document, out long rid)
        {
            rid = 0;
            if (document == null) return false;
            lock (_gate)
            {
                return _ridByHash.TryGetValue(SafeHash(document), out rid);
            }
        }

        /// <summary>The open (non-link) document with this rid, or null. UI thread.</summary>
        public Document FindDocument(Application app, long rid)
        {
            if (app == null || rid <= 0) return null;
            foreach (Document document in EnumerateDocuments(app))
            {
                lock (_gate)
                {
                    if (_ridByHash.TryGetValue(SafeHash(document), out long candidate) && candidate == rid) return document;
                }
            }
            return null;
        }

        /// <summary>Open (non-link) documents whose current key or Save As alias equals <paramref name="key"/>. UI thread.</summary>
        public List<Document> FindByKey(Application app, string key)
        {
            var result = new List<Document>();
            if (app == null || string.IsNullOrWhiteSpace(key)) return result;
            foreach (Document document in EnumerateDocuments(app))
            {
                long rid = GetRid(document);
                if (KeyMatches(rid, key)) result.Add(document);
            }
            return result;
        }

        /// <summary>The documentKey of an open document (computed now). UI thread.</summary>
        public string GetKey(Document document)
        {
            if (document == null) return string.Empty;
            lock (_gate)
            {
                DocState state = EnsureStateUnsafe(document);
                RefreshIdentityUnsafe(state, document);
                return state.Key;
            }
        }

        /// <summary>True when <paramref name="key"/> is the rid's current key or one of its aliases (case-insensitive).</summary>
        public bool KeyMatches(long rid, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            lock (_gate)
            {
                if (!_docs.TryGetValue(rid, out DocState state)) return false;
                return string.Equals(state.Key, key, StringComparison.OrdinalIgnoreCase) ||
                       state.Aliases.Any(alias => string.Equals(alias, key, StringComparison.OrdinalIgnoreCase));
            }
        }

        public IReadOnlyList<string> GetAliases(long rid)
        {
            lock (_gate)
            {
                return _docs.TryGetValue(rid, out DocState state) ? state.Aliases.ToList() : new List<string>();
            }
        }

        /// <summary>The snapshot entry of a rid (any thread), or null.</summary>
        public SnapshotDoc GetDoc(long rid)
        {
            return _current.Docs.FirstOrDefault(doc => doc.Rid == rid);
        }

        /// <summary>The response doc block {rid,key,title,year,kind,generation,modified}. UI thread.</summary>
        public ResponseDoc Describe(Document document)
        {
            if (document == null) return null;
            lock (_gate)
            {
                DocState state = EnsureStateUnsafe(document);
                RefreshIdentityUnsafe(state, document);
                bool modified = state.Modified;
                try { modified = document.IsModified; } catch { }
                return new ResponseDoc
                {
                    Rid = state.Rid,
                    Key = state.Key,
                    Title = state.Title,
                    Year = _identity.Year,
                    Kind = state.Kind,
                    Generation = state.Generation,
                    Modified = modified
                };
            }
        }

        /// <summary>Records that a family document was opened (EditFamily) from a project document. UI thread.</summary>
        public void SetFamilySource(Document familyDocument, Document projectDocument)
        {
            if (familyDocument == null) return;
            lock (_gate)
            {
                DocState state = EnsureStateUnsafe(familyDocument);
                state.FamilySourceRid = projectDocument == null ? (long?)null : EnsureStateUnsafe(projectDocument).Rid;
                state.Dirty = true;
            }
            Publish();
        }

        /// <summary>Builds the durable documentKey (D2 §9.2). UI thread.</summary>
        public static string BuildKey(Document document, int year, long rid)
        {
            string major = year.ToString(CultureInfo.InvariantCulture);
            try
            {
                if (document.IsModelInCloud)
                {
                    ModelPath cloud = document.GetCloudModelPath();
                    return major + "|cloud|" + cloud.GetProjectGUID().ToString("N") + "|" + cloud.GetModelGUID().ToString("N");
                }
            }
            catch
            {
                // Not a cloud model (or the cloud path is unavailable): fall through.
            }

            string central = CentralPath(document);
            if (document.IsWorkshared && !string.IsNullOrWhiteSpace(central))
            {
                return major + "|central|" + NormalizePath(central);
            }

            string path = SafePathName(document);
            if (!string.IsNullOrWhiteSpace(path)) return major + "|file|" + NormalizePath(path);

            bool detached = false;
            try { detached = document.IsDetached; } catch { }
            string title = SafeTitle(document);
            return major + (detached ? "|detached|" : "|unsaved|") + title + "|" + rid.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Path.GetFullPath with '/'->'\', trailing '\' trimmed, lower-cased (UNC and cloud paths kept as text).</summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string text = path.Trim().Replace('/', '\\');
            try
            {
                if (!text.StartsWith("RSN:", StringComparison.OrdinalIgnoreCase) &&
                    !text.StartsWith("BIM 360://", StringComparison.OrdinalIgnoreCase) &&
                    !text.StartsWith("Autodesk Docs://", StringComparison.OrdinalIgnoreCase))
                {
                    text = Path.GetFullPath(text);
                }
            }
            catch
            {
                // Keep the text as given.
            }
            return text.TrimEnd('\\').ToLowerInvariant();
        }

        // ---------------------------------------------------------------------------------------------------------
        // Generations, stamps, change ring, journal (UI thread)
        // ---------------------------------------------------------------------------------------------------------

        public long GetGeneration(Document document)
        {
            if (document == null) return 0;
            lock (_gate) return EnsureStateUnsafe(document).Generation;
        }

        public long GetGeneration(long rid)
        {
            lock (_gate) return _docs.TryGetValue(rid, out DocState state) ? state.Generation : 0;
        }

        /// <summary>
        /// The generation at which an element last changed (outside any open <see cref="DeferStamps"/> scope), or the
        /// stamp floor when unknown. A confirm plan is stale when any affected element's stamp exceeds the plan stamp.
        /// </summary>
        public long GetStamp(Document document, ElementId id)
        {
            if (document == null || id == null) return 0;
            lock (_gate)
            {
                DocState state = EnsureStateUnsafe(document);
                return state.Stamps.TryGetValue(id.Value, out long stamp) ? Math.Max(stamp, state.StampFloor) : state.StampFloor;
            }
        }

        /// <summary>The stamp returned for elements without a recorded change (raised when the stamp table overflows).</summary>
        public long GetStampFloor(Document document)
        {
            if (document == null) return 0;
            lock (_gate) return EnsureStateUnsafe(document).StampFloor;
        }

        /// <summary>
        /// Ids among <paramref name="ids"/> whose stamp is above <paramref name="planStamp"/> (changed after the plan).
        /// </summary>
        public List<long> ChangedSince(Document document, IEnumerable<ElementId> ids, long planStamp)
        {
            var changed = new List<long>();
            if (document == null || ids == null) return changed;
            lock (_gate)
            {
                DocState state = EnsureStateUnsafe(document);
                foreach (ElementId id in ids)
                {
                    if (id == null) continue;
                    long stamp = state.Stamps.TryGetValue(id.Value, out long value) ? Math.Max(value, state.StampFloor) : state.StampFloor;
                    if (stamp > planStamp) changed.Add(id.Value);
                }
            }
            return changed;
        }

        /// <summary>
        /// While the returned scope is open, stamp updates for this document are kept in an overlay and merged when it is
        /// disposed, so <see cref="GetStamp"/> keeps returning pre-request stamps during a confirmed apply. UI thread.
        /// </summary>
        public IDisposable DeferStamps(Document document)
        {
            if (document == null) return new NoopScope();
            lock (_gate)
            {
                DocState state = EnsureStateUnsafe(document);
                state.DeferDepth++;
                return new DeferScope(this, state.Rid);
            }
        }

        /// <summary>Net element changes since <paramref name="sinceGeneration"/> (ids capped at maxIds per list). UI thread.</summary>
        public ChangeQuery GetChanges(Document document, long sinceGeneration, int maxIds = 10000)
        {
            var query = new ChangeQuery { Since = sinceGeneration };
            if (document == null) return query;
            lock (_gate)
            {
                DocState state = EnsureStateUnsafe(document);
                query.Now = state.Generation;
                query.Incomplete = state.Ring.EvictedUpTo > sinceGeneration || state.StampFloor > sinceGeneration;
                // Net effect per element: did it exist at `since` (first event is not an add) and does it exist now
                // (last event is not a delete)? Undo/redo of creations and deletions net out correctly.
                var existedBefore = new Dictionary<long, bool>();
                var existsNow = new Dictionary<long, bool>();
                var order = new List<long>();
                foreach (ChangeEntry entry in state.Ring.Since(sinceGeneration))
                {
                    if (!existedBefore.ContainsKey(entry.Id))
                    {
                        existedBefore[entry.Id] = entry.Kind != ChangeKind.Added;
                        order.Add(entry.Id);
                    }
                    existsNow[entry.Id] = entry.Kind != ChangeKind.Deleted;
                }
                foreach (long id in order)
                {
                    bool before = existedBefore[id], now = existsNow[id];
                    if (!before && now) { if (query.Added.Count < maxIds) query.Added.Add(id); }
                    else if (before && !now) { if (query.Deleted.Count < maxIds) query.Deleted.Add(id); }
                    else if (before) { if (query.Modified.Count < maxIds) query.Modified.Add(id); }
                }
                query.Transactions = state.Journal.Where(entry => entry.Generation > sinceGeneration).ToList();
            }
            return query;
        }

        /// <summary>The newest journal entries (newest first). UI thread.</summary>
        public IReadOnlyList<JournalEntry> GetJournal(Document document, int max = JournalCapacity)
        {
            if (document == null) return new List<JournalEntry>();
            lock (_gate)
            {
                DocState state = EnsureStateUnsafe(document);
                return Enumerable.Reverse(state.Journal).Take(Math.Max(0, max)).ToList();
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Selection
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>The last known UI selection of the document (from SelectionChanged or sampling). UI thread.</summary>
        public SelectionState GetSelection(Document document)
        {
            if (document == null) return new SelectionState();
            lock (_gate)
            {
                DocState state = EnsureStateUnsafe(document);
                return new SelectionState
                {
                    Count = state.SelectionCount,
                    Ids = state.SelectionIds.ToList(),
                    AtUtc = state.SelectionAtUtc,
                    IsActiveDoc = _activeRid.HasValue && _activeRid.Value == state.Rid,
                    Source = state.SelectionSource
                };
            }
        }

        /// <summary>Samples the active document's selection (fallback for SelectionChanged; called after each batch). UI thread.</summary>
        public void SampleSelection(UIApplication app)
        {
            try
            {
                UIDocument uidoc = app?.ActiveUIDocument;
                Document document = uidoc?.Document;
                if (document == null || document.IsLinked) return;
                ICollection<ElementId> ids = uidoc.Selection.GetElementIds();
                RecordSelection(document, ids, "sample");
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Debug("registry", "Selection sampling failed: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // State markers (any thread)
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>starting | ready | stopping.</summary>
        public string State
        {
            get { lock (_gate) return _state; }
        }

        public void SetState(string state)
        {
            lock (_gate)
            {
                if (_state == state) return;
                _state = state;
            }
            Publish();
        }

        public void SetNative(string kind, Document document = null)
        {
            long? rid = null;
            if (document != null)
            {
                try { rid = GetRid(document); } catch { }
            }
            lock (_gate)
            {
                _native = new NativeActivity { Kind = kind, Rid = rid, SinceUtc = Utc(DateTime.UtcNow) };
            }
            Publish();
        }

        public void ClearNative()
        {
            lock (_gate)
            {
                if (_native == null) return;
                _native = null;
            }
            Publish();
        }

        /// <summary>The current native activity (any thread), or null.</summary>
        public NativeActivity Native
        {
            get { lock (_gate) return _native; }
        }

        /// <summary>Marks the MCP item executing on the UI thread (also attributes its transactions as "ours").</summary>
        public void SetExecuting(ExecutingInfo info)
        {
            lock (_gate)
            {
                _executing = info;
                _ours = info == null ? null : new OursInfo { RequestId = info.RequestId, WriteTag = info.WriteTag, ClientKey = info.ClientKey, Key = info.Op };
            }
            Publish();
        }

        public void ClearExecuting()
        {
            lock (_gate)
            {
                if (_executing == null && _ours == null) return;
                _executing = null;
                _ours = null;
            }
            Publish();
        }

        /// <summary>UI monitor state (P-REL-ADDIN UiStateMonitor). Publishes only when something changed.</summary>
        public void SetUi(SnapshotUi ui)
        {
            if (ui == null) return;
            lock (_gate)
            {
                SnapshotUi current = _ui;
                if (current != null && current.Foreground == ui.Foreground && current.Minimized == ui.Minimized &&
                    current.MainWindowEnabled == ui.MainWindowEnabled && current.Hung == ui.Hung &&
                    current.LastForegroundAtUtc == ui.LastForegroundAtUtc &&
                    string.Equals(current.Popup?.Title, ui.Popup?.Title, StringComparison.Ordinal) &&
                    current.Popup?.IsProgress == ui.Popup?.IsProgress)
                {
                    return;
                }
                SnapshotUi copy = ui.Clone();
                if (copy.LastViewActivatedAtUtc == null) copy.LastViewActivatedAtUtc = _ui?.LastViewActivatedAtUtc;
                _ui = copy;
            }
            Publish();
        }

        public SnapshotUi Ui
        {
            get { lock (_gate) return _ui.Clone(); }
        }

        /// <summary>Records an Idling tick (UI thread). Reconciles the document list every 10 s.</summary>
        public void OnIdling(UIApplication app)
        {
            DateTime now = DateTime.UtcNow;
            _lastIdlingUtc = now;
            bool rebuild;
            bool publish;
            lock (_gate)
            {
                rebuild = now - _lastRebuildUtc >= ReconcileInterval;
                publish = now - _lastIdlingPublishedUtc >= TimeSpan.FromSeconds(5);
                if (_state == "starting") { _state = "ready"; publish = true; }
            }
            if (app != null && _worksharingSource == null) HookWorksharingEnabled(app.Application);
            if (rebuild && app != null)
            {
                Rebuild(app.Application, app);
            }
            else if (publish)
            {
                Publish();
            }
        }

        public void SetCodeExecution(bool enabled, bool consented, DateTime? enabledSinceUtc = null)
        {
            lock (_gate)
            {
                if (_codeExecution.Enabled == enabled && _codeExecution.Consented == consented) return;
                _codeExecution = new CodeExecutionState
                {
                    Enabled = enabled,
                    Consented = consented,
                    EnabledSinceUtc = enabled && enabledSinceUtc.HasValue ? Utc(enabledSinceUtc.Value) : null
                };
            }
            Publish();
        }

        public CodeExecutionState CodeExecution
        {
            get { lock (_gate) return _codeExecution; }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Rebuild (UI thread)
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Full rebuild from app.Documents: assigns rids, refreshes keys/flags, prunes closed docs. UI thread.</summary>
        public void Rebuild(Application app, UIApplication uiApp = null)
        {
            if (app == null) return;
            try
            {
                var seen = new HashSet<long>();
                Document active = null;
                try
                {
                    UIApplication ui = uiApp ?? new UIApplication(app);
                    active = ui.ActiveUIDocument?.Document;
                }
                catch
                {
                    // No UI document yet.
                }

                lock (_gate)
                {
                    foreach (Document document in EnumerateDocuments(app))
                    {
                        DocState state = EnsureStateUnsafe(document);
                        RefreshIdentityUnsafe(state, document);
                        RefreshFlagsUnsafe(state, document);
                        if (!state.LevelsLoaded) RefreshLevelsUnsafe(state, document);
                        seen.Add(state.Rid);
                    }

                    foreach (long rid in _docs.Keys.Where(rid => !seen.Contains(rid)).ToList())
                    {
                        RemoveStateUnsafe(rid);
                    }

                    if (active != null && !active.IsLinked)
                    {
                        DocState activeState = EnsureStateUnsafe(active);
                        _activeRid = activeState.Rid;
                        if (activeState.Kind == "project") _lastActiveProjectRid = activeState.Rid;
                        RefreshActiveViewUnsafe(activeState, active);
                    }
                    else if (_activeRid.HasValue && !_docs.ContainsKey(_activeRid.Value))
                    {
                        _activeRid = null;
                    }
                    if (_lastActiveProjectRid.HasValue && !_docs.ContainsKey(_lastActiveProjectRid.Value)) _lastActiveProjectRid = null;
                    _lastRebuildUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("registry", "Document rebuild failed.", ex);
            }
            Publish();
        }

        /// <summary>Cheap refresh of the active document and view (after each batch; our ops may open or activate docs).</summary>
        public void RefreshActive(UIApplication app)
        {
            try
            {
                Document active = app?.ActiveUIDocument?.Document;
                bool changed = false;
                lock (_gate)
                {
                    if (active == null || active.IsLinked)
                    {
                        changed = _activeRid != null;
                        _activeRid = null;
                    }
                    else
                    {
                        DocState state = EnsureStateUnsafe(active);
                        if (_activeRid != state.Rid)
                        {
                            _activeRid = state.Rid;
                            changed = true;
                        }
                        if (state.Kind == "project" && _lastActiveProjectRid != state.Rid)
                        {
                            _lastActiveProjectRid = state.Rid;
                            changed = true;
                        }
                        changed |= RefreshActiveViewUnsafe(state, active);
                        bool modified = state.Modified;
                        try { modified = active.IsModified; } catch { }
                        if (modified != state.Modified)
                        {
                            state.Modified = modified;
                            state.Dirty = true;
                            changed = true;
                        }
                    }
                }
                if (changed) Publish();
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Debug("registry", "Active refresh failed: " + ex.Message);
            }
        }

        /// <summary>The UI-active document's rid, or null.</summary>
        public long? ActiveRid
        {
            get { lock (_gate) return _activeRid; }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Event handlers (UI thread; never throw)
        // ---------------------------------------------------------------------------------------------------------

        private void OnApplicationInitialized(object sender, ApplicationInitializedEventArgs args)
        {
            Guard("ApplicationInitialized", () =>
            {
                lock (_gate) _state = "ready";
                if (sender is Application app)
                {
                    HookWorksharingEnabled(app);
                    Rebuild(app);
                }
                else
                {
                    Publish();
                }
                Initialized?.Invoke();
            });
        }

        private void OnDocumentOpening(object sender, DocumentOpeningEventArgs args) => Guard("DocumentOpening", () => SetNative("opening"));

        private void OnDocumentCreating(object sender, DocumentCreatingEventArgs args) => Guard("DocumentCreating", () => SetNative("opening"));

        private void OnDocumentOpened(object sender, DocumentOpenedEventArgs args) => Guard("DocumentOpened", () => OnDocumentArrived(sender, args.Document));

        private void OnDocumentCreated(object sender, DocumentCreatedEventArgs args) => Guard("DocumentCreated", () => OnDocumentArrived(sender, args.Document));

        private void OnDocumentArrived(object sender, Document document)
        {
            lock (_gate) _native = null;
            if (document != null && !document.IsLinked)
            {
                lock (_gate)
                {
                    DocState state = EnsureStateUnsafe(document);
                    RefreshIdentityUnsafe(state, document);
                    RefreshFlagsUnsafe(state, document);
                    RefreshLevelsUnsafe(state, document);
                    if (state.Kind == "family" && string.IsNullOrWhiteSpace(state.Path) && !state.FamilySourceRid.HasValue)
                    {
                        // A family opened by EditFamily from a project: remember the project that was active.
                        state.FamilySourceRid = _lastActiveProjectRid;
                    }
                }
            }
            if (sender is Application app) Rebuild(app);
            else Publish();
        }

        /// <summary>DocumentWorksharingEnabled lives on Application (not ControlledApplication); hooked once in API context.</summary>
        private void HookWorksharingEnabled(Application app)
        {
            if (app == null || _worksharingSource != null) return;
            try
            {
                app.DocumentWorksharingEnabled += OnWorksharingEnabled;
                _worksharingSource = app;
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Debug("registry", "DocumentWorksharingEnabled is unavailable; Idling reconciliation covers it: " + ex.Message);
            }
        }

        private void OnWorksharingEnabled(object sender, DocumentWorksharingEnabledEventArgs args)
        {
            Guard("DocumentWorksharingEnabled", () =>
            {
                Document document = args.GetDocument();
                if (document == null) return;
                lock (_gate)
                {
                    DocState state = EnsureStateUnsafe(document);
                    string before = state.Key;
                    RefreshIdentityUnsafe(state, document);
                    AddAliasUnsafe(state, before);
                    RefreshFlagsUnsafe(state, document);
                }
                Publish();
            });
        }

        private void OnDocumentClosing(object sender, DocumentClosingEventArgs args)
        {
            Guard("DocumentClosing", () =>
            {
                Document document = args.Document;
                if (document == null || document.IsLinked) return;
                lock (_gate)
                {
                    DocState state = EnsureStateUnsafe(document);
                    state.Closing = true;
                    state.Dirty = true;
                }
                Publish();
            });
        }

        private void OnDocumentClosed(object sender, DocumentClosedEventArgs args)
        {
            Guard("DocumentClosed", () =>
            {
                if (args.Status == RevitAPIEventStatus.Cancelled || args.Status == RevitAPIEventStatus.Failed)
                {
                    lock (_gate)
                    {
                        foreach (DocState state in _docs.Values.Where(s => s.Closing))
                        {
                            state.Closing = false;
                            state.Dirty = true;
                        }
                    }
                }
                if (sender is Application app) Rebuild(app);
                else Publish();
            });
        }

        private void OnDocumentSavingAs(object sender, DocumentSavingAsEventArgs args) => Guard("DocumentSavingAs", () => SetNative("saving", args.Document));

        private void OnDocumentSavedAs(object sender, DocumentSavedAsEventArgs args)
        {
            Guard("DocumentSavedAs", () =>
            {
                Document document = args.Document;
                lock (_gate)
                {
                    _native = null;
                    if (document != null && !document.IsLinked)
                    {
                        DocState state = EnsureStateUnsafe(document);
                        string before = state.Key;
                        RefreshIdentityUnsafe(state, document);
                        AddAliasUnsafe(state, before);
                        RefreshFlagsUnsafe(state, document);
                        state.Modified = false;
                        state.LastSavedAtUtc = DateTime.UtcNow;
                    }
                }
                Publish();
            });
        }

        private void OnDocumentSaving(object sender, DocumentSavingEventArgs args) => Guard("DocumentSaving", () => SetNative("saving", args.Document));

        private void OnDocumentSaved(object sender, DocumentSavedEventArgs args)
        {
            Guard("DocumentSaved", () => MarkSaved(args.Document));
        }

        private void OnSynchronizing(object sender, DocumentSynchronizingWithCentralEventArgs args) => Guard("SynchronizingWithCentral", () => SetNative("sync", args.Document));

        private void OnSynchronized(object sender, DocumentSynchronizedWithCentralEventArgs args)
        {
            Guard("SynchronizedWithCentral", () => MarkSaved(args.Document));
        }

        private void OnReloadingLatest(object sender, DocumentReloadingLatestEventArgs args) => Guard("ReloadingLatest", () => SetNative("reloading", args.Document));

        private void OnReloadedLatest(object sender, DocumentReloadedLatestEventArgs args)
        {
            Guard("ReloadedLatest", () =>
            {
                Document document = args.Document;
                lock (_gate)
                {
                    _native = null;
                    if (document != null && !document.IsLinked)
                    {
                        DocState state = EnsureStateUnsafe(document);
                        state.Generation++;
                        state.LevelsLoaded = false;
                        RefreshLevelsUnsafe(state, document);
                        state.Dirty = true;
                    }
                }
                Publish();
            });
        }

        private void OnFileExporting(object sender, FileExportingEventArgs args) => Guard("FileExporting", () => SetNative("exporting", args.Document));

        private void OnFileExported(object sender, FileExportedEventArgs args) => Guard("FileExported", ClearNative);

        private void OnPrinting(object sender, DocumentPrintingEventArgs args) => Guard("DocumentPrinting", () => SetNative("printing", args.Document));

        private void OnPrinted(object sender, DocumentPrintedEventArgs args) => Guard("DocumentPrinted", ClearNative);

        private void OnProgressChanged(object sender, ProgressChangedEventArgs args)
        {
            try
            {
                bool publish;
                lock (_gate)
                {
                    if (_native == null) return;
                    _native = new NativeActivity
                    {
                        Kind = _native.Kind,
                        Rid = _native.Rid,
                        SinceUtc = _native.SinceUtc,
                        Progress = new NativeProgress { Caption = args.Caption, Pos = args.Position, Max = args.UpperRange }
                    };
                    DateTime now = DateTime.UtcNow;
                    publish = now - _lastProgressPublishUtc >= TimeSpan.FromSeconds(1);
                    if (publish) _lastProgressPublishUtc = now;
                }
                if (publish) Publish();
            }
            catch
            {
                // Progress is advisory.
            }
        }

        private void MarkSaved(Document document)
        {
            lock (_gate)
            {
                _native = null;
                if (document != null && !document.IsLinked)
                {
                    DocState state = EnsureStateUnsafe(document);
                    state.Modified = false;
                    state.LastSavedAtUtc = DateTime.UtcNow;
                    state.Dirty = true;
                }
            }
            Publish();
        }

        /// <summary>UTC time the document was last saved or synchronized in this session (null when never).</summary>
        public DateTime? LastSavedUtc(long rid)
        {
            lock (_gate) return _docs.TryGetValue(rid, out DocState state) ? state.LastSavedAtUtc : null;
        }

        private void OnViewActivated(object sender, ViewActivatedEventArgs args)
        {
            Guard("ViewActivated", () =>
            {
                Document document = args.Document;
                if (document == null || document.IsLinked) return;
                lock (_gate)
                {
                    DocState state = EnsureStateUnsafe(document);
                    _activeRid = state.Rid;
                    if (state.Kind == "project") _lastActiveProjectRid = state.Rid;
                    state.ActiveView = DescribeView(args.CurrentActiveView);
                    string now = Utc(DateTime.UtcNow);
                    state.LastActivatedAtUtc = now;
                    state.Dirty = true;
                    SnapshotUi ui = _ui.Clone();
                    ui.LastViewActivatedAtUtc = now;
                    _ui = ui;
                }
                Publish();
            });
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            try
            {
                Document document = args.GetDocument();
                if (document == null || document.IsLinked) return;
                RecordSelection(document, args.GetSelectedElements(), "event");
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Debug("registry", "SelectionChanged handling failed: " + ex.Message);
            }
        }

        private void RecordSelection(Document document, ICollection<ElementId> ids, string source)
        {
            bool changed;
            lock (_gate)
            {
                DocState state = EnsureStateUnsafe(document);
                int count = ids?.Count ?? 0;
                var list = new List<long>(Math.Min(count, MaxSelectionIds));
                if (ids != null)
                {
                    foreach (ElementId id in ids)
                    {
                        if (list.Count >= MaxSelectionIds) break;
                        if (id != null) list.Add(id.Value);
                    }
                }
                changed = count != state.SelectionCount || !list.SequenceEqual(state.SelectionIds);
                state.SelectionIds = list;
                state.SelectionCount = count;
                state.SelectionAtUtc = DateTime.UtcNow;
                state.SelectionSource = source;
                if (changed) state.Dirty = true;
            }
            if (changed) Publish();
        }

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs args)
        {
            try
            {
                Document document = args.GetDocument();
                if (document == null || document.IsLinked) return;
                ICollection<string> names = args.GetTransactionNames() ?? new List<string>();
                bool temp = TempScope.IsActive || Naming.AllTemp(names);
                try
                {
                    RawDocumentChanged?.Invoke(args, temp);
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Error("registry", "A raw DocumentChanged subscriber failed.", ex);
                }
                if (temp) return;

                ICollection<ElementId> added = args.GetAddedElementIds() ?? new List<ElementId>();
                ICollection<ElementId> modified = args.GetModifiedElementIds() ?? new List<ElementId>();
                ICollection<ElementId> deleted = args.GetDeletedElementIds() ?? new List<ElementId>();
                bool levelsChanged = false;
                try
                {
                    var levelFilter = new ElementClassFilter(typeof(Level));
                    levelsChanged = args.GetAddedElementIds(levelFilter).Count > 0 || args.GetModifiedElementIds(levelFilter).Count > 0;
                }
                catch
                {
                    levelsChanged = true;
                }

                DocumentChangeRecord record;
                lock (_gate)
                {
                    DocState state = EnsureStateUnsafe(document);
                    long generation = ++state.Generation;
                    state.Modified = true;
                    Dictionary<long, long> target = state.DeferDepth > 0 ? state.Overlay : state.Stamps;
                    foreach (ElementId id in added) { if (id != null) { target[id.Value] = generation; state.Ring.Add(generation, id.Value, ChangeKind.Added); } }
                    foreach (ElementId id in modified) { if (id != null) { target[id.Value] = generation; state.Ring.Add(generation, id.Value, ChangeKind.Modified); } }
                    foreach (ElementId id in deleted)
                    {
                        if (id == null) continue;
                        target[id.Value] = generation;
                        state.Ring.Add(generation, id.Value, ChangeKind.Deleted);
                        if (state.LevelIds.Contains(id.Value)) levelsChanged = true;
                    }
                    if (state.Stamps.Count + state.Overlay.Count > StampCapacity)
                    {
                        // Clear with a floor: unknown elements now count as changed at this generation (conservative).
                        state.Stamps.Clear();
                        state.Overlay.Clear();
                        state.StampFloor = generation;
                    }

                    NameClass cls = Naming.ClassifyAll(names);
                    var entry = new JournalEntry
                    {
                        Generation = generation,
                        Names = names.ToList(),
                        Operation = OperationName(args.Operation),
                        Class = cls,
                        Ours = (cls == NameClass.Write || cls == NameClass.Ui) ? _ours : null,
                        AtUtc = DateTime.UtcNow,
                        Added = added.Count,
                        Modified = modified.Count,
                        Deleted = deleted.Count
                    };
                    state.Journal.Add(entry);
                    if (state.Journal.Count > JournalCapacity) state.Journal.RemoveRange(0, state.Journal.Count - JournalCapacity);
                    if (levelsChanged) RefreshLevelsUnsafe(state, document);
                    state.Dirty = true;
                    record = new DocumentChangeRecord
                    {
                        Rid = state.Rid,
                        DocKey = state.Key,
                        Generation = generation,
                        Entry = entry,
                        AddedIds = added,
                        ModifiedIds = modified,
                        DeletedIds = deleted
                    };
                }

                Publish();
                try
                {
                    DocumentChangeRecorded?.Invoke(record);
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Error("registry", "A DocumentChanged subscriber failed.", ex);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("registry", "DocumentChanged handling failed.", ex);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Internals
        // ---------------------------------------------------------------------------------------------------------

        private void EndDefer(long rid)
        {
            lock (_gate)
            {
                if (!_docs.TryGetValue(rid, out DocState state)) return;
                state.DeferDepth = Math.Max(0, state.DeferDepth - 1);
                if (state.DeferDepth > 0) return;
                foreach (KeyValuePair<long, long> pair in state.Overlay) state.Stamps[pair.Key] = pair.Value;
                state.Overlay.Clear();
            }
        }

        private DocState EnsureStateUnsafe(Document document)
        {
            int hash = SafeHash(document);
            if (_ridByHash.TryGetValue(hash, out long rid) && _docs.TryGetValue(rid, out DocState existing)) return existing;
            rid = _nextRid++;
            _ridByHash[hash] = rid;
            var state = new DocState { Rid = rid, Hash = hash };
            _docs[rid] = state;
            RefreshIdentityUnsafe(state, document);
            RefreshFlagsUnsafe(state, document);
            return state;
        }

        private void RemoveStateUnsafe(long rid)
        {
            if (!_docs.TryGetValue(rid, out DocState state)) return;
            _docs.Remove(rid);
            _ridByHash.Remove(state.Hash);
            if (_activeRid == rid) _activeRid = null;
            if (_lastActiveProjectRid == rid) _lastActiveProjectRid = null;
        }

        private void RefreshIdentityUnsafe(DocState state, Document document)
        {
            try
            {
                state.Title = SafeTitle(document);
                state.Path = SafePathName(document);
                string central = CentralPath(document);
                state.Central = string.IsNullOrWhiteSpace(central) ? null : central;
                string key = BuildKey(document, _identity.Year, state.Rid);
                if (!string.Equals(key, state.Key, StringComparison.Ordinal))
                {
                    state.Key = key;
                    state.Dirty = true;
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Debug("registry", "Key refresh failed: " + ex.Message);
            }
        }

        private static void RefreshFlagsUnsafe(DocState state, Document document)
        {
            try
            {
                state.Kind = document.IsFamilyDocument ? "family" : "project";
                state.Workshared = document.IsWorkshared;
                bool cloud = false;
                try { cloud = document.IsModelInCloud; } catch { }
                state.Cloud = cloud;
                state.ReadOnly = document.IsReadOnly;
                state.Modified = document.IsModified;
                state.Dirty = true;
            }
            catch
            {
                // Flags are advisory; keep the previous values.
            }
        }

        private static void RefreshLevelsUnsafe(DocState state, Document document)
        {
            try
            {
                var levels = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>()
                    .Select(level => new { level.Id, level.Name, Elevation = SafeElevation(level) })
                    .OrderBy(level => level.Elevation).ThenBy(level => level.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                state.LevelIds = new HashSet<long>(levels.Select(level => level.Id.Value));
                state.Levels = levels.Take(MaxLevels)
                    .Select(level => new LevelRow { Id = level.Id.Value, Name = level.Name, ElevationMm = Math.Round(level.Elevation * 304.8, 1) })
                    .ToList();
                state.LevelsMore = Math.Max(0, levels.Count - MaxLevels);
                state.LevelsLoaded = true;
                state.Dirty = true;
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Debug("registry", "Level refresh failed: " + ex.Message);
            }
        }

        private static double SafeElevation(Level level)
        {
            try
            {
                return level.ProjectElevation;
            }
            catch
            {
                return level.Elevation;
            }
        }

        private static bool RefreshActiveViewUnsafe(DocState state, Document document)
        {
            View view = null;
            try { view = document.ActiveView; } catch { }
            ViewInfo info = DescribeView(view);
            if (info == null && state.ActiveView == null) return false;
            if (info != null && state.ActiveView != null && info.Id == state.ActiveView.Id && info.Name == state.ActiveView.Name) return false;
            state.ActiveView = info;
            state.Dirty = true;
            return true;
        }

        private static ViewInfo DescribeView(View view)
        {
            if (view == null) return null;
            try
            {
                int? scale = null;
                try { scale = view.Scale; } catch { }
                return new ViewInfo { Id = view.Id.Value, Name = view.Name, Type = view.ViewType.ToString(), Scale = scale };
            }
            catch
            {
                return null;
            }
        }

        private static void AddAliasUnsafe(DocState state, string previousKey)
        {
            if (string.IsNullOrWhiteSpace(previousKey) || string.Equals(previousKey, state.Key, StringComparison.OrdinalIgnoreCase)) return;
            state.Aliases.RemoveAll(alias => string.Equals(alias, previousKey, StringComparison.OrdinalIgnoreCase));
            state.Aliases.Insert(0, previousKey);
            if (state.Aliases.Count > MaxAliases) state.Aliases.RemoveRange(MaxAliases, state.Aliases.Count - MaxAliases);
            state.Dirty = true;
        }

        /// <summary>Builds and swaps a new snapshot, then raises <see cref="SnapshotChanged"/> (outside the lock).</summary>
        public void Publish()
        {
            DocSnapshot snapshot;
            lock (_gate)
            {
                snapshot = BuildSnapshotUnsafe(DateTime.UtcNow);
                _current = snapshot;
                _lastIdlingPublishedUtc = DateTime.UtcNow;
            }
            try
            {
                SnapshotChanged?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("registry", "A snapshot subscriber failed.", ex);
            }
        }

        private DocSnapshot BuildSnapshotUnsafe(DateTime nowUtc)
        {
            var snapshot = new DocSnapshot
            {
                Seq = ++_seq,
                AtUtc = Utc(nowUtc),
                InstanceId = _identity.InstanceId,
                Pid = _identity.Pid,
                Year = _identity.Year,
                Build = _identity.Build,
                Language = _identity.Language,
                AddinVersion = _identity.AddinVersion,
                GitSha = _identity.GitSha,
                PayloadId = _identity.PayloadId,
                CatalogHash = _identity.CatalogHash,
                Pipe = _identity.PipeName,
                ControlPipe = _identity.ControlPipeName,
                Home = _identity.HomeRoot,
                State = _state,
                Ui = _ui.Clone(),
                LastIdlingAtUtc = _lastIdlingUtc == DateTime.MinValue ? null : Utc(_lastIdlingUtc),
                Native = _native,
                Executing = _executing,
                ActiveRid = _activeRid,
                LastActiveProjectRid = _lastActiveProjectRid,
                CodeExecution = _codeExecution
            };
            foreach (DocState state in _docs.Values.OrderBy(s => s.Rid).Take(MaxDocs))
            {
                if (state.Dirty || state.Cached == null)
                {
                    state.Cached = new SnapshotDoc
                    {
                        Rid = state.Rid,
                        Key = state.Key ?? string.Empty,
                        Aliases = state.Aliases.ToList(),
                        Title = state.Title ?? string.Empty,
                        Kind = state.Kind ?? "project",
                        Path = string.IsNullOrWhiteSpace(state.Path) ? null : state.Path,
                        Central = state.Central,
                        Workshared = state.Workshared,
                        Cloud = state.Cloud,
                        ReadOnly = state.ReadOnly,
                        Modified = state.Modified,
                        Generation = state.Generation,
                        Closing = state.Closing,
                        ActiveView = state.ActiveView,
                        LastActivatedAtUtc = state.LastActivatedAtUtc,
                        Levels = state.Levels.ToList(),
                        LevelsMore = state.LevelsMore,
                        Selection = new SelectionInfo
                        {
                            Count = state.SelectionCount,
                            AtUtc = state.SelectionAtUtc.HasValue ? Utc(state.SelectionAtUtc.Value) : null
                        },
                        FamilySourceRid = state.FamilySourceRid
                    };
                    state.Dirty = false;
                }
                snapshot.Docs.Add(state.Cached);
            }
            return snapshot;
        }

        private static IEnumerable<Document> EnumerateDocuments(Application app)
        {
            var list = new List<Document>();
            try
            {
                foreach (Document document in app.Documents)
                {
                    try
                    {
                        if (document != null && document.IsValidObject && !document.IsLinked) list.Add(document);
                    }
                    catch
                    {
                        // Skip documents that are going away.
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Debug("registry", "Enumerating documents failed: " + ex.Message);
            }
            return list;
        }

        internal static string CentralPath(Document document)
        {
            try
            {
                if (!document.IsWorkshared) return string.Empty;
                ModelPath central = document.GetWorksharingCentralModelPath();
                return central == null ? string.Empty : ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SafePathName(Document document)
        {
            try { return document.PathName ?? string.Empty; } catch { return string.Empty; }
        }

        private static string SafeTitle(Document document)
        {
            try { return document.Title ?? string.Empty; } catch { return string.Empty; }
        }

        private static int SafeHash(Document document)
        {
            try { return document.GetHashCode(); } catch { return 0; }
        }

        private static string OperationName(UndoOperation operation)
        {
            switch (operation)
            {
                case UndoOperation.TransactionCommitted: return "committed";
                case UndoOperation.TransactionUndone: return "undone";
                case UndoOperation.TransactionRedone: return "redone";
                case UndoOperation.TransactionRolledBack: return "rolled_back";
                case UndoOperation.TransactionGroupRolledBack: return "group_rolled_back";
                default: return operation.ToString().ToLowerInvariant();
            }
        }

        internal static string Utc(DateTime value)
        {
            return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        private static void Guard(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Error("registry", name + " handling failed.", ex);
            }
        }

        private enum ChangeKind : byte
        {
            None = 0,
            Added = 1,
            Modified = 2,
            Deleted = 3
        }

        private struct ChangeEntry
        {
            public long Generation;
            public long Id;
            public ChangeKind Kind;
        }

        /// <summary>Fixed-capacity ring of element changes with an eviction mark (get_changes incomplete:true).</summary>
        private sealed class ChangeRing
        {
            private readonly ChangeEntry[] _items;
            private int _start;
            private int _count;

            public ChangeRing(int capacity)
            {
                _items = new ChangeEntry[capacity];
            }

            /// <summary>Highest generation of an evicted entry (0 when nothing was evicted).</summary>
            public long EvictedUpTo { get; private set; }

            public void Add(long generation, long id, ChangeKind kind)
            {
                if (_count == _items.Length)
                {
                    EvictedUpTo = Math.Max(EvictedUpTo, _items[_start].Generation);
                    _items[_start] = new ChangeEntry { Generation = generation, Id = id, Kind = kind };
                    _start = (_start + 1) % _items.Length;
                    return;
                }
                _items[(_start + _count) % _items.Length] = new ChangeEntry { Generation = generation, Id = id, Kind = kind };
                _count++;
            }

            public IEnumerable<ChangeEntry> Since(long generation)
            {
                for (int i = 0; i < _count; i++)
                {
                    ChangeEntry entry = _items[(_start + i) % _items.Length];
                    if (entry.Generation > generation) yield return entry;
                }
            }
        }

        private sealed class DocState
        {
            public long Rid;
            public int Hash;
            public string Key;
            public readonly List<string> Aliases = new List<string>();
            public string Title;
            public string Kind = "project";
            public string Path;
            public string Central;
            public bool Workshared;
            public bool Cloud;
            public bool ReadOnly;
            public bool Modified;
            public bool Closing;
            public long Generation;
            public readonly Dictionary<long, long> Stamps = new Dictionary<long, long>();
            public readonly Dictionary<long, long> Overlay = new Dictionary<long, long>();
            public int DeferDepth;
            public long StampFloor;
            public readonly ChangeRing Ring = new ChangeRing(ChangeRingCapacity);
            public readonly List<JournalEntry> Journal = new List<JournalEntry>();
            public HashSet<long> LevelIds = new HashSet<long>();
            public List<LevelRow> Levels = new List<LevelRow>();
            public int LevelsMore;
            public bool LevelsLoaded;
            public int SelectionCount;
            public List<long> SelectionIds = new List<long>();
            public DateTime? SelectionAtUtc;
            public string SelectionSource = "none";
            public ViewInfo ActiveView;
            public string LastActivatedAtUtc;
            public long? FamilySourceRid;
            public DateTime? LastSavedAtUtc;
            public bool Dirty = true;
            public SnapshotDoc Cached;
        }

        private sealed class DeferScope : IDisposable
        {
            private readonly DocumentRegistry _owner;
            private readonly long _rid;
            private bool _disposed;

            public DeferScope(DocumentRegistry owner, long rid)
            {
                _owner = owner;
                _rid = rid;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _owner.EndDefer(_rid);
            }
        }

        private sealed class NoopScope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
