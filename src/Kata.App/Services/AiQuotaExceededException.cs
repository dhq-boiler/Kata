namespace Kata.App.Services;

// Community 版で AI 相談の月次上限に達したときに投げる。呼び出し側は
// これを掴んで「Pro にアップグレード」導線を出す。
public sealed class AiQuotaExceededException : Exception
{
    public AiUsageSnapshot Snapshot { get; }

    public AiQuotaExceededException(AiUsageSnapshot snapshot)
        : base($"AI monthly quota exhausted: {snapshot.UsedCount}/{snapshot.Limit}, resets {snapshot.NextResetUtc:u}")
    {
        Snapshot = snapshot;
    }
}
