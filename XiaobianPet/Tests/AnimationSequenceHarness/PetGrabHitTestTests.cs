using XiaobianPet.Models;

internal static class PetGrabHitTestTests
{
    public static void Run()
    {
        // Face, hair and both horn tips all select the head-specific reaction.
        ExpectRegion(0.50, 0.25, PetGrabRegion.Head, "face center");
        ExpectRegion(0.31, 0.10, PetGrabRegion.Head, "left horn");
        ExpectRegion(0.70, 0.11, PetGrabRegion.Head, "right horn");
        ExpectRegion(0.28, 0.41, PetGrabRegion.Head, "left hair edge");
        ExpectRegion(0.71, 0.42, PetGrabRegion.Head, "right hair edge");

        // Both asymmetrical wings map to the same wing-grab animation.
        ExpectRegion(0.22, 0.62, PetGrabRegion.Wing, "left wing tip");
        ExpectRegion(0.34, 0.61, PetGrabRegion.Wing, "left wing membrane");
        ExpectRegion(0.78, 0.62, PetGrabRegion.Wing, "right wing membrane");
        ExpectRegion(0.86, 0.66, PetGrabRegion.Wing, "right wing tip");

        // Torso, skirt, feet, tail and transparent padding use the whole-body grab.
        ExpectRegion(0.50, 0.65, PetGrabRegion.Body, "torso");
        ExpectRegion(0.50, 0.88, PetGrabRegion.Body, "feet");
        ExpectRegion(0.17, 0.83, PetGrabRegion.Body, "tail");
        ExpectRegion(0.03, 0.03, PetGrabRegion.Body, "transparent padding");

        // Semantic overlap and contour boundaries are deterministic.
        ExpectRegion(0.35, 0.50, PetGrabRegion.Head, "head-over-wing priority");
        ExpectRegion(0.18, 0.68, PetGrabRegion.Wing, "wing contour boundary");

        // The adaptive classifier follows the current frame's alpha bounds.
        // Samples use the same relative anatomy after shifting/scaling the
        // visible character within the fixed 384x416 canvas.
        ExpectVisibleRegion(0.50, 0.28, 0.20, 0.08, 0.80, 0.95,
            PetGrabRegion.Head, "shifted visible head");
        ExpectVisibleRegion(0.22, 0.55, 0.15, 0.10, 0.85, 0.92,
            PetGrabRegion.Wing, "shifted left wing");
        ExpectVisibleRegion(0.79, 0.58, 0.15, 0.10, 0.85, 0.92,
            PetGrabRegion.Wing, "shifted right wing");
        ExpectVisibleRegion(0.50, 0.80, 0.20, 0.08, 0.80, 0.95,
            PetGrabRegion.Body, "shifted lower body");

        ExpectDialogue(
            PetGrabRegion.Wing,
            PetGrabPhase.Started,
            PetDialogueIntent.WingGrabStarted);
        ExpectDialogue(
            PetGrabRegion.Head,
            PetGrabPhase.Holding,
            PetDialogueIntent.HeadGrabHolding);
        ExpectDialogue(
            PetGrabRegion.Body,
            PetGrabPhase.Released,
            PetDialogueIntent.BodyGrabReleased);
        Expect(
            PetGrabDialogueContext.IsReleasedIntent(PetDialogueIntent.WingGrabReleased),
            "wing release was not classified as a distinct release voice event");
        Expect(
            !string.IsNullOrWhiteSpace(
                PetActionDialogueContext.SpeechPerformance(PetDialogueIntent.HeadFlicked)),
            "head-flick voice has no performance direction");
        Expect(
            !string.IsNullOrWhiteSpace(
                PetActionDialogueContext.SpeechPerformance(PetDialogueIntent.RunningEffort)),
            "running voice has no performance direction");
        Expect(
            !string.IsNullOrWhiteSpace(
                PetActionDialogueContext.SpeechPerformance(PetDialogueIntent.RunFallImpact)),
            "running-fall impact voice has no performance direction");
        Expect(
            !string.IsNullOrWhiteSpace(
                PetActionDialogueContext.ChooseInstantReaction(
                    PetDialogueIntent.WingGrabStarted)),
            "wing grab has no instant physical reaction");
        Expect(
            !string.IsNullOrWhiteSpace(
                PetActionDialogueContext.ChooseInstantReaction(
                    PetDialogueIntent.WingGrabReleased)),
            "wing release has no immediate anger fallback");
        Expect(
            !string.IsNullOrWhiteSpace(
                PetActionDialogueContext.ChooseInstantReaction(
                    PetDialogueIntent.HeadGrabReleased)),
            "head release has no immediate anger fallback");
        Expect(
            !string.IsNullOrWhiteSpace(
                PetActionDialogueContext.ChooseInstantReaction(
                    PetDialogueIntent.BodyGrabReleased)),
            "body release has no immediate anger fallback");
        Expect(
            !string.IsNullOrWhiteSpace(
                PetActionDialogueContext.ChooseInstantReaction(
                    PetDialogueIntent.HeadFlicked)),
            "head flick has no instant physical reaction");
        var fallImpact = PetActionDialogueContext.ChooseInstantReaction(
            PetDialogueIntent.RunFallImpact);
        Expect(
            !string.IsNullOrWhiteSpace(fallImpact) &&
            fallImpact.Contains("疼", StringComparison.Ordinal),
            "running-fall impact has no immediate pain reaction");
        Expect(
            PetActionDialogueContext.ChooseInstantReaction(
                PetDialogueIntent.HeadFlickAngry) is null,
            "the Luna follow-up anger line was accidentally replaced by a fixed reaction");
        var princessVoice = TtsVoiceCatalog.GetOrDefault(TtsVoiceCatalog.TiaoPiGongZhuId);
        Expect(
            princessVoice.Instruction?.Contains("微沙", StringComparison.Ordinal) == true &&
            princessVoice.Pitch == 0,
            "the selected princess voice did not retain its subtle husky tuning");

        ExpectThrows(
            () => PetGrabHitTest.Classify(-0.001, 0.5),
            "negative normalized x was accepted");
        ExpectThrows(
            () => PetGrabHitTest.Classify(0.5, 1.001),
            "normalized y above one was accepted");
        ExpectThrows(
            () => PetGrabHitTest.Classify(double.NaN, 0.5),
            "NaN normalized x was accepted");
        ExpectThrows(
            () => PetGrabHitTest.Classify(0.5, double.PositiveInfinity),
            "infinite normalized y was accepted");
        ExpectArgumentException(
            () => PetGrabHitTest.ClassifyVisiblePoint(
                0.5, 0.5, 0.6, 0.2, 0.6, 0.9),
            "zero-width visible bounds were accepted");
    }

