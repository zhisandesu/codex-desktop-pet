namespace XiaobianPet.Models;

public static class CharacterProfileLimits
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxDirectives = 12;
    public const int MaxDirectiveCharacters = 120;
}

public sealed record CharacterProfileSnapshot(
    int SchemaVersion,
    int Version,
    IReadOnlyList<string> OwnerDirectives,
    DateTimeOffset UpdatedAt);

public enum CharacterProfileMutationStatus
{
    Updated,
    Duplicate,
    Cleared,
    AlreadyEmpty,
    InvalidContent,
    TooLong,
    SensitiveContent,
    LimitReached,
    ReadOnly,
    WriteFailed
}

public sealed record CharacterProfileMutationResult(
    CharacterProfileMutationStatus Status,
    string? Directive = null);

internal sealed class CharacterProfileDocument
{
    public int SchemaVersion { get; set; } = CharacterProfileLimits.CurrentSchemaVersion;

    public int Version { get; set; } = 1;

    public List<string>? OwnerDirectives { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
