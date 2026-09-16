namespace XiaobianPet.Models;

public static class PetMemoryLimits
{
    public const int CurrentSchemaVersion = 2;

    public const int MaxEntries = 120;

    public const int MaxContentCharacters = 80;

    public const int MaxEvidenceCharacters = 100;
}

public enum PetMemoryCategory
{
    Preference,
    Dislike,
    Habit,
    ImportantEvent,
    Goal,
    Relationship,
    Profile,
    Other
}

public enum PetMemoryOrigin
{
    Explicit,
    Automatic
}

public sealed record PetMemoryEntry(
    string Id,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PetMemoryCategory Category = PetMemoryCategory.Other,
    int Importance = 3,
    PetMemoryOrigin Origin = PetMemoryOrigin.Explicit);

public sealed record PetAutomaticMemoryProposal(
    string Action,
    string MemoryId,
    PetMemoryCategory Category,
    string Content,
    string SourceQuote,
    double Confidence,
    int Importance);

public enum PetMemoryStoreState
{
    Ready,
    ReadOnlyUnknownSchema,
    ReadOnlyCorruptFile,
    ReadOnlyUnavailable
}

public sealed record PetMemorySnapshot(
    int SchemaVersion,
    PetMemoryStoreState State,
    IReadOnlyList<PetMemoryEntry> Entries,
    string? PreservedCorruptPath = null,
    string? Notice = null)
{
    public int Count => Entries.Count;

    public bool IsReadOnly => State != PetMemoryStoreState.Ready;
}

public enum PetMemoryMutationStatus
{
    Remembered,
    Updated,
    Duplicate,
    Removed,
    Cleared,
    AlreadyEmpty,
    NotFound,
    Ambiguous,
    InvalidContent,
    TooLong,
    SensitiveContent,
    LimitReached,
    ReadOnly,
    WriteFailed
}

public sealed record PetMemoryMutationResult(
    PetMemoryMutationStatus Status,
    PetMemoryEntry? Entry,
    IReadOnlyList<PetMemoryEntry> Matches)
{
    public bool Succeeded => Status is PetMemoryMutationStatus.Remembered
        or PetMemoryMutationStatus.Updated
        or PetMemoryMutationStatus.Duplicate
        or PetMemoryMutationStatus.Removed
        or PetMemoryMutationStatus.Cleared
        or PetMemoryMutationStatus.AlreadyEmpty;
}

public enum PetCommandKind
{
    Chat,
    Remember,
    QueryMemory,
    Forget,
    RequestClearMemory,
    ConfirmClearMemory,
    UpdatePersona,
    ViewPersona,
    RequestClearPersona,
    ConfirmClearPersona
}

public sealed record PetCommand(
    PetCommandKind Kind,
    string WakeWord,
    string Content,
    string OriginalText)
{
    public bool IsMemoryCommand => Kind is PetCommandKind.Remember
        or PetCommandKind.QueryMemory
        or PetCommandKind.Forget
        or PetCommandKind.RequestClearMemory
        or PetCommandKind.ConfirmClearMemory;

    public bool IsDestructive => Kind is PetCommandKind.Forget
        or PetCommandKind.RequestClearMemory
        or PetCommandKind.ConfirmClearMemory
        or PetCommandKind.RequestClearPersona
        or PetCommandKind.ConfirmClearPersona;
}

internal sealed class PetMemoryDocument
{
    public int SchemaVersion { get; set; } = PetMemoryLimits.CurrentSchemaVersion;

    public List<PetMemoryEntry>? Entries { get; set; } = [];
}
