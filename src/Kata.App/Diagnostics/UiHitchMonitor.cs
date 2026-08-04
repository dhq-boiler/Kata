using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace Kata.App.Diagnostics;

/// <summary>
/// Background probe of WPF Dispatcher responsiveness. Every <see cref="ProbeInterval"/> a
/// worker thread posts a no-op callback to the Dispatcher and measures how long it takes
/// to actually execute. A latency above <see cref="HitchThresholdMs"/> is a "hitch" — the
/// UI thread was busy that long, which the user perceives as a stall/cursor stutter.
/// </summary>
public sealed class UiHitchMonitor : IDisposable
{
    public static UiHitchMonitor? Current { get; private set; }

    public static UiHitchMonitor StartFor(Dispatcher dispatcher)
    {
        var m = new UiHitchMonitor(dispatcher);
        m.Start();
        Current = m;
        return m;
    }

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(50);
    private const int HitchThresholdMs = 80;

    private readonly Dispatcher _dispatcher;
    private readonly Thread _worker;
    private readonly CancellationTokenSource _cts = new();

    private long _hitchCount;
    private long _maxHitchMs;
    private long _totalHitchMs;
    private long _lastHitchMs;
    private long _probesSent;
    private string _lastHitchContext = string.Empty;
    private string _worstHitchContext = string.Empty;
    private readonly object _contextLock = new();

    public UiHitchMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _worker = new Thread(RunProbeLoop)
        {
            IsBackground = true,
            Name = "UiHitchMonitor",
        };
    }

    public void Start() => _worker.Start();

    public UiHitchStats SnapshotStats()
    {
        string last, worst;
        lock (_contextLock) { last = _lastHitchContext; worst = _worstHitchContext; }
        return new(
            HitchCount: Interlocked.Read(ref _hitchCount),
            MaxHitchMs: Interlocked.Read(ref _maxHitchMs),
            LastHitchMs: Interlocked.Read(ref _lastHitchMs),
            TotalHitchMs: Interlocked.Read(ref _totalHitchMs),
            ProbesSent: Interlocked.Read(ref _probesSent),
            LastHitchContext: last,
            WorstHitchContext: worst);
    }

    private void RunProbeLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                // Post an empty priority-Normal callback. When it runs, UI thread was free.
                var op = _dispatcher.InvokeAsync(() => { }, DispatcherPriority.Normal);
                op.Wait();
                sw.Stop();

                Interlocked.Increment(ref _probesSent);

                var elapsed = sw.ElapsedMilliseconds;
                if (elapsed >= HitchThresholdMs)
                {
                    Interlocked.Increment(ref _hitchCount);
                    Interlocked.Add(ref _totalHitchMs, elapsed);
                    Interlocked.Exchange(ref _lastHitchMs, elapsed);
                    var context = PerfProbe.ActivePhasesSnapshot();
                    lock (_contextLock)
                    {
                        _lastHitchContext = context;
                    }
                    // Max needs CAS loop.
                    long snapshot;
                    bool becameMax = false;
                    do
                    {
                        snapshot = Interlocked.Read(ref _maxHitchMs);
                        if (elapsed <= snapshot) break;
                        if (Interlocked.CompareExchange(ref _maxHitchMs, elapsed, snapshot) == snapshot)
                        {
                            becameMax = true;
                            break;
                        }
                    } while (true);
                    if (becameMax)
                    {
                        lock (_contextLock) _worstHitchContext = context;
                    }
                }

                Thread.Sleep(ProbeInterval);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Worker must never crash the app. Ignore and retry next tick.
                try { Thread.Sleep(ProbeInterval); } catch { return; }
            }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}

public readonly record struct UiHitchStats(
    long HitchCount,
    long MaxHitchMs,
    long LastHitchMs,
    long TotalHitchMs,
    long ProbesSent,
    string LastHitchContext,
    string WorstHitchContext);
