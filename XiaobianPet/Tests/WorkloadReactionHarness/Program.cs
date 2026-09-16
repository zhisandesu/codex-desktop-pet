using XiaobianPet.Models;

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
}

foreach (var (count, tier) in new[]
{
    (0, PetWorkloadTier.None), (1, PetWorkloadTier.Light), (2, PetWorkloadTier.Light),
    (3, PetWorkloadTier.Light), (4, PetWorkloadTier.Medium), (5, PetWorkloadTier.Medium),
    (6, PetWorkloadTier.Overloaded), (99, PetWorkloadTier.Overloaded)
}) Check(PetWorkloadObservationPolicy.Classify(count) == tier, $"Wrong tier for {count}");

Check(PetWorkloadObservationPolicy.StartIntent(3) == PetDialogueIntent.TaskStarted,
    "Three active tasks must use confident dialogue, not the legacy overloaded threshold");
Check(PetWorkloadObservationPolicy.StartIntent(4) == PetDialogueIntent.TaskModeratelyBusy &&
    PetWorkloadObservationPolicy.StartIntent(5) == PetDialogueIntent.TaskModeratelyBusy,
    "Four and five tasks need the panting dialogue group");
Check(PetWorkloadObservationPolicy.StartIntent(6) == PetDialogueIntent.TaskOverloaded,
    "Only six or more tasks use crying/overloaded dialogue");

var policy = new PetWorkloadObservationPolicy();
Check(!policy.Observe(0, false, 0).HasReliableCount, "Offline zero must not imply idle");
Check(policy.Observe(3, true, 1).TierChanged, "Initial active workload needs one reaction");
Check(!policy.Observe(3, true, 2).TierChanged, "Polling must not repeat a reaction");
Check(!policy.Observe(4, true, 10).TierChanged, "Transient boundary crossing is deferred");
Check(!policy.Observe(3, true, 100).TierChanged, "Brief 3/4 fluctuation stays light");
Check(!policy.Observe(5, true, 200).TierChanged, "New candidate begins its own stability period");
Check(policy.Observe(4, true, 1700).TierChanged && policy.Tier == PetWorkloadTier.Medium,
    "4 and 5 share one tier and stable crossing commits");
Check(!policy.Observe(0, false, 1800).HasReliableCount && policy.Tier == PetWorkloadTier.Medium,
    "Disconnect preserves workload instead of creating a completion");
Check(!policy.Observe(5, true, 2000).TierChanged, "Same-tier reconnect does not repeat dialogue");
Check(!policy.Observe(6, true, 2100).TierChanged, "Overload entry awaits a stable count");
Check(policy.Observe(7, true, 3600).TierChanged && policy.Tier == PetWorkloadTier.Overloaded,
    "Stable 6+ count enters overload");
Check(!policy.Observe(0, true, 3700).TierChanged, "Zero is an observation, never a success event");
Check(policy.Observe(0, true, 5200).TierChanged && policy.Tier == PetWorkloadTier.None,
    "Stable zero enters no-active-tasks without inventing success");
Console.WriteLine("WORKLOAD_OBSERVATION_POLICY_OK: thresholds, debounce, reconnect, no synthetic completion");

var routine = new PetWorkloadRoutinePolicy();
routine.Observe(2, true, 0);
Check(routine.TakeNextStandingAction(0) == PetWorkloadAction.Confident, "Light entry is confident");
Check(routine.TakeNextStandingAction(1) == PetWorkloadAction.None, "No repeated confident greeting");
Check(routine.ObserveSuccessfulCompletion("success-a", 10), "New explicit success accepted");
Check(!routine.ObserveSuccessfulCompletion("success-a", 11), "Same success is deduplicated");
Check(routine.TakeNextStandingAction(12) == PetWorkloadAction.WipeSweat, "Light completion wipes sweat");
routine.Observe(6, true, 20);
routine.Observe(7, true, 1520);
Check(routine.TakeNextStandingAction(1521) == PetWorkloadAction.Computer, "6+ enters the computer family");
Check(routine.BeginComputerVisit() == PetComputerInterlude.Typing, "First visit starts typing");
routine.ObserveSuccessfulCompletion("success-b", 1530);
Check(routine.ShouldContinueComputer, "One completion with 6+ remaining must keep typing");
Check(routine.AfterCompleteTypingCycle(0, 0) == PetComputerInterlude.Typing, "Complete first typing loop");
Check(routine.AfterCompleteTypingCycle(0, 0) == PetComputerInterlude.Typing, "Complete second typing loop");
Check(routine.AfterCompleteTypingCycle(0, 0) == PetComputerInterlude.WipeTearsAndSniff,
    "Interlude only follows multiple complete typing loops");
