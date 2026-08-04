using System.IO;
using System.Text.Json;

namespace Kata.App.Services;

// Community 版で AI 相談の使用回数を月次で追跡する。
// %LOCALAPPDATA%/Kata/ai-usage.json に置く。settings.json とは別ファイル
// にしているのは、設定エクスポート時にこれが一緒に持ち出されないため。
//
// bypass 対策は入れない (plain text)。この制限は「原価ゲート」ではなく
// 「価値ゲート」(API 費用はユーザーの subscription 持ち) なので、破られても
// Kata 側に金銭的実害はない。過剰対策より運用の単純さを優先する。
public sealed class AiUsageStore
{
    public const int MonthlyLimit = 10;

    // path 解決失敗 (AV / ディスクフル / 権限) は握りつぶす。App の static initializer で
    // 生成されるので、ここで例外を投げるとアプリが起動不能になる。null なら usage tracking は
    // 無効化 (毎回 fresh snapshot = 全 quota 使える) — 追跡失敗で AI が使えないより無難。
    private readonly string? _path;
    private readonly object _sync = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public AiUsageStore()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kata");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "ai-usage.json");
        }
        catch
        {
            _path = null;
        }
    }

    public AiUsageSnapshot Snapshot()
    {
        lock (_sync)
        {
            var (period, count) = LoadAndMigrate();
            return new AiUsageSnapshot(period, count, MonthlyLimit);
        }
    }

    public AiUsageSnapshot RecordSuccess()
    {
        lock (_sync)
        {
            var (period, count) = LoadAndMigrate();
            count += 1;
            Save(period, count);
            return new AiUsageSnapshot(period, count, MonthlyLimit);
        }
    }

    private (DateTime PeriodStartUtc, int Count) LoadAndMigrate()
    {
        var current = CurrentPeriodStartUtc();
        var loaded = TryLoad();
        if (loaded is null || loaded.Value.PeriodStartUtc != current)
        {
            // 新しい月に入ったのでリセット。ここでは write せず、次の RecordSuccess で保存する。
            return (current, 0);
        }
        return loaded.Value;
    }

    private (DateTime PeriodStartUtc, int Count)? TryLoad()
    {
        if (_path is null) return null;
        try
        {
            if (!File.Exists(_path)) return null;
            var json = File.ReadAllText(_path);
            var doc = JsonSerializer.Deserialize<AiUsageFile>(json, Options);
            if (doc is null) return null;
            return (DateTime.SpecifyKind(doc.PeriodStart, DateTimeKind.Utc), doc.Count);
        }
        catch { return null; }
    }

    private void Save(DateTime periodStartUtc, int count)
    {
        if (_path is null) return;
        try
        {
            var doc = new AiUsageFile
            {
                PeriodStart = DateTime.SpecifyKind(periodStartUtc, DateTimeKind.Utc),
                Count = count,
            };
            File.WriteAllText(_path, JsonSerializer.Serialize(doc, Options));
        }
        catch
        {
            // usage tracking の失敗で AI 呼び出しを止めるのはやりすぎ。次回起動時に読み直す。
        }
    }

    private static DateTime CurrentPeriodStartUtc()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class AiUsageFile
    {
        public DateTime PeriodStart { get; set; }
        public int Count { get; set; }
    }
}

public readonly record struct AiUsageSnapshot(DateTime PeriodStartUtc, int UsedCount, int Limit)
{
    public int Remaining => Math.Max(0, Limit - UsedCount);
    public bool IsExhausted => Remaining <= 0;
    public DateTime NextResetUtc => PeriodStartUtc.AddMonths(1);
}
