namespace XiaobianPet.Models;

internal sealed class ConversationContextDocument
{
    public int SchemaVersion { get; set; } = 1;

    public string Epoch { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
