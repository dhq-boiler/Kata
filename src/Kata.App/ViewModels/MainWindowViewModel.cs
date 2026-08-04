using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kata.App.Diagnostics;
using Kata.App.Graph;
using Kata.App.Localization;
using Kata.Core.Analysis;
using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Core.Sessions;
using Kata.Roslyn;

namespace Kata.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const string AllNamespacesToken = "(All)";

    private readonly CSharpLanguageAdapter _adapter = new();
    private SolutionModel? _currentModel;
    private SmellIndex _currentSmellIndex = SmellIndex.Empty;
    private BuiltGraph? _fullGraph;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _reloadDebounce;
    private int _suppressWatcherDepth;
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(500);
    private static readonly string[] WatchedExtensions =
        new[] { ".cs", ".csproj", ".slnx", ".sln" };

    public MainWindowViewModel()
    {
        CSharpLanguageAdapter.OnLoadPhaseCompleted = (label, ms) =>
        {
            PerfProbe.Record(label, ms);
            AdvanceLoadingProgress(label);
        };
        CSharpLanguageAdapter.OnLoadPhaseStarted = label =>
        {
            PerfProbe.PhaseStarted(label);
            SetLoadingPhase(label);
        };
        CSharpLanguageAdapter.OnLoadPhaseEnded = label => PerfProbe.PhaseEnded(label);
        LoadCommand = new AsyncRelayCommand<string>(LoadSolutionAsync);
        ClearFilterCommand = new RelayCommand(ClearFilter);
        ClearFocusCommand = new RelayCommand(() => FocusedNode = null);
        ClearImpactFocusCommand = new RelayCommand(ClearImpactFocus);
        ExpandImpactFocusCommand = new RelayCommand(ExpandImpactFocus);
        ClearDiffOverlayCommand = new RelayCommand(ClearDiffOverlay);
    }

    [ObservableProperty] private string _title = "Kata";
    [ObservableProperty] private string _status = "Idle";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SolutionDisplayName))]
    private string? _currentSolutionPath;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedNamespace = AllNamespacesToken;
    [ObservableProperty] private TypeNodeViewModel? _focusedNode;
    [ObservableProperty] private string _impactFocusStatus = string.Empty;
    [ObservableProperty] private bool _isImpactFocusActive;
    [ObservableProperty] private int _impactFocusHops = 1;
    [ObservableProperty] private string _diffOverlayStatus = string.Empty;
    [ObservableProperty] private bool _isDiffOverlayActive;
    [ObservableProperty] private MemberSourceViewModel? _currentMemberSource;
    [ObservableProperty] private bool _isCodeViewerVisible;
    [ObservableProperty] private bool _isReferencesPanelVisible;
    [ObservableProperty] private string _referencesHeader = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _loadingMessage = string.Empty;
    [ObservableProperty] private string _loadingPhase = string.Empty;
    [ObservableProperty] private double _loadingProgress;

    // Cumulative progress fraction reached at the END of each phase, based on
    // measured 対象-sln timings (open_sln 48%, map_async 40%, layout 8%…).
    // Real times vary per sln but the ORDER is stable, so the bar advances
    // monotonically. When a load includes only a subset of phases (short sln,
    // failed shim, etc.) the bar simply stops at whatever the last phase's
    // marker was — never rewinds.
    private static readonly Dictionary<string, double> LoadPhaseProgress = new(StringComparer.Ordinal)
    {
        ["open_sln"] = 0.48,
        ["discover"] = 0.49,
        ["cpp_compile"] = 0.51,
        ["inject_shim"] = 0.52,
        ["map_async"] = 0.92,
        ["foreign_projects"] = 0.93,
        ["graph"] = 0.94,
        ["layout"] = 0.99,
        ["filter"] = 1.00,
    };

    // Property (not field) so each lookup re-reads Strings.* — Strings resolves against
    // CultureInfo.CurrentUICulture on every call, and a static field would freeze the
    // initial language even after a Preferences change on subsequent loads
    private static Dictionary<string, string> LoadPhaseHumanName => new(StringComparer.Ordinal)
    {
        ["open_sln"] = Strings.LoadPhase_OpenSln,
        ["discover"] = Strings.LoadPhase_Discover,
        ["cpp_compile"] = Strings.LoadPhase_CppCompile,
        ["inject_shim"] = Strings.LoadPhase_InjectShim,
        ["map_async"] = Strings.LoadPhase_MapAsync,
        ["foreign_projects"] = Strings.LoadPhase_ForeignProjects,
        ["graph"] = Strings.LoadPhase_Graph,
        ["layout"] = Strings.LoadPhase_Layout,
        ["filter"] = Strings.LoadPhase_Filter,
    };

    // First-run fallback estimates (ms) per phase. Once a load completes, the
    // ACTUAL measured ms from PerfProbe replaces these — so a warm reload
    // interpolates against real timings instead of these guesses.
    private static readonly Dictionary<string, double> LoadPhaseEstimateMs = new(StringComparer.Ordinal)
    {
        ["open_sln"] = 12000,
        ["discover"] = 150,
        ["cpp_compile"] = 400,
        ["inject_shim"] = 200,
        ["map_async"] = 10000,
        ["foreign_projects"] = 200,
        ["graph"] = 300,
        ["layout"] = 1500,
        ["filter"] = 250,
    };

    // While a phase is in-flight we sweep LoadingProgress from _interpStart
    // toward _interpTarget on a background DispatcherTimer, so the bar creeps
    // instead of sitting motionless during 10-second phases and then jumping.
    private string _interpPhase = string.Empty;
    private double _interpStart;
    private double _interpTarget;
    private double _interpEstimatedMs;
    private System.Diagnostics.Stopwatch? _interpSw;
    private DispatcherTimer? _interpTimer;

    private void SetLoadingPhase(string label)
    {
        if (LoadPhaseHumanName.TryGetValue(label, out var human)) LoadingPhase = human;
        if (!LoadPhaseProgress.TryGetValue(label, out var target)) return;

        _interpPhase = label;
        _interpStart = LoadingProgress;
        _interpTarget = target;
        var lastMs = PerfProbe.LastMs(label);
        _interpEstimatedMs = lastMs > 0
            ? lastMs
            : LoadPhaseEstimateMs.GetValueOrDefault(label, 500);
        _interpSw = System.Diagnostics.Stopwatch.StartNew();
        EnsureInterpTimer();
    }

    private void AdvanceLoadingProgress(string label)
    {
        if (LoadPhaseProgress.TryGetValue(label, out var target) && target > LoadingProgress)
            LoadingProgress = target;
        if (string.Equals(_interpPhase, label, StringComparison.Ordinal))
            StopInterpTimer();
    }

    private void EnsureInterpTimer()
    {
        if (_interpTimer != null) return;
        _interpTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _interpTimer.Tick += (_, _) => TickInterpolation();
        _interpTimer.Start();
    }

    private void StopInterpTimer()
    {
        _interpTimer?.Stop();
        _interpTimer = null;
        _interpSw = null;
        _interpPhase = string.Empty;
    }

    private void TickInterpolation()
    {
        if (_interpSw is null || _interpEstimatedMs <= 0) return;
        // Asymptotic ease-out: fast at first (so a stalled bar visibly moves the
        // moment a slow phase begins), slowing toward the phase target. Capped
        // shy of 1.0 so we never overshoot the real completion event, which
        // will snap the bar to _interpTarget itself.
        var t = _interpSw.ElapsedMilliseconds / _interpEstimatedMs;
        var curve = 1.0 - System.Math.Exp(-2.5 * t);
        var fraction = _interpStart + (_interpTarget - _interpStart) * System.Math.Min(0.97, curve);
        if (fraction > LoadingProgress) LoadingProgress = fraction;
    }

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = new();
    public ObservableCollection<ReferenceRowViewModel> References { get; } = new();

    /// <summary>
    /// 現在 CodeViewer に表示中のメンバーに付いている smell を返す。CodeViewer 側で
    /// 「DuplicatedCode なら body 範囲を赤くハイライト」のような装飾判定に使う。
    /// </summary>
    public IReadOnlyList<Kata.Core.Analysis.CodeSmell> GetCurrentMemberSmells()
    {
        if (CurrentMemberSource is null) return Array.Empty<Kata.Core.Analysis.CodeSmell>();
        return _currentSmellIndex.ForMember(CurrentMemberSource.Source.Member);
    }

    public async Task RunFindReferencesForCurrentAsync()
    {
        if (_currentModel is null || CurrentMemberSource is null)
        {
            Status = Strings.Status_LoadMemberFirst;
            return;
        }
        var owner = CurrentMemberSource.Source.OwnerType;
        var member = CurrentMemberSource.Source.Member;
        References.Clear();
        ReferencesHeader = string.Format(Strings.Refs_SearchingFor, owner.FullyQualifiedName, member.Signature);
        IsReferencesPanelVisible = true;
        try
        {
            var refs = await _adapter.FindReferencesAsync(_currentModel, owner, member).ConfigureAwait(true);
            foreach (var r in refs)
            {
                References.Add(new ReferenceRowViewModel(r));
            }
            ReferencesHeader = string.Format(Strings.Refs_CountFound, refs.Count, owner.FullyQualifiedName, member.Signature);
        }
        catch (Exception ex)
        {
            ReferencesHeader = string.Format(Strings.Refs_FindFailed, ex.Message);
        }
    }

    public void CloseReferencesPanel()
    {
        IsReferencesPanelVisible = false;
        References.Clear();
    }

    public async Task NavigateToReferenceLocationAsync(ReferenceLocation location)
    {
        if (string.IsNullOrEmpty(location.FilePath) || !File.Exists(location.FilePath))
        {
            Status = string.Format(Strings.Status_RefTargetMissing, location.FilePath);
            return;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(location.FilePath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = string.Format(Strings.Status_ReadFailed, ex.Message);
            return;
        }

        var displayName = Path.GetFileName(location.FilePath);
        // Choose a human-friendly "what did I click on?" label for the crumb.
        // Prefer the last identifier in the snippet at the reference column; fall
        // back to filename. Signature carries "<display> || file:line" so
        // BreadcrumbItem.Label renders it as "display (file:line)".
        var display = ExtractSymbolFromSnippet(location.LineSnippet, location.Column) ?? displayName;
        var typeRef = new TypeRef($"<ref:{displayName}>");
        var memberRef = new MemberRef(typeRef, $"{display} || {displayName}:{location.Line}");
        var span = ClampSpan(location.SpanStart, location.SpanLength, text.Length);
        var source = new MemberSource(
            OwnerType: typeRef,
            Member: memberRef,
            FilePath: location.FilePath,
            SourceText: text,
            MemberSpanStart: span.Start,
            MemberSpanLength: span.Length,
            BodySpanStart: span.Start,
            BodySpanLength: span.Length);

        CurrentMemberSource = new MemberSourceViewModel(source);
        IsCodeViewerVisible = true;
        // Record the jump in the breadcrumb trail so the user can walk back to
        // where they were before double-clicking the ref row.
        AppendBreadcrumb(new BreadcrumbItem(typeRef, memberRef));
        Status = string.Format(Strings.Status_ReferenceLocation, displayName, location.Line, location.Column);
    }

    private void AppendBreadcrumb(BreadcrumbItem item)
    {
        // Skip duplicate append — repeatedly clicking the same target shouldn't
        // grow the trail.
        if (Breadcrumbs.Count > 0 && Breadcrumbs[^1] == item) return;
        Breadcrumbs.Add(item);
    }

    private static string? ExtractSymbolFromSnippet(string snippet, int column)
    {
        if (string.IsNullOrEmpty(snippet)) return null;
        // `column` is 1-based in Roslyn convention; clamp inside the snippet.
        int idx = System.Math.Max(0, System.Math.Min(snippet.Length - 1, column - 1));
        // Walk forward while we're on whitespace so we land ON an identifier.
        while (idx < snippet.Length && !IsIdentChar(snippet[idx])) idx++;
        if (idx >= snippet.Length) return null;
        int start = idx;
        while (start > 0 && IsIdentChar(snippet[start - 1])) start--;
        int end = idx;
        while (end < snippet.Length && IsIdentChar(snippet[end])) end++;
        return end > start ? snippet[start..end] : null;

        static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    private static (int Start, int Length) ClampSpan(int start, int length, int totalLength)
    {
        if (start < 0) start = 0;
        if (start > totalLength) start = totalLength;
        if (length < 0) length = 0;
        if (start + length > totalLength) length = totalLength - start;
        return (start, length);
    }

    public async Task LoadMemberSourceAsync(TypeRef ownerType, MemberRef member)
    {
        Breadcrumbs.Clear();
        await NavigateToMemberAsync(ownerType, member).ConfigureAwait(true);
    }

    public async Task NavigateDeeperAtOffsetAsync(int offsetInSource)
    {
        if (_currentModel is null || CurrentMemberSource is null)
        {
            return;
        }

        try
        {
            var current = CurrentMemberSource.Source;
            var target = await _adapter
                .ResolveMemberAtAsync(_currentModel, current.OwnerType, current.Member, offsetInSource)
                .ConfigureAwait(true);
            if (target is null)
            {
                var why = _adapter.LastResolveDiagnostic;
                Status = string.IsNullOrEmpty(why)
                    ? "No navigable symbol at that position."
                    : $"Ctrl+Click: {why}";
                return;
            }

            if (target.Value.OwnerType == current.OwnerType && target.Value.Member == current.Member)
            {
                return;
            }

            var resolveHint = _adapter.LastResolveDiagnostic;
            await NavigateToMemberAsync(target.Value.OwnerType, target.Value.Member).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(resolveHint))
            {
                Status = resolveHint;
            }
        }
        catch (Exception ex)
        {
            Status = string.Format(Strings.Status_NavigateFailed, ex.Message);
        }
    }

    public async Task NavigateBackToBreadcrumbAsync(BreadcrumbItem target)
    {
        var idx = Breadcrumbs.IndexOf(target);
        if (idx < 0)
        {
            return;
        }
        for (var i = Breadcrumbs.Count - 1; i > idx; i--)
        {
            Breadcrumbs.RemoveAt(i);
        }
        var loaded = await _adapter
            .GetMemberSourceAsync(_currentModel!, target.OwnerType, target.Member)
            .ConfigureAwait(true);
        if (loaded is null) return;
        CurrentMemberSource = new MemberSourceViewModel(loaded);
        IsCodeViewerVisible = true;
    }

    private async Task NavigateToMemberAsync(TypeRef ownerType, MemberRef member)
    {
        if (_currentModel is null) return;

        var source = await _adapter
            .GetMemberSourceAsync(_currentModel, ownerType, member)
            .ConfigureAwait(true);
        if (source is null)
        {
            Status = string.Format(Strings.Status_SourceNotFoundFor, member.Signature);
            return;
        }

        CurrentMemberSource = new MemberSourceViewModel(source);
        IsCodeViewerVisible = true;
        AppendBreadcrumb(new BreadcrumbItem(ownerType, member));
        Status = string.Format(Strings.Status_ViewingMember, ownerType.FullyQualifiedName, member.Signature);
    }

    public void CloseCodeViewer()
    {
        IsCodeViewerVisible = false;
        CurrentMemberSource = null;
        Breadcrumbs.Clear();
    }

    private IReadOnlyList<string> _impactSeeds = System.Array.Empty<string>();
    private HashSet<string>? _impactSet;
    private SolutionModel? _lastBeforeModel;

    public BulkObservableCollection<TypeNodeViewModel> Nodes { get; } = new();
    public BulkObservableCollection<ConnectionViewModel> Connections { get; } = new();
    public BulkObservableCollection<NamespaceClusterViewModel> NamespaceClusters { get; } = new();
    public ObservableCollection<string> NamespaceOptions { get; } = new();

    public AsyncRelayCommand<string> LoadCommand { get; }
    public RelayCommand ClearFilterCommand { get; }
    public RelayCommand ClearFocusCommand { get; }
    public RelayCommand ClearImpactFocusCommand { get; }
    public RelayCommand ExpandImpactFocusCommand { get; }
    public RelayCommand ClearDiffOverlayCommand { get; }

    public event Action? LayoutChanged;

    public string? SolutionRootDirectory => CurrentSolutionPath is null
        ? null
        : Path.GetDirectoryName(CurrentSolutionPath);

    /// <summary>タイトルバー右側に出す、開いているソリューションの表示名 (拡張子除いたファイル名)。未読込なら空。</summary>
    public string SolutionDisplayName => CurrentSolutionPath is null
        ? string.Empty
        : Path.GetFileNameWithoutExtension(CurrentSolutionPath);

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedNamespaceChanged(string value) => ApplyFilter();

    partial void OnFocusedNodeChanged(TypeNodeViewModel? value) => ApplyFocusDim();

    private void ApplyFocusDim()
    {
        if (FocusedNode is null)
        {
            foreach (var n in Nodes) n.IsDimmed = false;
            foreach (var e in Connections) e.IsDimmed = false;
            return;
        }

        var related = new HashSet<TypeNodeViewModel> { FocusedNode };
        foreach (var edge in Connections)
        {
            if (edge.SourceNode == FocusedNode) related.Add(edge.TargetNode);
            else if (edge.TargetNode == FocusedNode) related.Add(edge.SourceNode);
        }

        foreach (var n in Nodes) n.IsDimmed = !related.Contains(n);
        foreach (var e in Connections)
            e.IsDimmed = e.SourceNode != FocusedNode && e.TargetNode != FocusedNode;
    }

    public async Task LoadSolutionAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Status = string.Format(Strings.Status_SolutionNotFound, path);
            return;
        }

        Status = string.Format(Strings.Status_LoadingSln, Path.GetFileName(path));
        LoadingMessage = string.Format(Strings.Status_LoadingSln, Path.GetFileName(path));
        LoadingPhase = Strings.Loading_PhaseStarting;
        LoadingProgress = 0;
        IsLoading = true;
        Nodes.ReplaceAll(System.Array.Empty<TypeNodeViewModel>());
        Connections.ReplaceAll(System.Array.Empty<ConnectionViewModel>());
        IsDiffOverlayActive = false;
        DiffOverlayStatus = string.Empty;

        try
        {
            PerfProbe.Clear();
            PerfProbe.PhaseStarted("total");
            var totalSw = System.Diagnostics.Stopwatch.StartNew();

            PerfProbe.PhaseStarted("adapter");
            var adapterSw = System.Diagnostics.Stopwatch.StartNew();
            var model = await _adapter.LoadSolutionAsync(path).ConfigureAwait(true);
            adapterSw.Stop();
            PerfProbe.Record("adapter", adapterSw.ElapsedMilliseconds);
            PerfProbe.PhaseEnded("adapter");

            await RebuildViewFromModelAsync(model, trackPhases: true).ConfigureAwait(true);
            CurrentSolutionPath = path;

            totalSw.Stop();
            PerfProbe.Record("total", totalSw.ElapsedMilliseconds);
            PerfProbe.PhaseEnded("total");
            // A "render" phase covers WPF's post-load measure/arrange/render for the freshly
            // populated Nodes; it's not measurable directly but this marker helps identify
            // hitches that happen just after LoadSolutionAsync returns.
            PerfProbe.PhaseStarted("render");
            _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => PerfProbe.PhaseEnded("render"),
                System.Windows.Threading.DispatcherPriority.ContextIdle);

            // ApplyFilter set Status without filter/total (they weren't yet recorded).
            // Re-refresh with the full summary now that every phase has a number.
            RefreshLoadStatus();

            SessionHandshake.Publish(path);
            SetupWatcher(path);

            // Kick off static smell analysis off the UI thread. Results decorate the existing
            // TypeNode/MemberItem VMs in-place, so the diagram picks them up without a re-layout.
            _ = DetectSmellsInBackgroundAsync(model);
        }
        catch (Exception ex)
        {
            Status = string.Format(Strings.Status_Failed, ex.Message);
        }
        finally
        {
            StopInterpTimer();
            IsLoading = false;
        }
    }

    // Common view rebuild used by both LoadSolutionAsync (after a workspace open) and
    // ApplyAndReloadAsync (after an in-memory apply). trackPhases=false is the fast path
    // used by apply — it skips PerfProbe / LoadingPhase updates so a sub-second apply
    // doesn't pollute the perf log or flash a loading overlay.
    private async Task RebuildViewFromModelAsync(SolutionModel model, bool trackPhases)
    {
        _currentModel = model;

        if (trackPhases) SetLoadingPhase("graph");
        var graph = await Task.Run(() =>
        {
            if (trackPhases) PerfProbe.PhaseStarted("graph");
            var graphSw = System.Diagnostics.Stopwatch.StartNew();
            var g = SolutionGraphBuilder.Build(model);
            graphSw.Stop();
            if (trackPhases)
            {
                PerfProbe.Record("graph", graphSw.ElapsedMilliseconds);
                PerfProbe.PhaseEnded("graph");
                PerfProbe.PhaseStarted("layout");
            }
            var layoutSw = System.Diagnostics.Stopwatch.StartNew();
            SugiyamaLayout.Apply(g.Nodes, g.Connections.Where(c => c.Kind != ConnectionKind.Uses).ToList());
            layoutSw.Stop();
            if (trackPhases)
            {
                PerfProbe.Record("layout", layoutSw.ElapsedMilliseconds);
                PerfProbe.PhaseEnded("layout");
            }
            return g;
        }).ConfigureAwait(true);

        if (trackPhases)
        {
            AdvanceLoadingProgress("graph");
            SetLoadingPhase("layout");
            AdvanceLoadingProgress("layout");
        }
        _fullGraph = graph;

        RefreshNamespaceOptions(graph);

        if (IsImpactFocusActive)
        {
            RecomputeImpactSet();
            if (_impactSet is null) IsImpactFocusActive = false;
        }

        if (trackPhases)
        {
            SetLoadingPhase("filter");
            PerfProbe.PhaseStarted("filter");
        }
        var filterSw = System.Diagnostics.Stopwatch.StartNew();
        ApplyFilter(skipLayout: true);
        filterSw.Stop();
        if (trackPhases)
        {
            PerfProbe.Record("filter", filterSw.ElapsedMilliseconds);
            PerfProbe.PhaseEnded("filter");
            AdvanceLoadingProgress("filter");
        }
    }

    private async Task DetectSmellsInBackgroundAsync(SolutionModel model)
    {
        try
        {
            var index = await Task.Run(() => _adapter.DetectSmellsAsync(model)).ConfigureAwait(true);
            if (!ReferenceEquals(model, _currentModel)) return; // stale — a newer load happened
            _currentSmellIndex = index;
            DistributeSmellsToNodes();
        }
        catch
        {
            // Non-fatal — the diagram is still usable without smell decoration.
        }
    }

    private void DistributeSmellsToNodes()
    {
        if (_fullGraph is null) return;
        foreach (var node in _fullGraph.Nodes)
        {
            if (node.IsExternal) continue;
            node.ApplySmells(_currentSmellIndex);
        }
    }

    // Read the current source of a member — used by the smell popup's "AI に相談" path
    // to feed the LLM real code AND the file path (so it can produce a unified diff).
    public async Task<MemberSource?> GetMemberSourceAsync(TypeRef ownerType, MemberRef member, CancellationToken ct)
    {
        if (_currentModel is null) return null;
        return await _adapter.GetMemberSourceAsync(_currentModel, ownerType, member, ct).ConfigureAwait(false);
    }

    // Prefer the full graph — the filter can hide user nodes yet the smell popup still
    // needs to resolve the owning TypeNodeViewModel by TypeRef.
    public TypeNodeViewModel? FindNodeByRef(TypeRef typeRef)
    {
        if (_fullGraph is not null)
        {
            foreach (var n in _fullGraph.Nodes)
                if (n.Ref.Equals(typeRef)) return n;
        }
        foreach (var n in Nodes)
            if (n.Ref.Equals(typeRef)) return n;
        return null;
    }

    private void RefreshLoadStatus()
    {
        if (_fullGraph is null) return;
        var visibleUserNodes = _fullGraph.Nodes.Count(n => !n.IsExternal && MatchesFilter(n));
        var totalUser = _fullGraph.Nodes.Count(n => !n.IsExternal);
        var visibleEdgeCount = Connections.Count;
        var visibleExternalCount = Nodes.Count(n => n.IsExternal);
        var baseStatus = string.Format(Strings.Status_Showing, visibleUserNodes, totalUser, visibleExternalCount, visibleEdgeCount);
        var perf = PerfProbe.Summary("total", "adapter", "open_sln", "map_async", "cpp_compile", "inject_shim", "foreign_projects", "graph", "layout", "filter");
        if (perf.Length > 0) baseStatus += $"  ⏱ {perf}";
        var hitch = UiHitchMonitor.Current?.SnapshotStats();
        if (hitch is { } h && h.HitchCount > 0)
        {
            baseStatus += $"  🥶 UI hitches: {h.HitchCount} (max {h.MaxHitchMs}ms in [{h.WorstHitchContext}], last {h.LastHitchMs}ms in [{h.LastHitchContext}])";
        }
        var warnings = _adapter.StaleCppShimWarnings;
        Status = warnings.Count == 0
            ? baseStatus
            : $"{baseStatus}  ⚠ {warnings.Count} stale Cpp shim warning(s) — {warnings[0]}";
    }

    private void SetupWatcher(string solutionPath)
    {
        var root = Path.GetDirectoryName(solutionPath);
        if (root is null)
        {
            return;
        }

        var previous = _watcher;
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnSolutionFileChanged;
        _watcher.Created += OnSolutionFileChanged;
        _watcher.Deleted += OnSolutionFileChanged;
        _watcher.Renamed += OnSolutionFileChanged;

        previous?.Dispose();
    }

    private void OnSolutionFileChanged(object sender, FileSystemEventArgs e)
    {
        if (Volatile.Read(ref _suppressWatcherDepth) > 0)
        {
            return;
        }
        if (!IsWatchedFile(e.FullPath) || IsInGeneratedOrHiddenDirectory(e.FullPath))
        {
            return;
        }
        ScheduleReload();
    }

    private static bool IsWatchedFile(string path)
    {
        var ext = Path.GetExtension(path);
        foreach (var candidate in WatchedExtensions)
        {
            if (string.Equals(ext, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsInGeneratedOrHiddenDirectory(string path)
        => path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\.vs\", StringComparison.OrdinalIgnoreCase);

    private void ScheduleReload()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            _reloadDebounce ??= CreateReloadTimer();
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        }));
    }

    private DispatcherTimer CreateReloadTimer()
    {
        var timer = new DispatcherTimer { Interval = ReloadDebounce };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (Volatile.Read(ref _suppressWatcherDepth) > 0)
            {
                return;
            }
            if (CurrentSolutionPath is null)
            {
                return;
            }
            Status = Strings.Status_ExternalChangeReload;
            await LoadSolutionAsync(CurrentSolutionPath);
        };
        return timer;
    }

    public Task<ChangeSet?> ProposeRenameAsync(TypeNodeViewModel node, string newName, string? rationale)
    {
        var intent = IntentFactory.Rename(node.Ref, newName, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Rename {node.Name} → {newName}");
    }

    public Task<ChangeSet?> ProposeRenameMemberAsync(TypeNodeViewModel node, MemberRef member, string newName, string? rationale)
    {
        var intent = IntentFactory.Rename(node.Ref, newName, IntentSource.Human, rationale, member);
        return ProposeAsync(intent, progressLabel: $"Rename {node.Name}.{member.Signature} → {newName}");
    }

    public Task<ChangeSet?> ProposeExtractInterfaceAsync(
        TypeNodeViewModel node,
        IReadOnlyList<MemberRef> members,
        string interfaceName,
        string? rationale)
    {
        var intent = IntentFactory.ExtractInterface(node.Ref, members, interfaceName, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Extract interface {interfaceName} from {node.Name}");
    }

    public Task<ChangeSet?> ProposeExtractSuperclassAsync(
        TypeNodeViewModel node,
        IReadOnlyList<MemberRef> members,
        string superclassName,
        string? rationale)
    {
        var intent = IntentFactory.ExtractSuperclass(node.Ref, members, superclassName, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Extract superclass {superclassName} from {node.Name}");
    }

    public Task<ChangeSet?> ProposeExtractClassAsync(
        TypeNodeViewModel node,
        IReadOnlyList<MemberRef> members,
        string newClassName,
        string delegatePropertyName,
        string? rationale)
    {
        var intent = IntentFactory.ExtractClass(
            node.Ref, members, newClassName, delegatePropertyName, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Extract class {newClassName} from {node.Name}");
    }

    public Task<ChangeSet?> ProposeCollapseHierarchyAsync(
        TypeNodeViewModel subclassNode,
        TypeRef parent,
        string? rationale)
    {
        var intent = IntentFactory.CollapseHierarchy(subclassNode.Ref, parent, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Collapse {subclassNode.Name} into base");
    }

    public Task<ChangeSet?> ProposeRemoveSubclassAsync(
        TypeNodeViewModel subclassNode,
        TypeRef replacementBase,
        string? rationale)
    {
        var intent = IntentFactory.RemoveSubclass(subclassNode.Ref, replacementBase, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Remove subclass {subclassNode.Name}");
    }

    public TypeNodeViewModel? FindNode(TypeRef type)
    {
        return Nodes.FirstOrDefault(n => n.Ref.FullyQualifiedName == type.FullyQualifiedName);
    }

    public TypeRef? TryFindReplacementBase(TypeNodeViewModel subclass)
    {
        if (_fullGraph is null) return null;
        foreach (var edge in _fullGraph.Connections)
        {
            if (edge.SourceNode.Ref.FullyQualifiedName == subclass.Ref.FullyQualifiedName
                && edge.Kind == ConnectionKind.Inheritance)
            {
                return edge.TargetNode.Ref;
            }
        }
        return null;
    }

    public IReadOnlyList<TypeNodeViewModel> FindSubclasses(TypeNodeViewModel parent)
    {
        if (_fullGraph is null) return Array.Empty<TypeNodeViewModel>();
        var result = new List<TypeNodeViewModel>();
        foreach (var edge in _fullGraph.Connections)
        {
            if (edge.TargetNode.Ref.FullyQualifiedName == parent.Ref.FullyQualifiedName
                && edge.Kind == ConnectionKind.Inheritance
                && edge.SourceNode is TypeNodeViewModel sub
                && !result.Any(r => r.Ref.FullyQualifiedName == sub.Ref.FullyQualifiedName))
            {
                result.Add(sub);
            }
        }
        return result;
    }

    public Task<ChangeSet?> ProposePullUpMethodAsync(
        TypeNodeViewModel subclassNode,
        TypeRef parent,
        IReadOnlyList<MemberRef> members,
        string? rationale)
    {
        var intent = IntentFactory.PullUpMethod(subclassNode.Ref, parent, members, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Pull up {members.Count} method(s) from {subclassNode.Name}");
    }

    public Task<ChangeSet?> ProposePushDownMethodAsync(
        TypeNodeViewModel parentNode,
        TypeRef subclass,
        IReadOnlyList<MemberRef> members,
        string? rationale)
    {
        var intent = IntentFactory.PushDownMethod(parentNode.Ref, subclass, members, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Push down {members.Count} method(s) from {parentNode.Name}");
    }

    public Task<ChangeSet?> ProposePullUpFieldAsync(
        TypeNodeViewModel subclassNode,
        TypeRef parent,
        IReadOnlyList<MemberRef> members,
        string? rationale)
    {
        var intent = IntentFactory.PullUpField(subclassNode.Ref, parent, members, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Pull up {members.Count} field(s) from {subclassNode.Name}");
    }

    public Task<ChangeSet?> ProposePushDownFieldAsync(
        TypeNodeViewModel parentNode,
        TypeRef subclass,
        IReadOnlyList<MemberRef> members,
        string? rationale)
    {
        var intent = IntentFactory.PushDownField(parentNode.Ref, subclass, members, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Push down {members.Count} field(s) from {parentNode.Name}");
    }

    public Task<ChangeSet?> ProposeRenameFieldAsync(
        TypeNodeViewModel ownerNode,
        MemberRef field,
        string newName,
        string? rationale)
    {
        var intent = IntentFactory.RenameField(ownerNode.Ref, field, newName, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Rename field {ownerNode.Name}.{field.Signature} → {newName}");
    }

    public Task<ChangeSet?> ProposePullUpConstructorBodyAsync(
        TypeNodeViewModel subclassNode,
        TypeRef parent,
        string? rationale)
    {
        var intent = IntentFactory.PullUpConstructorBody(subclassNode.Ref, parent, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Pull up constructor body from {subclassNode.Name}");
    }

    public Task<ChangeSet?> ProposeEncapsulateFieldAsync(
        TypeNodeViewModel ownerNode,
        MemberRef field,
        string? rationale)
    {
        var intent = IntentFactory.EncapsulateField(ownerNode.Ref, field, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Encapsulate field {ownerNode.Name}.{field.Signature}");
    }

    public Task<ChangeSet?> ProposeMoveMethodAsync(
        TypeNodeViewModel sourceNode,
        TypeRef target,
        IReadOnlyList<MemberRef> members,
        string? rationale)
    {
        var intent = IntentFactory.MoveMethod(sourceNode.Ref, target, members, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Move {members.Count} method(s) from {sourceNode.Name}");
    }

    public Task<ChangeSet?> ProposeMoveFieldAsync(
        TypeNodeViewModel sourceNode,
        TypeRef target,
        IReadOnlyList<MemberRef> members,
        string? rationale)
    {
        var intent = IntentFactory.MoveField(sourceNode.Ref, target, members, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Move {members.Count} field(s) from {sourceNode.Name}");
    }

    public IReadOnlyList<TypeNodeViewModel> AllUserTypes()
        => _fullGraph is null
            ? System.Array.Empty<TypeNodeViewModel>()
            : _fullGraph.Nodes.Where(n => !n.IsExternal && !n.IsGhost).ToArray();

    public Task<ChangeSet?> ProposeReplaceConstructorWithFactoryAsync(
        TypeNodeViewModel ownerNode,
        string factoryName,
        bool makeConstructorPrivate,
        string? rationale)
    {
        var intent = IntentFactory.ReplaceConstructorWithFactory(
            ownerNode.Ref, IntentSource.Human, factoryName, makeConstructorPrivate, rationale);
        return ProposeAsync(intent, progressLabel: $"Replace constructor of {ownerNode.Name} with static factory");
    }

    public Task<ChangeSet?> ProposeReplaceMagicNumberAsync(
        TypeNodeViewModel ownerNode,
        string literalValue,
        string constantName,
        string constantType,
        string? rationale)
    {
        var intent = IntentFactory.ReplaceMagicNumber(
            ownerNode.Ref, literalValue, constantName, IntentSource.Human, constantType, rationale);
        return ProposeAsync(intent, progressLabel: $"Replace {literalValue} → {constantName} on {ownerNode.Name}");
    }

    public Task<ChangeSet?> ProposeChangeBidirectionalToUnidirectionalAsync(
        TypeNodeViewModel ownerNode,
        MemberRef field,
        string? rationale)
    {
        var intent = IntentFactory.ChangeBidirectionalToUnidirectional(
            ownerNode.Ref, field, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Drop back-reference {ownerNode.Name}.{field.Signature}");
    }

    public Task<ChangeSet?> ProposeIntroduceParameterObjectAsync(
        TypeNodeViewModel ownerNode,
        MemberRef method,
        string proposedObjectName,
        string parameterName,
        string? rationale)
    {
        var intent = IntentFactory.IntroduceParameterObject(
            ownerNode.Ref, method, proposedObjectName, IntentSource.Human, parameterName, targetNamespace: null, rationale);
        return ProposeAsync(intent, progressLabel: $"Introduce parameter object {proposedObjectName}");
    }

    public Task<ChangeSet?> ProposeAddParameterAsync(
        TypeNodeViewModel ownerNode,
        MemberRef method,
        string parameterType,
        string parameterName,
        string? defaultValue,
        string? rationale)
    {
        var intent = IntentFactory.AddParameter(
            ownerNode.Ref, method, parameterType, parameterName, IntentSource.Human, defaultValue, rationale);
        return ProposeAsync(intent, progressLabel: $"Add parameter {parameterType} {parameterName}");
    }

    public Task<ChangeSet?> ProposeRemoveParameterAsync(
        TypeNodeViewModel ownerNode,
        MemberRef method,
        string parameterName,
        string? rationale)
    {
        var intent = IntentFactory.RemoveParameter(
            ownerNode.Ref, method, parameterName, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Remove parameter {parameterName}");
    }

    public Task<ChangeSet?> ProposeReplaceDataValueWithObjectAsync(
        TypeNodeViewModel ownerNode,
        MemberRef field,
        string wrapperClassName,
        string innerFieldName,
        string? rationale)
    {
        var intent = IntentFactory.ReplaceDataValueWithObject(
            ownerNode.Ref, field, wrapperClassName, IntentSource.Human, innerFieldName, targetNamespace: null, rationale);
        return ProposeAsync(intent, progressLabel: $"Wrap {ownerNode.Name}.{field.Signature} in {wrapperClassName}");
    }

    public Task<ChangeSet?> ProposeRenameParameterAsync(
        TypeNodeViewModel ownerNode,
        MemberRef method,
        string oldName,
        string newName,
        string? rationale)
    {
        var intent = IntentFactory.RenameParameter(
            ownerNode.Ref, method, oldName, newName, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Rename parameter {oldName} → {newName}");
    }

    public Task<ChangeSet?> ProposeSelfEncapsulateFieldAsync(
        TypeNodeViewModel ownerNode,
        MemberRef field,
        string? rationale)
    {
        var intent = IntentFactory.SelfEncapsulateField(
            ownerNode.Ref, field, IntentSource.Human, propertyName: null, rationale);
        return ProposeAsync(intent, progressLabel: $"Self-encapsulate {ownerNode.Name}.{field.Signature}");
    }

    public Task<ChangeSet?> ProposeChangeReferenceToValueAsync(
        TypeNodeViewModel ownerNode,
        string? rationale)
    {
        var intent = IntentFactory.ChangeReferenceToValue(ownerNode.Ref, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Lock down {ownerNode.Name} to value semantics");
    }

    public Task<ChangeSet?> ProposeChangeValueToReferenceAsync(
        TypeNodeViewModel ownerNode,
        string keyType,
        string? rationale)
    {
        var intent = IntentFactory.ChangeValueToReference(
            ownerNode.Ref, IntentSource.Human, keyType: keyType, rationale: rationale);
        return ProposeAsync(intent, progressLabel: $"Add shared-instance registry to {ownerNode.Name}");
    }

    public Task<ChangeSet?> ProposeReplaceTypeCodeWithClassAsync(
        TypeNodeViewModel ownerNode,
        MemberRef field,
        string newClassName,
        IReadOnlyList<TypeCodeEntry> codes,
        string innerCodeType,
        string? rationale)
    {
        var intent = IntentFactory.ReplaceTypeCodeWithClass(
            ownerNode.Ref, field, newClassName, codes, IntentSource.Human,
            innerCodeType: innerCodeType, targetNamespace: null, rationale: rationale);
        return ProposeAsync(intent, progressLabel: $"Replace type code {ownerNode.Name}.{field.Signature} with {newClassName}");
    }

    public Task<ChangeSet?> ProposePreserveWholeObjectAsync(
        TypeNodeViewModel ownerNode,
        MemberRef method,
        TypeRef objectType,
        string parameterName,
        IReadOnlyList<string> replacedParameterNames,
        string? rationale)
    {
        var intent = IntentFactory.PreserveWholeObject(
            ownerNode.Ref, method, objectType, parameterName, replacedParameterNames, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Preserve whole object on {ownerNode.Name}.{method.Signature}");
    }

    public Task<ChangeSet?> ProposeReplaceArrayWithObjectAsync(
        TypeNodeViewModel ownerNode,
        MemberRef arrayField,
        string newClassName,
        IReadOnlyList<ArrayFieldMapping> mappings,
        string? rationale)
    {
        var intent = IntentFactory.ReplaceArrayWithObject(
            ownerNode.Ref, arrayField, newClassName, mappings, IntentSource.Human, targetNamespace: null, rationale);
        return ProposeAsync(intent, progressLabel: $"Replace array {ownerNode.Name}.{arrayField.Signature} with {newClassName}");
    }

    public Task<ChangeSet?> ProposeReplaceTypeCodeWithSubclassesAsync(
        TypeNodeViewModel ownerNode,
        IReadOnlyList<string> subclassNames,
        string? rationale)
    {
        var intent = IntentFactory.ReplaceTypeCodeWithSubclasses(
            ownerNode.Ref, subclassNames, IntentSource.Human, targetNamespace: null, rationale);
        return ProposeAsync(intent, progressLabel: $"Create {subclassNames.Count} subclass(es) of {ownerNode.Name}");
    }

    public Task<ChangeSet?> ProposeExtractHierarchyAsync(
        TypeNodeViewModel ownerNode,
        IReadOnlyList<string> subclassNames,
        IReadOnlyList<MemberRef> methodsToVirtualize,
        string? rationale)
    {
        var intent = IntentFactory.ExtractHierarchy(
            ownerNode.Ref, subclassNames, IntentSource.Human,
            methodsToVirtualize: methodsToVirtualize,
            targetNamespace: null,
            rationale: rationale);
        var label = methodsToVirtualize.Count == 0
            ? $"Create {subclassNames.Count} subclass(es) of {ownerNode.Name}"
            : $"Extract {subclassNames.Count} subclass(es) of {ownerNode.Name} + virtualize {methodsToVirtualize.Count} method(s)";
        return ProposeAsync(intent, progressLabel: label);
    }

    public Task<ChangeSet?> ProposeTeaseApartInheritanceAsync(
        TypeNodeViewModel primaryNode,
        string secondaryHierarchyName,
        IReadOnlyList<string> secondarySubclassNames,
        string delegationFieldName,
        string? rationale)
    {
        var intent = IntentFactory.TeaseApartInheritance(
            primaryHierarchyRoot: primaryNode.Ref,
            secondaryHierarchyName: secondaryHierarchyName,
            secondarySubclassNames: secondarySubclassNames,
            delegationFieldName: delegationFieldName,
            source: IntentSource.Human,
            targetNamespace: null,
            rationale: rationale);
        return ProposeAsync(intent, progressLabel: $"Tease {primaryNode.Name} apart → {secondaryHierarchyName} ({secondarySubclassNames.Count} case(s))");
    }

    public Task<ChangeSet?> ProposeExtractVariableAsync(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        string newVariableName,
        string? rationale)
    {
        var intent = IntentFactory.ExtractVariable(
            ownerType: ownerType,
            containingMember: containingMember,
            selectionStart: selectionStart,
            selectionLength: selectionLength,
            newVariableName: newVariableName,
            source: IntentSource.Human,
            rationale: rationale);
        return ProposeAsync(intent, progressLabel: $"Extract variable {newVariableName}");
    }

    public Task<ChangeSet?> ProposeDecomposeConditionalAsync(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        string conditionMethodName,
        string thenMethodName,
        string? elseMethodName,
        string? rationale)
    {
        var intent = IntentFactory.DecomposeConditional(
            ownerType: ownerType,
            containingMember: containingMember,
            selectionStart: selectionStart,
            selectionLength: selectionLength,
            conditionMethodName: conditionMethodName,
            thenMethodName: thenMethodName,
            source: IntentSource.Human,
            elseMethodName: elseMethodName,
            rationale: rationale);
        return ProposeAsync(intent, progressLabel: $"Decompose conditional → {conditionMethodName} / {thenMethodName}");
    }

    public Task<ChangeSet?> ProposeConsolidateConditionalExpressionAsync(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        string? rationale)
    {
        var intent = IntentFactory.ConsolidateConditionalExpression(
            ownerType: ownerType,
            containingMember: containingMember,
            selectionStart: selectionStart,
            selectionLength: selectionLength,
            source: IntentSource.Human,
            rationale: rationale);
        return ProposeAsync(intent, progressLabel: $"Consolidate conditional expression");
    }

    public Task<ChangeSet?> ProposeConsolidateDuplicateConditionalFragmentsAsync(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        string? rationale)
    {
        var intent = IntentFactory.ConsolidateDuplicateConditionalFragments(
            ownerType, containingMember, selectionStart, selectionLength,
            IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: "Consolidate duplicate conditional fragments");
    }

    public Task<ChangeSet?> ProposeReplaceNestedConditionalWithGuardClausesAsync(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        string? rationale)
    {
        var intent = IntentFactory.ReplaceNestedConditionalWithGuardClauses(
            ownerType, containingMember, selectionStart, selectionLength,
            IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: "Replace nested conditional with guard clauses");
    }

    public Task<ChangeSet?> ProposeIntroduceAssertionAsync(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        string assertionExpression,
        string? message,
        string? rationale)
    {
        var intent = IntentFactory.IntroduceAssertion(
            ownerType, containingMember, selectionStart, assertionExpression,
            IntentSource.Human, message, rationale);
        return ProposeAsync(intent, progressLabel: "Introduce assertion");
    }

    public Task<ChangeSet?> ProposeInlineMethodAsync(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        string? rationale)
    {
        var intent = IntentFactory.InlineMethod(
            ownerType: ownerType,
            containingMember: containingMember,
            selectionStart: selectionStart,
            selectionLength: selectionLength,
            source: IntentSource.Human,
            rationale: rationale);
        return ProposeAsync(intent, progressLabel: $"Inline method at selection");
    }

    public Task<ChangeSet?> ProposeInlineVariableAsync(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        string? rationale)
    {
        var intent = IntentFactory.InlineVariable(
            ownerType: ownerType,
            containingMember: containingMember,
            selectionStart: selectionStart,
            selectionLength: selectionLength,
            source: IntentSource.Human,
            rationale: rationale);
        return ProposeAsync(intent, progressLabel: $"Inline variable at selection");
    }

    public Task<ChangeSet?> ProposeExtractMethodAsync(
        TypeRef ownerType,
        MemberRef containingMember,
        int selectionStart,
        int selectionLength,
        string newMethodName,
        string? rationale)
    {
        var intent = IntentFactory.ExtractMethod(
            ownerType: ownerType,
            containingMember: containingMember,
            selectionStart: selectionStart,
            selectionLength: selectionLength,
            newMethodName: newMethodName,
            source: IntentSource.Human,
            rationale: rationale);
        return ProposeAsync(intent, progressLabel: $"Extract method {newMethodName} from {containingMember.Signature}");
    }

    public Task<ChangeSet?> ProposeConvertProceduralToObjectsAsync(
        TypeNodeViewModel proceduralNode,
        TypeNodeViewModel dataRecordNode,
        IReadOnlyList<MemberRef> methodsToMove,
        string? rationale)
    {
        var intent = IntentFactory.ConvertProceduralToObjects(
            proceduralClass: proceduralNode.Ref,
            dataRecordType: dataRecordNode.Ref,
            methodsToMove: methodsToMove,
            source: IntentSource.Human,
            rationale: rationale);
        return ProposeAsync(intent, progressLabel: $"Move {methodsToMove.Count} procedure(s) from {proceduralNode.Name} onto {dataRecordNode.Name}");
    }

    public Task<ChangeSet?> ProposeIntroduceNullObjectAsync(
        TypeNodeViewModel sourceNode,
        string? nullClassName,
        string? rationale)
    {
        var intent = IntentFactory.IntroduceNullObject(
            sourceType: sourceNode.Ref,
            source: IntentSource.Human,
            nullClassName: nullClassName,
            targetNamespace: null,
            rationale: rationale);
        return ProposeAsync(intent, progressLabel: $"Introduce Null Object for {sourceNode.Name}");
    }

    public Task<ChangeSet?> ProposeReplaceSubclassWithFieldsAsync(
        TypeNodeViewModel parentNode,
        IReadOnlyList<TypeRef> subclasses,
        string? rationale)
    {
        var intent = IntentFactory.ReplaceSubclassWithFields(
            parentNode.Ref, subclasses, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Flatten hierarchy under {parentNode.Name}");
    }

    public Task<ChangeSet?> ProposeRemoveSettingMethodAsync(
        TypeNodeViewModel ownerNode,
        MemberRef property,
        string? rationale)
    {
        var intent = IntentFactory.RemoveSettingMethod(ownerNode.Ref, property, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Remove setter from {ownerNode.Name}.{property.Signature}");
    }

    public Task<ChangeSet?> ProposeAddGhostTypeAsync(
        string typeName,
        NamespaceRef @namespace,
        TypeKind kind,
        string? rationale)
    {
        var intent = IntentFactory.AddGhostType(typeName, @namespace, kind, IntentSource.Human, rationale);
        return ProposeAsync(intent, progressLabel: $"Add {kind.ToString().ToLowerInvariant()} {@namespace.FullName}.{typeName}");
    }

    private IReadOnlyList<TypeRef>? _pendingImpactSeeds;

    public RefactoringIntent? LastProposedIntent { get; private set; }

    private async Task<ChangeSet?> ProposeAsync(RefactoringIntent intent, string progressLabel)
    {
        if (_currentModel is null)
        {
            Status = Strings.Status_NoSolutionLoaded;
            return null;
        }

        Status = string.Format(Strings.Status_Proposing, progressLabel);
        try
        {
            var changeSet = await _adapter
                .ProposeChangesAsync(_currentModel, new[] { intent })
                .ConfigureAwait(true);
            _pendingImpactSeeds = IntentAffectedTypes.Extract(intent);
            LastProposedIntent = intent;
            var suffix = string.IsNullOrEmpty(_adapter.LastRenameDiagnostic)
                ? string.Empty
                : $" — {_adapter.LastRenameDiagnostic}";
            Status = string.Format(Strings.Status_ProposalReady, changeSet.Changes.Count, suffix);
            return changeSet;
        }
        catch (Exception ex)
        {
            Status = string.Format(Strings.Status_ProposeFailed, ex.Message);
            return null;
        }
    }

    public async Task ApplyAndReloadAsync(ChangeSet changeSet, IReadOnlyList<TypeRef>? impactSeeds = null)
    {
        if (CurrentSolutionPath is null)
        {
            return;
        }

        // Callers that don't come through the intent pipeline (AI-suggested diff, manual
        // patch import, …) can pass an explicit seed set here so the post-apply flow
        // still lights up Impact Focus on the affected types.
        if (impactSeeds is not null && impactSeeds.Count > 0)
        {
            _pendingImpactSeeds = impactSeeds;
        }

        Status = string.Format(Strings.Status_ApplyingChanges, changeSet.Changes.Count);
        _lastBeforeModel = _currentModel;
        DiagLog.Line($"[apply] enter, changes={changeSet.Changes.Count}, _lastBeforeModel null? {_lastBeforeModel is null}");
        for (int i = 0; i < changeSet.Changes.Count; i++)
        {
            var c = changeSet.Changes[i];
            DiagLog.Line($"[apply]   change[{i}] kind={c.Kind} file={c.FilePath}");
        }
        Interlocked.Increment(ref _suppressWatcherDepth);
        try
        {
            // adapter.ApplyChangesAsync writes to disk AND incrementally updates its
            // Roslyn Solution, handing us back a fresh SolutionModel. No sln reopen,
            // no MSBuild round-trip, no LoadSolutionAsync side-effects (progress
            // overlay / Nodes.ReplaceAll(empty) flicker / etc.).
            var updatedModel = await _adapter.ApplyChangesAsync(changeSet).ConfigureAwait(true);
            DiagLog.Line($"[apply] after ApplyChangesAsync: updated types={updatedModel.Projects.Sum(p => p.Types.Count)}");

            // Safety net: if the incremental apply somehow produced a nearly-empty
            // model (compilation regression, MetadataReference loss, mapper aborted
            // mid-way, …), keep the pre-apply view and fall back to a full disk reload
            // instead of silently drawing a diagram with every type marked removed.
            var beforeTypeCount = _lastBeforeModel?.Projects.Sum(p => p.Types.Count) ?? 0;
            var afterTypeCount = updatedModel.Projects.Sum(p => p.Types.Count);
            if (beforeTypeCount > 0 && afterTypeCount * 2 < beforeTypeCount)
            {
                DiagLog.Line($"[apply] SAFETY NET TRIGGERED (before={beforeTypeCount}, after={afterTypeCount}) — full sln reload");
                Status = string.Format(
                    Strings.Status_ApplyBroken_Reload_Format,
                    beforeTypeCount, afterTypeCount);
                await LoadSolutionAsync(CurrentSolutionPath).ConfigureAwait(true);
                return;
            }
            DiagLog.Line($"[apply] safety net passed (before={beforeTypeCount}, after={afterTypeCount})");

            await RebuildViewFromModelAsync(updatedModel, trackPhases: false).ConfigureAwait(true);
            DiagLog.Line($"[apply] RebuildViewFromModelAsync done, _currentModel null? {_currentModel is null}");

            if (_lastBeforeModel is not null && _currentModel is not null)
            {
                var diff = Kata.Core.Diff.SolutionDiffer.Diff(_lastBeforeModel, _currentModel);
                var memberAdded = diff.Types.Sum(t => t.MemberDiffs.Count(m => m.State == Kata.Core.Diff.DiffState.Added));
                var memberRemoved = diff.Types.Sum(t => t.MemberDiffs.Count(m => m.State == Kata.Core.Diff.DiffState.Removed));
                DiagLog.Line($"[diff] types +{diff.AddedCount}/-{diff.RemovedCount}/~{diff.ModifiedCount}, members +{memberAdded}/-{memberRemoved}, HasChanges={diff.HasChanges}");
                if (diff.HasChanges) ApplyDiffOverlay(_lastBeforeModel, diff);
                else DiagLog.Line($"[diff] NO CHANGES (before types={_lastBeforeModel.Projects.Sum(p => p.Types.Count)} → after types={_currentModel.Projects.Sum(p => p.Types.Count)})");
            }
            var seeds = _pendingImpactSeeds;
            if (seeds is not null && seeds.Count > 0)
            {
                EnableImpactFocus(seeds, ImpactFocusHops > 0 ? ImpactFocusHops : 1);
            }

            // Re-run smell detection against the new model. Fire-and-forget so the
            // caller returns immediately.
            _ = DetectSmellsInBackgroundAsync(updatedModel);
        }
        catch (Exception ex)
        {
            DiagLog.Line($"[apply] EXCEPTION: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _suppressWatcherDepth);
        }
    }

    private void ApplyDiffOverlay(SolutionModel beforeModel, Kata.Core.Diff.SolutionDiff diff)
    {
        if (_fullGraph is null) return;

        // Index existing nodes by FQN.
        var byRef = _fullGraph.Nodes.ToDictionary(
            n => n.Ref.FullyQualifiedName,
            n => n,
            StringComparer.Ordinal);

        // Set DiffState on existing nodes for Added / Modified, and set member-level state.
        var missedNode = 0;
        var mvmHits = 0;
        var mvmMisses = 0;
        foreach (var td in diff.Types)
        {
            if (td.State == Kata.Core.Diff.DiffState.Removed) continue;
            if (!byRef.TryGetValue(td.Ref.FullyQualifiedName, out var node))
            {
                missedNode++;
                DiagLog.Line($"[overlay] byRef miss: {td.Ref.FullyQualifiedName}");
                continue;
            }
            node.DiffState = td.State;
            foreach (var md in td.MemberDiffs)
            {
                var mvm = node.Members.FirstOrDefault(m => m.Ref.Signature == md.Ref.Signature);
                if (mvm is not null) { mvm.DiffState = md.State; mvmHits++; }
                else
                {
                    mvmMisses++;
                    DiagLog.Line($"[overlay] member sig miss on {td.Ref.FullyQualifiedName}:");
                    DiagLog.Line($"[overlay]   expected sig: {md.Ref.Signature}");
                    DiagLog.Line($"[overlay]   node has {node.Members.Count} members:");
                    foreach (var mm in node.Members) DiagLog.Line($"[overlay]     - {mm.Ref.Signature}");
                }
            }
        }
        DiagLog.Line($"[overlay] ApplyDiffOverlay: types={diff.Types.Count}, missedNode={missedNode}, mvmHits={mvmHits}, mvmMisses={mvmMisses}, IsDiffOverlayActive→true");

        // Reinject Removed types as ghost overlay nodes (rebuilt from the "before" model).
        var beforeTypes = beforeModel.Projects
            .SelectMany(p => p.Types)
            .ToDictionary(t => t.Ref.FullyQualifiedName, t => t, StringComparer.Ordinal);

        var injectedNodes = new List<TypeNodeViewModel>();
        foreach (var td in diff.Types.Where(t => t.State == Kata.Core.Diff.DiffState.Removed))
        {
            if (byRef.ContainsKey(td.Ref.FullyQualifiedName)) continue;
            if (!beforeTypes.TryGetValue(td.Ref.FullyQualifiedName, out var oldType)) continue;
            var vm = new TypeNodeViewModel(oldType) { DiffState = Kata.Core.Diff.DiffState.Removed };
            foreach (var mvm in vm.Members)
            {
                mvm.DiffState = Kata.Core.Diff.DiffState.Removed;
            }
            injectedNodes.Add(vm);
        }

        if (injectedNodes.Count > 0)
        {
            var newNodes = _fullGraph.Nodes.Concat(injectedNodes).ToList();
            var newByRef = newNodes.ToDictionary(
                n => n.Ref.FullyQualifiedName,
                n => n,
                StringComparer.Ordinal);
            var newConnections = _fullGraph.Connections.ToList();

            // Add edges from beforeModel for removed types where both endpoints resolve.
            foreach (var td in diff.Types.Where(t => t.State == Kata.Core.Diff.DiffState.Removed))
            {
                if (!beforeTypes.TryGetValue(td.Ref.FullyQualifiedName, out var oldType)) continue;
                if (!newByRef.TryGetValue(oldType.Ref.FullyQualifiedName, out var source)) continue;
                foreach (var b in oldType.BaseTypes)
                {
                    if (newByRef.TryGetValue(b.FullyQualifiedName, out var target))
                    {
                        newConnections.Add(new ConnectionViewModel(source, target, ConnectionKind.Inheritance));
                    }
                }
                foreach (var i in oldType.ImplementedInterfaces)
                {
                    if (newByRef.TryGetValue(i.FullyQualifiedName, out var target))
                    {
                        newConnections.Add(new ConnectionViewModel(source, target, ConnectionKind.Interface));
                    }
                }
            }

            SugiyamaLayout.Apply(newNodes, newConnections.Where(c => c.Kind != ConnectionKind.Uses).ToList());
            _fullGraph = new BuiltGraph(newNodes, newConnections);
        }

        DiffOverlayStatus = string.Format(Strings.DiffOverlay_Summary, diff.AddedCount, diff.RemovedCount, diff.ModifiedCount);
        IsDiffOverlayActive = true;
        ApplyFilter();
    }

    private void ClearDiffOverlay()
    {
        if (!IsDiffOverlayActive) return;
        // Simplest reset path: reload the solution which rebuilds _fullGraph from scratch.
        _lastBeforeModel = null;
        IsDiffOverlayActive = false;
        DiffOverlayStatus = string.Empty;
        if (CurrentSolutionPath is not null)
        {
            _ = LoadSolutionAsync(CurrentSolutionPath);
        }
    }

    private void ClearFilter()
    {
        SearchText = string.Empty;
        SelectedNamespace = AllNamespacesToken;
    }

    private void RefreshNamespaceOptions(BuiltGraph graph)
    {
        NamespaceOptions.Clear();
        NamespaceOptions.Add(AllNamespacesToken);
        var namespaces = graph.Nodes
            .Where(n => !n.IsExternal)
            .Select(n => n.Namespace.FullName)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
        foreach (var ns in namespaces)
        {
            NamespaceOptions.Add(ns);
        }

        SelectedNamespace = AllNamespacesToken;
    }

    private void ApplyFilter() => ApplyFilter(skipLayout: false);

    private void ApplyFilter(bool skipLayout)
    {
        // Sync path: chunked=false means no awaits fire, so ApplyFilterCore completes
        // synchronously. GetAwaiter().GetResult() surfaces any exception without deadlocking.
        ApplyFilterCore(skipLayout, chunked: false).GetAwaiter().GetResult();
    }

    private Task ApplyFilterChunkedAsync(bool skipLayout) => ApplyFilterCore(skipLayout, chunked: true);

    private async Task ApplyFilterCore(bool skipLayout, bool chunked)
    {
        if (_fullGraph is null)
        {
            return;
        }

        var visibleUserNodes = new HashSet<TypeNodeViewModel>();
        foreach (var node in _fullGraph.Nodes)
        {
            if (node.IsExternal) continue;
            if (MatchesFilter(node)) visibleUserNodes.Add(node);
        }

        var visibleExternals = new HashSet<TypeNodeViewModel>();
        var visibleEdges = new List<ConnectionViewModel>();
        foreach (var edge in _fullGraph.Connections)
        {
            // Uses edges only appear while Impact Focus is active — they'd otherwise
            // overwhelm the diagram.
            if (edge.Kind == ConnectionKind.Uses && !IsImpactFocusActive) continue;

            var sourceVisible = visibleUserNodes.Contains(edge.SourceNode);
            if (!sourceVisible) continue;

            var targetVisible = edge.TargetNode.IsExternal
                ? true
                : visibleUserNodes.Contains(edge.TargetNode);
            if (!targetVisible) continue;

            visibleEdges.Add(edge);
            if (edge.TargetNode.IsExternal)
            {
                visibleExternals.Add(edge.TargetNode);
            }
        }

        var allVisible = visibleUserNodes.Concat(visibleExternals).ToList();
        // During initial sln load we already ran a full Sugiyama on _fullGraph in the
        // background — the node Locations are already correct and re-running layout
        // here just wastes time (and may deadlock/crash when Task.Run touched WPF-bound VMs).
        if (!skipLayout)
        {
            SugiyamaLayout.Apply(allVisible, visibleEdges.Where(c => c.Kind != ConnectionKind.Uses).ToList());
            var usesEdges = visibleEdges.Where(c => c.Kind == ConnectionKind.Uses).ToList();
            if (usesEdges.Count > 0)
            {
                SugiyamaLayout.RouteRectilinear(allVisible, usesEdges);
            }
        }
        else
        {
            // Uses edges still need routing since Sugiyama only handles Inheritance/Interface.
            var usesEdges = visibleEdges.Where(c => c.Kind == ConnectionKind.Uses).ToList();
            if (usesEdges.Count > 0)
            {
                SugiyamaLayout.RouteRectilinear(allVisible, usesEdges);
            }
        }

        // Bulk-replace instead of Clear+N Add — one Reset event per collection instead
        // of N CollectionChanged events, which the item host (Nodify) handles far cheaper.
        // Chunked variant yields the UI thread every 40 items so Nodify's per-visual
        // creation doesn't monopolize it — quicker to input, longer to fully paint.
        var clusters = BuildNamespaceClusters(visibleUserNodes).ToList();
        if (chunked)
        {
            await Nodes.ReplaceAllChunkedAsync(allVisible, chunkSize: 40).ConfigureAwait(true);
            await Connections.ReplaceAllChunkedAsync(visibleEdges, chunkSize: 40).ConfigureAwait(true);
            await NamespaceClusters.ReplaceAllChunkedAsync(clusters, chunkSize: 40).ConfigureAwait(true);
        }
        else
        {
            Nodes.ReplaceAll(allVisible);
            Connections.ReplaceAll(visibleEdges);
            NamespaceClusters.ReplaceAll(clusters);
        }

        if (FocusedNode is not null && !Nodes.Contains(FocusedNode))
        {
            FocusedNode = null;
        }
        else
        {
            ApplyFocusDim();
        }

        var totalUser = _fullGraph.Nodes.Count(n => !n.IsExternal);
        var baseStatus = string.Format(Strings.Status_Showing, visibleUserNodes.Count, totalUser, visibleExternals.Count, visibleEdges.Count);

        var perf = PerfProbe.Summary("total", "adapter", "open_sln", "map_async", "cpp_compile", "inject_shim", "foreign_projects", "graph", "layout", "filter");
        if (perf.Length > 0) baseStatus += $"  ⏱ {perf}";

        var hitch = UiHitchMonitor.Current?.SnapshotStats();
        if (hitch is { } h && h.HitchCount > 0)
        {
            baseStatus += $"  🥶 UI hitches: {h.HitchCount} (max {h.MaxHitchMs}ms in [{h.WorstHitchContext}], last {h.LastHitchMs}ms in [{h.LastHitchContext}])";
        }

        var warnings = _adapter.StaleCppShimWarnings;
        Status = warnings.Count == 0
            ? baseStatus
            : $"{baseStatus}  ⚠ {warnings.Count} stale Cpp shim warning(s) — {warnings[0]}";
        LayoutChanged?.Invoke();
    }

    private const double ClusterPadding = 10;
    private const double ClusterTopPadding = 26;

    public void RebuildClusters()
    {
        NamespaceClusters.Clear();
        var visibleUserNodes = Nodes.Where(n => !n.IsExternal);
        foreach (var cluster in BuildNamespaceClusters(visibleUserNodes))
        {
            NamespaceClusters.Add(cluster);
        }
    }

    private static IEnumerable<NamespaceClusterViewModel> BuildNamespaceClusters(
        IEnumerable<TypeNodeViewModel> nodes)
    {
        var groups = nodes
            .Where(n => !string.IsNullOrEmpty(n.Namespace.FullName))
            .GroupBy(n => n.Namespace.FullName, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var members = group.ToList();
            if (members.Count < 2) continue;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var n in members)
            {
                var l = n.Location.X;
                var t = n.Location.Y;
                var r = l + n.Size.Width;
                var b = t + n.Size.Height;
                if (l < minX) minX = l;
                if (t < minY) minY = t;
                if (r > maxX) maxX = r;
                if (b > maxY) maxY = b;
            }

            var location = new System.Windows.Point(minX - ClusterPadding, minY - ClusterTopPadding);
            var size = new System.Windows.Size(
                maxX - minX + ClusterPadding * 2,
                maxY - minY + ClusterTopPadding + ClusterPadding);
            yield return new NamespaceClusterViewModel(group.Key, location, size);
        }
    }

    private bool MatchesFilter(TypeNodeViewModel node)
    {
        // Impact Focus takes precedence: showing the reach is the whole point,
        // so we bypass Namespace/Search filters. They resume once focus is cleared.
        if (_impactSet is not null)
        {
            return _impactSet.Contains(node.Ref.FullyQualifiedName);
        }

        return MatchesFilterIgnoringImpact(node);
    }

    private bool MatchesFilterIgnoringImpact(TypeNodeViewModel node)
    {
        // Snapshot both bound strings. During RefreshNamespaceOptions the ComboBox's
        // SelectedItem transiently flips to null (Clear fires a Reset event that Selector
        // pushes back into SelectedNamespace before we get a chance to re-assign the token),
        // which used to throw ArgumentNullException on StartsWith(null, …). Treating null
        // as "no filter" makes the reload path safe.
        var selectedNs = SelectedNamespace;
        var searchText = SearchText;

        if (!string.IsNullOrEmpty(selectedNs)
            && !string.Equals(selectedNs, AllNamespacesToken, StringComparison.Ordinal))
        {
            if (!node.Namespace.FullName.StartsWith(selectedNs, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(searchText))
        {
            if (node.Name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    public void EnableImpactFocus(IReadOnlyList<TypeRef> seeds, int hops)
    {
        _impactSeeds = seeds.Select(s => s.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();
        ImpactFocusHops = System.Math.Max(0, hops);
        RecomputeImpactSet();
        IsImpactFocusActive = _impactSet is not null;
        ApplyFilter();
    }

    private void ExpandImpactFocus()
    {
        if (!IsImpactFocusActive) return;
        ImpactFocusHops++;
        RecomputeImpactSet();
        ApplyFilter();
    }

    private void ClearImpactFocus()
    {
        _impactSeeds = System.Array.Empty<string>();
        _impactSet = null;
        IsImpactFocusActive = false;
        ImpactFocusStatus = string.Empty;
        ApplyFilter();
    }

    private void RecomputeImpactSet()
    {
        if (_fullGraph is null || _impactSeeds.Count == 0)
        {
            _impactSet = null;
            ImpactFocusStatus = string.Empty;
            return;
        }

        var frontier = new HashSet<string>(_impactSeeds, StringComparer.Ordinal);
        var reached = new HashSet<string>(frontier, StringComparer.Ordinal);
        for (var hop = 0; hop < ImpactFocusHops; hop++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in _fullGraph.Connections)
            {
                var src = edge.SourceNode.Ref.FullyQualifiedName;
                var dst = edge.TargetNode.Ref.FullyQualifiedName;
                if (frontier.Contains(src) && !reached.Contains(dst)) next.Add(dst);
                if (frontier.Contains(dst) && !reached.Contains(src)) next.Add(src);
            }
            if (next.Count == 0) break;
            foreach (var n in next) reached.Add(n);
            frontier = next;
        }
        _impactSet = reached;

        // Diagnostic: figure out why "reached" count and visible node count often diverge.
        int userCount = 0, externalCount = 0, filteredOutCount = 0;
        var missingFromGraph = new List<string>();
        if (_fullGraph is not null)
        {
            var nodeByFqn = _fullGraph.Nodes.ToDictionary(n => n.Ref.FullyQualifiedName, StringComparer.Ordinal);
            foreach (var fqn in reached)
            {
                if (!nodeByFqn.TryGetValue(fqn, out var node))
                {
                    missingFromGraph.Add(fqn);
                    continue;
                }
                if (node.IsExternal) externalCount++;
                else if (!MatchesFilterIgnoringImpact(node)) filteredOutCount++;
                else userCount++;
            }
        }
        var sampleShort = string.Join(", ", reached.Select(f =>
        {
            int lastDot = f.LastIndexOf('.');
            return lastDot < 0 ? f : f.Substring(lastDot + 1);
        }).Take(10));
        ImpactFocusStatus = string.Format(Strings.ImpactFocus_Summary, _impactSeeds.Count, reached.Count, userCount, externalCount, filteredOutCount, missingFromGraph.Count, ImpactFocusHops, sampleShort);
    }

    public void Dispose()
    {
        _reloadDebounce?.Stop();
        StopInterpTimer();
        _watcher?.Dispose();
        _adapter.Dispose();
    }
}
