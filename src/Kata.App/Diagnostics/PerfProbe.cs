using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Kata.App.Diagnostics;

/// <summary>
/// Lightweight Stopwatch scope. `using (PerfProbe.Measure("phase") { ... }` records the
/// elapsed ms of that block into a static in-memory table that <see cref="Summary"/>
/// can format for the status bar.
/// </summary>
public static class PerfProbe
{
    private static readonly Dictionary<string, long> _lastMsByLabel = new();
    private static readonly HashSet<string> _activePhases = new();
    private static readonly object _lock = new();

    public static IDisposable Measure(string label) => new Scope(label);

    public static void PhaseStarted(string label)
    {
        lock (_lock) _activePhases.Add(label);
    }

    public static void PhaseEnded(string label)
    {
        lock (_lock) _activePhases.Remove(label);
    }

    public static string ActivePhasesSnapshot()
    {
        lock (_lock) return _activePhases.Count == 0 ? "(idle)" : string.Join(",", _activePhases);
    }

    public static void Record(string label, long ms)
    {
        lock (_lock)
        {
            _lastMsByLabel[label] = ms;
        }
    }

    public static long LastMs(string label)
    {
        lock (_lock)
        {
            return _lastMsByLabel.TryGetValue(label, out var ms) ? ms : 0;
        }
    }

    public static string Summary(params string[] labelsInOrder)
    {
        var sb = new StringBuilder();
        lock (_lock)
        {
            foreach (var label in labelsInOrder)
            {
                if (!_lastMsByLabel.TryGetValue(label, out var ms)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(label).Append('=').Append(ms).Append("ms");
            }
        }
        return sb.ToString();
    }

    public static void Clear()
    {
        lock (_lock) _lastMsByLabel.Clear();
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _label;
        private readonly Stopwatch _sw;

        public Scope(string label)
        {
            _label = label;
            _sw = Stopwatch.StartNew();
            PhaseStarted(label);
        }

        public void Dispose()
        {
            _sw.Stop();
            Record(_label, _sw.ElapsedMilliseconds);
            PhaseEnded(_label);
        }
    }
}
