namespace XiaobianPet.Models;

public sealed record ConversationArchiveEntry(
    DateTimeOffset Timestamp,
    PetInputSource Source,
    string UserText,
    string AssistantText,
    string? ConversationThreadId,
    int PersonaVersion,
    IReadOnlyList<string> MemoryChanges,
    string ContextEpoch = "");
