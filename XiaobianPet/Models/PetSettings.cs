namespace XiaobianPet.Models;

public sealed class PetSettings
{
    public const double MinimumPetSizeScale = 0.75;

    public const double MaximumPetSizeScale = 1.25;

    public const double DefaultPetSizeScale = 1.00;

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public bool TtsEnabled { get; set; } = true;

    public string TtsVoice { get; set; } = TtsVoiceCatalog.DefaultId;

    public bool DynamicDialogueEnabled { get; set; } = true;

    public bool TaskContextFeedbackEnabled { get; set; } = true;

    public bool ShareMemoryWithDialogue { get; set; }

    public bool MemorySharingConsentRecorded { get; set; }

    public bool AutomaticMemoryEnabled { get; set; }

    public int MemorySystemVersion { get; set; }

    public bool PreferAgentPlanAsr { get; set; } = true;

    public string? LastCwd { get; set; }

    public double UiOpacity { get; set; } = 1.0;

    public double PetSizeScale { get; set; } = DefaultPetSizeScale;
}
