using System.Collections.Concurrent;

namespace Kata.Mcp;

// Kata.App enqueues a smell analysis request → AI agent (a Streamable-HTTP MCP client with
// LLM capability) polls for it → completes the task with a proposal payload → Kata.App polls
// for completion. Stateless spec-compatible: no protocol session needed, coordination happens
// entirely through tool calls against this in-process queue.
//
// This is *server-internal* state and thus not covered by the "stateless protocol core" rule:
// the spec removed protocol-level Mcp-Session-Id, but says nothing about server-side caches.
public sealed class AiTaskQueue
{
    private readonly ConcurrentDictionary<Guid, AiSmellTask> _tasks = new();

    public AiSmellTask Enqueue(
        string typeFullName,
        string? memberSignature,
        string category,
        string prompt)
    {
        var task = new AiSmellTask(
            Id: Guid.NewGuid(),
            CreatedAt: DateTimeOffset.UtcNow,
            TypeFullName: typeFullName,
            MemberSignature: memberSignature,
            Category: category,
            Prompt: prompt);
        _tasks[task.Id] = task;
        return task;
    }

    public IReadOnlyList<AiSmellTask> Pending() => _tasks.Values
        .Where(t => t.Status == AiSmellTaskStatus.Pending)
        .OrderBy(t => t.CreatedAt)
        .ToArray();

    public AiSmellTask? TryGet(Guid id) => _tasks.TryGetValue(id, out var t) ? t : null;

    public bool Complete(Guid id, string proposalJson)
    {
        if (!_tasks.TryGetValue(id, out var t)) return false;
        lock (t)
        {
            if (t.Status != AiSmellTaskStatus.Pending) return false;
            t.Status = AiSmellTaskStatus.Completed;
            t.Result = proposalJson;
            t.CompletedAt = DateTimeOffset.UtcNow;
        }
        return true;
    }

    public bool Fail(Guid id, string errorMessage)
    {
        if (!_tasks.TryGetValue(id, out var t)) return false;
        lock (t)
        {
            if (t.Status != AiSmellTaskStatus.Pending) return false;
            t.Status = AiSmellTaskStatus.Failed;
            t.Result = errorMessage;
            t.CompletedAt = DateTimeOffset.UtcNow;
        }
        return true;
    }
}

public sealed class AiSmellTask
{
    public AiSmellTask(
        Guid Id,
        DateTimeOffset CreatedAt,
        string TypeFullName,
        string? MemberSignature,
        string Category,
        string Prompt)
    {
        this.Id = Id;
        this.CreatedAt = CreatedAt;
        this.TypeFullName = TypeFullName;
        this.MemberSignature = MemberSignature;
        this.Category = Category;
        this.Prompt = Prompt;
        Status = AiSmellTaskStatus.Pending;
    }

    public Guid Id { get; }
    public DateTimeOffset CreatedAt { get; }
    public string TypeFullName { get; }
    public string? MemberSignature { get; }
    public string Category { get; }
    public string Prompt { get; }
    public AiSmellTaskStatus Status { get; set; }
    public string? Result { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public enum AiSmellTaskStatus
{
    Pending,
    Completed,
    Failed,
}
