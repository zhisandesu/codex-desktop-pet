namespace XiaobianPet.Models;

public static partial class PetAnimations
{
    // This family is optional until ALL clips and their calibration pass QA.
    // A partial install must never enter a chair/floor pose it cannot leave.
    public static readonly IReadOnlyList<PetMood> WorkloadMoods = Array.AsReadOnly<PetMood>(
    [
        PetMood.WorkloadConfident, PetMood.WorkloadPanting, PetMood.WorkloadWipeSweat,
        PetMood.WorkloadComputerEnter, PetMood.WorkloadCryTyping, PetMood.WorkloadWipeTears,
        PetMood.WorkloadDeskSlump, PetMood.WorkloadDeskBonk, PetMood.WorkloadComputerExit,
        PetMood.WorkloadCollapse, PetMood.WorkloadStandUp
    ]);

    public static bool IsWorkloadMood(PetMood mood) => WorkloadMoods.Contains(mood);

    public static PetMood ComputerMood(PetComputerInterlude action) => action switch
    {
        PetComputerInterlude.Typing => PetMood.WorkloadCryTyping,
        PetComputerInterlude.WipeTearsAndSniff => PetMood.WorkloadWipeTears,
        PetComputerInterlude.SlumpOnDesk => PetMood.WorkloadDeskSlump,
        PetComputerInterlude.BonkDesk => PetMood.WorkloadDeskBonk,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}