Check(routine.AfterCompleteTypingCycle(0, 0) == PetComputerInterlude.Typing, "Return to typing after interlude");
Check(routine.AfterCompleteTypingCycle(0, 0) != PetComputerInterlude.WipeTearsAndSniff,
    "Adjacent interludes do not repeat");
routine.Observe(0, false, 1600);
Check(routine.ShouldContinueComputer, "Disconnection cannot cause collapse or fake completion");
routine.Observe(5, true, 1700);
routine.Observe(5, true, 3200);
Check(!routine.ShouldContinueComputer, "Stable count <=5 asks for a graceful computer exit");
Check(routine.TakeNextStandingAction(3201) == PetWorkloadAction.Rest, "Overload reduction leads to ground rest");
Check(routine.TakeNextStandingAction(3202) == PetWorkloadAction.None, "Rest cannot restart itself");
routine.Observe(6, true, 3300);
routine.Observe(6, true, 4800);
Check(routine.TakeNextStandingAction(4801) == PetWorkloadAction.None &&
    !routine.HasPendingStandingAction(4801),
    "A renewed high count must not replace the prone hold or independent stand-up");
Check(PetWorkloadRoutinePolicy.GroundRestDuration == TimeSpan.FromSeconds(30),
    "Actual prone endpoint must be held for the agreed long rest, not another animation loop");
routine.MarkGroundRestCompleted();
Check(routine.TakeNextStandingAction(4801) == PetWorkloadAction.Computer, "Recovered overload returns to computer");
Check(routine.BeginComputerVisit() == PetComputerInterlude.BonkDesk, "Still overloaded after recovery may bonk then type");
routine.InterruptForGrab(5000);
Check(!routine.HasPendingStandingAction(5001), "Real grab suspends workload reentry");
Check(routine.HasPendingStandingAction(11000), "Overload can resume after interaction grace");
Console.WriteLine("WORKLOAD_ROUTINE_POLICY_OK: loop interludes, true completion dedup, fatigue recovery, grab priority");

foreach (var owner in Enum.GetValues<PetVisualOwner>())
    Check(PetVisualTransitionPolicy.CanReplace(PetVisualOwner.WorkloadRoutine, owner) ==
        (owner is PetVisualOwner.WorkloadRoutine or PetVisualOwner.Grab),
        $"Workload chain ownership was stolen by {owner}");
Check(PetVisualTransitionPolicy.GetTiming(PetVisualOwner.WorkloadRoutine) ==
    PetVisualTransitionTiming.AnimationBoundary, "Workload entry must wait for real final frame");
Check(PetVisualTransitionQueuePolicy.DecideRequest(PetVisualOwner.WorkloadRoutine,
    PetVisualOwner.Temporary, false, true, false) == PetNormalTransitionRequestDecision.Reject,
    "A stale ordinary request must not overwrite the prepared next workload clip");
Check(PetVisualTransitionQueuePolicy.DecideRequest(PetVisualOwner.WorkloadRoutine,
    PetVisualOwner.WorkloadRoutine, false, true, false) == PetNormalTransitionRequestDecision.ApplyNow,
    "Next preloaded workload clip can start at the real boundary");
Console.WriteLine("WORKLOAD_TRANSITION_POLICY_OK: boundary entry, exclusive chain, actual grab override");

var grabProfile = new PetWorkloadGrabProfile
{
    Keyframes =
    [
        new() { Frame = 0, Head = [160, 100, 55, 70], LeftWing = [90, 180, 25, 30],
            ExcludeRects = [[245, 105, 320, 180]] },
        new() { Frame = 10, Head = [150, 260, 55, 70], LeftWing = [80, 300, 25, 30],
            ExcludeRects = [[245, 105, 320, 180]] }
    ]
};
grabProfile.Validate(11);
Check(grabProfile.TryClassify(0, 160, 100, out var region) && region == PetGrabRegion.Head,
    "standing head region missing");
Check(grabProfile.TryClassify(10, 150, 260, out region) && region == PetGrabRegion.Head,
    "low prone head must not become body because the scene bbox is wide");
Check(grabProfile.TryClassify(5, 155, 180, out region) && region == PetGrabRegion.Head,
    "head landmarks must interpolate at the actual source frame");
Check(grabProfile.TryClassify(5, 85, 240, out region) && region == PetGrabRegion.Wing,
    "wing landmarks must follow the pose independently of furniture");
Check(!grabProfile.TryClassify(5, 270, 150, out _), "monitor must not be classified as head or wing");
Check(grabProfile.TryClassify(5, 210, 310, out region) && region == PetGrabRegion.Body,
    "remaining opaque character pixels should remain draggable");
var invalidRejected = false;
try { grabProfile.Validate(12); } catch (ArgumentException) { invalidRejected = true; }
Check(invalidRejected, "incomplete semantic landmark coverage cannot activate");
Console.WriteLine("WORKLOAD_GRAB_PROFILE_OK: head/wing interpolation, furniture exclusion, full coverage validation");
