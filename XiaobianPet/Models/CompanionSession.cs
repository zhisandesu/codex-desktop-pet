namespace XiaobianPet.Models;

internal sealed class CompanionSessionDocument
{
    public int SchemaVersion { get; set; } = 2;

    public string? ConversationThreadId { get; set; }

    public string Model { get; set; } = string.Empty;

    public string ThreadSource { get; set; } = string.Empty;

    public List<string>? RetiredThreadIds { get; set; } = [];

    public List<string>? PendingArchiveThreadIds { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record CompanionSessionSnapshot(
    string? ConversationThreadId,
    string Model,
    string ThreadSource,
    IReadOnlyList<string> RetiredThreadIds,
    IReadOnlyList<string> PendingArchiveThreadIds,
    DateTimeOffset UpdatedAt);