    private static void ExpectDialogue(
        PetGrabRegion region,
        PetGrabPhase phase,
        PetDialogueIntent expected)
    {
        var actual = PetGrabDialogueContext.Resolve(region, phase);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Grab dialogue {region}/{phase} resolved to {actual}, expected {expected}.");
        }

        if (string.IsNullOrWhiteSpace(PetGrabDialogueContext.SpeechPerformance(actual)))
        {
            throw new InvalidOperationException(
                $"Grab dialogue {actual} has no per-scene speech performance instruction.");
        }

        if (!PetGrabDialogueContext.IsGrabIntent(actual))
        {
            throw new InvalidOperationException($"Grab dialogue {actual} was not classified as a grab intent.");
        }
    }

    private static void ExpectRegion(
        double normalizedX,
        double normalizedY,
        PetGrabRegion expected,
        string sample)
    {
        var actual = PetGrabHitTest.Classify(normalizedX, normalizedY);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Grab sample '{sample}' resolved to {actual}, expected {expected}.");
        }
    }

    private static void ExpectVisibleRegion(
        double normalizedX,
        double normalizedY,
        double visibleLeft,
        double visibleTop,
        double visibleRight,
        double visibleBottom,
        PetGrabRegion expected,
        string sample)
    {
        var actual = PetGrabHitTest.ClassifyVisiblePoint(
            normalizedX,
            normalizedY,
            visibleLeft,
            visibleTop,
            visibleRight,
            visibleBottom);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Adaptive grab sample '{sample}' resolved to {actual}, expected {expected}.");
        }
    }

    private static void ExpectThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void ExpectArgumentException(Action action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
