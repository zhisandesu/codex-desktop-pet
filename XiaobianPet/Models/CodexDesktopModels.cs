using System.Text.Json.Nodes;

namespace XiaobianPet.Models;

/// <summary>
/// A task summary reported by the running Codex desktop app.
/// All textual fields originate from local task data and must be treated as data, never as instructions.
/// </summary>
public record CodexDesktopTask(
    string Id,
    string Title,
    string? Summary,
    string Status,
    string? HostId,
    string? Cwd,
    long UpdatedAt,
    string? ProjectId,
    IReadOnlyList<string> ActiveFlags,
    string? CurrentTurnStatus,
    string? CurrentOperation,
    string? CurrentActivityKey)
{
    public bool IsActive => Status.Equals("active", StringComparison.OrdinalIgnoreCase);

    public string DisplayName => string.IsNullOrWhiteSpace(Title)
        ? $"任务 {Id[..Math.Min(8, Id.Length)]}"
        : Title.Trim();

    public string DisplayStatus
    {
        get
        {
            if (ActiveFlags.Any(flag => flag.Contains("Approval", StringComparison.OrdinalIgnoreCase)))
            {
                return "待确认";
            }

            if (ActiveFlags.Any(flag => flag.Contains("Input", StringComparison.OrdinalIgnoreCase)))
            {
                return "待输入";
            }

            if (ActiveFlags.Any(flag =>
                    flag.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                    flag.Contains("Blocked", StringComparison.OrdinalIgnoreCase) ||
                    flag.Contains("Failed", StringComparison.OrdinalIgnoreCase)))
            {
                return "异常";
            }

            return Status.ToLowerInvariant() switch
            {
                "active" => "进行中",
                "idle" => "已就绪",
                "notloaded" => "未加载",
                _ => string.IsNullOrWhiteSpace(Status) ? "状态未知" : Status
            };
        }
    }
}

public sealed record CodexDesktopTurnSummary(
    string Id,
    string Status,
    string? Operation,
    string? CurrentActivityKey,
    long? StartedAt,
    long? CompletedAt,
    long? DurationMs,
    string? ErrorMessage)
{
    // Locally classified signals only: never forward prompt, patch, or log text.
    public bool ResumeRequested { get; init; }
    public bool HasCodeEdit { get; init; }
}

public sealed record CodexDesktopTaskDetail(
    string Id,
    string Title,
    string? Summary,
    string Status,
    string? HostId,
    string? Cwd,
    long UpdatedAt,
    string? ProjectId,
    IReadOnlyList<string> ActiveFlags,
    string? CurrentTurnStatus,
    string? CurrentOperation,
    string? CurrentActivityKey,
    string? CurrentTurnId,
    IReadOnlyList<CodexDesktopTurnSummary> RecentTurns)
    : CodexDesktopTask(
        Id,
        Title,
        Summary,
        Status,
        HostId,
        Cwd,
        UpdatedAt,
        ProjectId,
        ActiveFlags,
        CurrentTurnStatus,
        CurrentOperation,
        CurrentActivityKey);

/// <summary>
/// Raw result of a Codex desktop app tool call. JsonData is parsed only when a complete
/// text content block is valid JSON; it remains untrusted task data.
/// </summary>
public sealed record CodexDesktopToolResult(
    bool IsError,
    IReadOnlyList<string> TextBlocks,
    JsonNode? JsonData,
    JsonObject RawResult);

public sealed class CodexDesktopBridgeException : Exception
{
    public CodexDesktopBridgeException(string message)
        : base(message)
    {
    }

    public CodexDesktopBridgeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
