using System.Collections.Concurrent;
using Kata.Core.Analysis;
using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Core.Sessions;
using Kata.Roslyn;

namespace Kata.Mcp;

public sealed class KataSession : IDisposable
{
    private readonly CSharpLanguageAdapter _adapter = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, PendingChangeSet> _pending = new();
    private SolutionModel? _model;
    private SmellIndex? _smellIndex;
    private string? _smellIndexModelPath;

    public CSharpLanguageAdapter Adapter => _adapter;
    public SolutionModel? CurrentModel => _model;

    public async Task<SolutionModel> LoadAsync(string solutionPath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _model = await _adapter.LoadSolutionAsync(solutionPath, cancellationToken).ConfigureAwait(false);
            _pending.Clear();
            _smellIndex = null;
            _smellIndexModelPath = null;
            return _model;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Lazily analyzes smells for the current model. Cache is invalidated on LoadAsync so a
    // reload gets fresh results; within one loaded solution the index is stable and reused.
    public async Task<SmellIndex> GetSmellIndexAsync(CancellationToken cancellationToken)
    {
        var model = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (_smellIndex is not null
            && string.Equals(_smellIndexModelPath, model.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return _smellIndex;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_smellIndex is not null
                && string.Equals(_smellIndexModelPath, model.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                return _smellIndex;
            }
            _smellIndex = await _adapter.DetectSmellsAsync(model, cancellationToken).ConfigureAwait(false);
            _smellIndexModelPath = model.FilePath;
            return _smellIndex;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SolutionModel> ReloadAsync(CancellationToken cancellationToken)
    {
        var slnPath = _model?.FilePath
            ?? throw new InvalidOperationException("No solution loaded to reload.");
        return await LoadAsync(slnPath, cancellationToken).ConfigureAwait(false);
    }

    // Swap in an already-computed SolutionModel (returned from
    // adapter.ApplyChangesAsync's incremental update path). No disk reload happens —
    // the Roslyn Solution inside the adapter is already the source of truth.
    public void UpdateModel(SolutionModel model)
    {
        _model = model;
        _smellIndex = null;
        _smellIndexModelPath = null;
    }

    public SolutionModel RequireLoaded()
        => _model ?? throw new InvalidOperationException("No solution loaded. Call load_solution first, or start Kata.App to publish a session handshake.");

    public async Task<SolutionModel> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_model is not null)
        {
            var handshake = SessionHandshake.TryRead();
            if (handshake is not null &&
                !string.Equals(handshake.SolutionPath, _model.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                await LoadAsync(handshake.SolutionPath, cancellationToken).ConfigureAwait(false);
            }
            return _model!;
        }

        var initial = SessionHandshake.TryRead()
            ?? throw new InvalidOperationException(
                "No solution loaded and no active session handshake found. Either call load_solution explicitly or open a solution in Kata.App.");
        return await LoadAsync(initial.SolutionPath, cancellationToken).ConfigureAwait(false);
    }

    public PendingChangeSet Register(string kind, string label, ChangeSet changeSet, RefactoringIntent? intent = null)
    {
        var pending = new PendingChangeSet(Guid.NewGuid(), DateTimeOffset.UtcNow, kind, label, changeSet, intent);
        _pending[pending.Id] = pending;
        return pending;
    }

    public IReadOnlyCollection<PendingChangeSet> ListPending() => _pending.Values.ToArray();

    public PendingChangeSet RequirePending(Guid id)
        => _pending.TryGetValue(id, out var p)
            ? p
            : throw new InvalidOperationException($"No pending change set with id {id}.");

    public bool RemovePending(Guid id) => _pending.TryRemove(id, out _);

    public void Dispose()
    {
        _adapter.Dispose();
        _gate.Dispose();
    }
}

public sealed record PendingChangeSet(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Kind,
    string Label,
    ChangeSet ChangeSet,
    RefactoringIntent? Intent = null);
