namespace XiaobianPet.Models;

public sealed record CodexDesktopMonitorSnapshot(
    IReadOnlyList<CodexDesktopTask> Tasks,
    CodexDesktopTask? PrimaryTask,
    int ActiveCount,
    int WaitingCount,
    int ReadyCount,
    int ErrorCount,
    bool IsConnected,
    string? Error,
    DateTimeOffset UpdatedAt)
{
    public static CodexDesktopMonitorSnapshot Empty { get; } = new(
        [],
        null,
        0,
        0,
        0,
        0,
        false,
        null,
        DateTimeOffset.MinValue);
}
