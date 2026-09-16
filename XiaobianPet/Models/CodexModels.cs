using System.Text.Json.Nodes;

namespace XiaobianPet.Models;

public enum CodexConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

public sealed record CodexThreadSummary(
    string Id,
    string Name,
    string Preview,
    string Cwd,
    string Status,
    long UpdatedAt,
    string? ThreadSource = null,
    bool? Ephemeral = null,
    string? ModelProvider = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? (string.IsNullOrWhiteSpace(Preview) ? "未命名任务" : Preview)
        : Name;
}

public sealed record CodexThreadMetadata(
    string Id,
    string Cwd,
    string? ThreadSource,
    bool? Ephemeral,
    string? ModelProvider,
    string? Name);

public sealed record CodexApprovalRequest(
    JsonNode RequestId,
    string Method,
    string ThreadId,
    string TurnId,
    string Title,
    string Detail,
    IReadOnlyList<string> AvailableDecisions);

public sealed record CodexTurnCompleted(string ThreadId, string TurnId, string Status, string? ErrorMessage);

public sealed record CodexAgentMessageCompleted(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Text,
    string? Phase);

public sealed record CodexThreadStartOptions(
    string? Model = null,
    string? BaseInstructions = null,
    string? DeveloperInstructions = null,
    string? ApprovalPolicy = null,
    string? Sandbox = null,
    bool? Ephemeral = null,
    bool DisableDynamicTools = false,
    bool DisableEnvironments = false,
    string? ThreadSource = null,
    bool? AllowProviderModelFallback = null,
    string? ModelProvider = null);

public sealed record CodexThreadStartResult(
    string ThreadId,
    string? Model,
    string? ModelProvider,
    bool? Ephemeral);

public sealed record CodexTurnStartOptions(
    string? Model = null,
    string? Effort = null,
    JsonNode? OutputSchema = null,
    string? TurnTrigger = null);

public sealed record CodexAccountInfo(string? Type, string? PlanType)
{
    public bool IsChatGpt => string.Equals(Type, "chatgpt", StringComparison.OrdinalIgnoreCase);

    public bool IsKnownIncludedAccessPlan => PlanType?.ToLowerInvariant() is
        "free" or
        "go" or
        "plus" or
        "pro" or
        "prolite" or
        "team" or
        "self_serve_business_prolite" or
        "business" or
        "ent26" or
        "enterprise_cbp_automation" or
        "enterprise" or
        "edu" or
        "edu_plus" or
        "edu_pro";
}

public sealed record CodexNotification(string Method, JsonObject Params);

public enum CodexTaskProgressState
{
    Idle,
    Running,
    Waiting,
    Completed,
    Failed,
    Interrupted
}

public sealed record CodexPlanStepProgress(string Step, string Status, string Icon);

public sealed record CodexActivityProgress(
    string Id,
    string Kind,
    string Summary,
    string Detail,
    string Status,
    string Icon,
    DateTimeOffset UpdatedAt)
{
    public string TimeText => UpdatedAt.ToLocalTime().ToString("HH:mm");
}

public sealed record CodexTaskProgressSnapshot(
    string? ThreadId,
    string? TurnId,
    CodexTaskProgressState State,
    bool IsVisible,
    bool IsActive,
    string StatusText,
    string CurrentStep,
    string CurrentOperation,
    int? Percent,
    string ProgressLabel,
    IReadOnlyList<CodexPlanStepProgress> PlanSteps,
    IReadOnlyList<CodexActivityProgress> RecentActivities)
{
    public static CodexTaskProgressSnapshot Empty { get; } = new(
        null,
        null,
        CodexTaskProgressState.Idle,
        false,
        false,
        "等待任务",
        "尚未开始",
        "等待你的要求",
        null,
        "—",
        [],
        []);
}
