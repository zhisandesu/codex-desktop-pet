namespace XiaobianPet.Models;

public enum PetMood
{
    Idle,
    IdleVariant,
    IdleHeadTilt,
    IdleHeadTiltAlt,
    IdleChinShake,
    MessageFeedback,
    ClickFlickFall,
    CuteAngry,
    Dragged,
    DraggedWingPout,
    DraggedHeadAngry,
    Walking,
    WalkingLeft,
    ClimbingUp,
    JumpingDown,
    PantingAfterEffort,
    RunFallRight,
    SeatedSitDown,
    SeatedIdle,
    SeatedHeadTilt,
    SeatedBlink,
    SeatedCoverHead,
    SeatedChinShake,
    SeatedStandUp,
    Waving,
    Jumping,
    Failed,
    Waiting,
    Working,
    Review,
    RunFallLeft,
    HeadPatHappy,
    TaskCelebrate,
    StandingRamNibble,
    DragLanding,
    SeatedDoubleCheekCute,
    WorkloadConfident,
    WorkloadPanting,
    WorkloadWipeSweat,
    WorkloadComputerEnter,
    WorkloadCryTyping,
    WorkloadWipeTears,
    WorkloadDeskSlump,
    WorkloadDeskBonk,
    WorkloadComputerExit,
    WorkloadCollapse,
    WorkloadStandUp,
    SelfPlayPeekaboo,
    SelfPlayAirplane,
    SelfPlayTail,
    RecycleBinPeek
}

public sealed record PetAnimationDefinition(int Row, int FrameCount, TimeSpan FrameDuration, bool Loop = true);

public enum PetAnimationPreviewCategory
{
    IdleAndFeedback,
    Movement,
    Drag
}

public sealed record PetAnimationPreviewOption(
    PetMood Mood,
    string DisplayName,
    PetAnimationPreviewCategory Category,
    string HelpText);

public sealed record PetDragVisualProfile(
    double AnchorX,
    double AnchorY,
    double WindowScale);

public static partial class PetAnimations
{
    public const int CellWidth = 384;
    public const int CellHeight = 416;
    public const int AtlasColumns = 8;
    public const int AtlasRows = 13;
    public const double ClimbingWindowScale = 1.18;
    // JumpingDown was measured against the idle render after WPF's fixed 7 px
    // Image margin. This is a content scale, not a raw window multiplier.
    public const double JumpingContentScale = 1.28128641;
    public const double JumpingCenterOffsetX = 0.40833284;
    public const double JumpingCenterOffsetY = 0.12674792;
    // Head-height measurements require about 1.15x at the side-facing start
    // and about 1.19x at the final front-facing stand. Their fixed midpoint,
    // 1.17x, keeps both endpoints within roughly 2% of the matching
    // Walking/Idle head size without pose-by-pose resizing.
    public const double RunFallContentScale = 1.17;
    // Keep this synchronized with PetImage.Margin in MainWindow.xaml. Window
    // geometry must compensate for the fixed inset when matching sprite sizes.
    public const double PetImageMarginPerSide = 7;
    public const double PetImageTotalMargin = PetImageMarginPerSide * 2;
    // Click F192 has a 165 px complete head versus Idle's 213 px (alpha >=24,
    // head-only regions, excluding wings/tail). Match that final pose, then
    // reduce the entire sequence another 2%, as requested. This scales the
    // internal presentation viewport; MainWindow's transparent native host stays
    // fixed while animations play and is resized only by the pet-size setting.
    public const double ClickFlickFallContentScale = (213d / 165d) * 0.98;
    // Landing retains the full 922px source canvas instead of the shared
    // 718px standing crop. One fixed content compensation matches its head.
    public const double DragLandingContentScale = 922d / 718d;
    public const double MaximumPresentationScale = 1.35;
    // The complete alpha silhouette of CuteAngry F000 already matches Idle
    // (383 px tall, only 1 px wider). The old 94/90 correction came from a
    // skin-only connected-component proxy that changed with bangs/expression
    // and visibly enlarged the whole character, so no extra scale is applied.
    public const double CuteAngryWindowScale = 1d;
    // One family-wide content scale makes SitDown F000 and StandUp F096 match
    // the idle endpoints. CalculateContentScaledWindowDimension removes the
    // fixed Image margin before applying it.
    public const double SeatedContentScale = 1.1712021321205954;
    public const int WalkingPlaybackCycleCount = 4;
    public const int ClimbingAutonomousCycleCount = 2;
    public const int JumpingDownTravelStartFrame = 38;
    public const int JumpingDownTravelEndFrameExclusive = 71;
    // Preserve the source action's timing: only the opening run moves the
    // desktop window. The fall, recovery, knee pat, and turn-to-front remain
    // fixed even if the dispatcher briefly stalls.
    public const int RunFallTravelStartFrame = 0;
    public const int RunFallTravelEndFrameExclusive = 39;
    // Frame 39 is the first non-travel pose and the visual impact boundary.
    // Runtime voice delivery keys off this exact source-frame transition.
    public const int RunFallImpactFrame = RunFallTravelEndFrameExclusive;
    public const double MinimumRunFallTravel = 80;
    public const double MinimumRunFallRightTravel = MinimumRunFallTravel;
    // The 47-frame idle loop lasts about 3.92 seconds. Every quiet-pause
    // deadline is due before its first wrap; vary the following complete
    // gesture instead of repeating the same near-static loop for 7.8 seconds.
    public const double MinimumIdleGestureDelayMilliseconds = 1000;
    public const double MaximumIdleGestureDelayMilliseconds = 3000;
    public static readonly TimeSpan ExternalAnimationFrameDuration =
        TimeSpan.FromSeconds(1d / 24d);
    public static readonly TimeSpan CalmAnimationFrameDuration =
        TimeSpan.FromSeconds(1d / 12d);
    public static readonly TimeSpan MaximumSingleAnimationDuration =
        TimeSpan.FromSeconds(6);
    public static readonly TimeSpan ClickFlickFallFrameDuration =
        TimeSpan.FromTicks(MaximumSingleAnimationDuration.Ticks / 193);
    public static readonly TimeSpan RunFallFrameDuration =
        TimeSpan.FromTicks(MaximumSingleAnimationDuration.Ticks / 193);
    public static readonly TimeSpan AnimationCompletionWatchdogGrace =
        TimeSpan.FromSeconds(2);
    public static readonly TimeSpan SeatedAnimationWatchdogGrace =
        AnimationCompletionWatchdogGrace;

    public static readonly IReadOnlyDictionary<PetMood, PetDragVisualProfile> DragVisualProfiles =
        new Dictionary<PetMood, PetDragVisualProfile>
        {
            // Each video starts with a different subject scale. Match the head,
            // rather than the tail/wing-inflated outer bounds, to the idle head.
            // Invisible support at the middle-back / waist fold.
            [PetMood.Dragged] = new(0.54, 0.37, 1.34),
            // Fixed grab at the raised wing tip.
            [PetMood.DraggedWingPout] = new(0.675, 0.045, 1.32),
            // Fixed grab on the middle of the forehead, below the horn roots.
            [PetMood.DraggedHeadAngry] = new(0.495, 0.341, 1.35)
        };

    public static readonly IReadOnlyList<PetMood> DragMoods =
        Array.AsReadOnly(
        [
            PetMood.Dragged,
            PetMood.DraggedWingPout,
            PetMood.DraggedHeadAngry
        ]);

    public static readonly IReadOnlyList<PetMood> IdleGestureMoods =
        Array.AsReadOnly(
        [
            PetMood.IdleVariant,
            PetMood.IdleHeadTilt,
            PetMood.IdleHeadTiltAlt,
            PetMood.IdleChinShake,
            PetMood.StandingRamNibble,
            PetMood.SelfPlayPeekaboo,
            PetMood.SelfPlayAirplane,
            PetMood.SelfPlayTail
        ]);

    public static readonly IReadOnlyDictionary<PetMood, double> IdleGestureWeights =
        new Dictionary<PetMood, double>
        {
            [PetMood.IdleVariant] = 0.28,
            [PetMood.IdleHeadTilt] = 0.126,
            [PetMood.IdleHeadTiltAlt] = 0.126,
            [PetMood.IdleChinShake] = 0.098,
            [PetMood.StandingRamNibble] = 0.07,
            [PetMood.SelfPlayPeekaboo] = 0.10,
            [PetMood.SelfPlayAirplane] = 0.10,
            [PetMood.SelfPlayTail] = 0.10
        };

    public static readonly IReadOnlyList<PetMood> SeatedSequenceMoods =
        Array.AsReadOnly(
        [
            PetMood.SeatedSitDown,
            PetMood.SeatedIdle,
            PetMood.SeatedHeadTilt,
            PetMood.SeatedBlink,
            PetMood.SeatedCoverHead,
            PetMood.SeatedChinShake,
            PetMood.SeatedDoubleCheekCute,
            PetMood.SeatedStandUp
        ]);

    public static readonly IReadOnlyList<PetMood> SeatedGestureMoods =
        Array.AsReadOnly(
        [
            PetMood.SeatedHeadTilt,
            PetMood.SeatedBlink,
            PetMood.SeatedCoverHead,
            PetMood.SeatedChinShake,
            PetMood.SeatedDoubleCheekCute
        ]);

    public static readonly IReadOnlyList<PetMood> AnimationWarmUpMoods =
        Array.AsReadOnly(
        [
            // Make pointer-driven reactions ready first so a grab never waits
            // behind long idle, autonomous, or seated video decoding.
            PetMood.Dragged,
            PetMood.DraggedWingPout,
            PetMood.DraggedHeadAngry,
            PetMood.ClickFlickFall,
            PetMood.CuteAngry,
            PetMood.HeadPatHappy,
            PetMood.TaskCelebrate,
            PetMood.StandingRamNibble,

            PetMood.SelfPlayPeekaboo,
            PetMood.SelfPlayAirplane,
            PetMood.SelfPlayTail,
            PetMood.RecycleBinPeek,

            // Idle is normally cached by the first visible SetMood call, but it
            // remains in the complete warm-up contract for deterministic coverage.
            PetMood.Idle,
            PetMood.IdleVariant,
            PetMood.IdleHeadTilt,
            PetMood.IdleHeadTiltAlt,
            PetMood.IdleChinShake,

            PetMood.Walking,
            PetMood.WalkingLeft,
            PetMood.ClimbingUp,
            PetMood.JumpingDown,
            PetMood.PantingAfterEffort,
            PetMood.RunFallRight,
            PetMood.RunFallLeft,

            PetMood.SeatedSitDown,
            PetMood.SeatedIdle,
            PetMood.SeatedHeadTilt,
            PetMood.SeatedBlink,
            PetMood.SeatedCoverHead,
            PetMood.SeatedChinShake,
            PetMood.SeatedDoubleCheekCute,
            PetMood.SeatedStandUp,

            PetMood.MessageFeedback
        ]);

    public static readonly IReadOnlyList<PetAnimationPreviewOption> AnimationPreviewOptions =
        Array.AsReadOnly<PetAnimationPreviewOption>(
        [
            new(PetMood.SelfPlayPeekaboo, "自己玩·藏猫猫", PetAnimationPreviewCategory.IdleAndFeedback,
                "SelfPlayPeekaboo：捂眼、露出脸再回正；完整 141 帧，5.875 秒，加入随机待机"),
            new(PetMood.SelfPlayAirplane, "自己玩·小飞机", PetAnimationPreviewCategory.IdleAndFeedback,
                "SelfPlayAirplane：张开双臂向两侧倾身，再完整站好；141 帧，5.875 秒"),
            new(PetMood.SelfPlayTail, "自己玩·逗尾巴", PetAnimationPreviewCategory.IdleAndFeedback,
                "SelfPlayTail：回头轻碰尾巴，再完整回正；141 帧，5.875 秒"),
            new(PetMood.RecycleBinPeek, "翻翻垃圾桶", PetAnimationPreviewCategory.IdleAndFeedback,
                "RecycleBinPeek：纯素材预览；真实反馈请右键看看回收站，空闲时最多每 10 分钟自动触发一次"),
            new(PetMood.StandingRamNibble, "吃内存条卖萌", PetAnimationPreviewCategory.IdleAndFeedback,
                "StandingRamNibble：站着拿出小内存条、轻咬卖萌再收回；完整接回站姿"),
            new(PetMood.SeatedDoubleCheekCute, "坐着弯腰双手托腮", PetAnimationPreviewCategory.IdleAndFeedback,
                "SeatedDoubleCheekCute：盘腿前倾，双肘撑在腿上，双手托腮卖萌，完整回到坐姿"),
            new(
                PetMood.Idle,
                "基础待机",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "Idle：播放一轮基础待机"),
            new(
                PetMood.IdleVariant,
                "眨眼",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "IdleVariant：播放一次眨眼"),
            new(
                PetMood.IdleHeadTilt,
                "歪头 ①",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "IdleHeadTilt：播放第一条完整歪头"),
            new(
                PetMood.IdleHeadTiltAlt,
                "歪头 ②",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "IdleHeadTiltAlt：播放第二条完整歪头"),
            new(
                PetMood.IdleChinShake,
                "托腮摇头",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "IdleChinShake：托住下巴左右摇头并完整回正"),
            new(
                PetMood.MessageFeedback,
                "消息反馈",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "MessageFeedback：播放一次消息回应"),
            new(
                PetMood.ClickFlickFall,
                "弹脑门摔倒",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "ClickFlickFall：纯素材预览，不代表真实独占交互；只播放弹脑门、摔倒、抱怨并完整站起的画面"),
            new(
                PetMood.CuteAngry,
                "可爱生气",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "CuteAngry：纯素材预览；站姿开始并回到站姿，运行时主要接在弹脑门和抓取之后"),
            new(
                PetMood.SeatedSitDown,
                "坐下",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "SeatedSitDown：从站姿完整坐下"),
            new(
                PetMood.HeadPatHappy,
                "摸头开心",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "HeadPatHappy：不按鼠标，在头部左右轻抚触发；闭眼轻蹭后完整回到站姿，左键按住仍为抓取"),
            new(
                PetMood.TaskCelebrate,
                "完成任务庆祝",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "TaskCelebrate：原地小握拳庆祝并回正；真实任务完成时低频触发"),
            new(
                PetMood.SeatedIdle,
                "坐姿待机",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "SeatedIdle：循环播放坐姿待机"),
            new(
                PetMood.SeatedHeadTilt,
                "坐姿歪头",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "SeatedHeadTilt：坐着完整歪头并回正"),
            new(
                PetMood.SeatedBlink,
                "坐姿眨眼",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "SeatedBlink：坐着完整眨眼一次"),
            new(
                PetMood.SeatedCoverHead,
                "坐姿捂头",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "SeatedCoverHead：坐着完整捂头并放下手"),
            new(
                PetMood.SeatedChinShake,
                "坐姿托腮摇头",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "SeatedChinShake：盘腿坐着托住下巴、缓慢左右摇头一次并完整放手回正"),
            new(
                PetMood.SeatedStandUp,
                "坐姿站起",
                PetAnimationPreviewCategory.IdleAndFeedback,
                "SeatedStandUp：从坐姿完整站起"),
            new(
                PetMood.Walking,
                "向右走",
                PetAnimationPreviewCategory.Movement,
                "Walking：原地连续播放四轮完整向右行走帧"),
            new(
                PetMood.WalkingLeft,
                "向左走",
                PetAnimationPreviewCategory.Movement,
                "WalkingLeft：原地连续播放四轮完整向左行走帧"),
            new(
                PetMood.ClimbingUp,
                "向上爬",
                PetAnimationPreviewCategory.Movement,
                "ClimbingUp：原地播放一轮攀爬帧"),
            new(
                PetMood.JumpingDown,
                "向下跳",
                PetAnimationPreviewCategory.Movement,
                "JumpingDown：原地播放一次下跳帧"),
            new(
                PetMood.PantingAfterEffort,
                "喘气恢复",
                PetAnimationPreviewCategory.Movement,
                "PantingAfterEffort：播放一次完成后喘气"),
            new(
                PetMood.RunFallRight,
                "向右奔跑摔倒",
                PetAnimationPreviewCategory.Movement,
                "RunFallRight：原地播放一次向右奔跑摔倒"),
            new(
                PetMood.RunFallLeft,
                "向左奔跑摔倒",
                PetAnimationPreviewCategory.Movement,
                "RunFallLeft：原地播放一次逐帧镜像的向左奔跑摔倒"),
            new(
                PetMood.Dragged,
                "托起身体",
                PetAnimationPreviewCategory.Drag,
                "Dragged：播放一轮从背后托起"),
            new(
                PetMood.DraggedWingPout,
                "拎住翅膀",
                PetAnimationPreviewCategory.Drag,
                "DraggedWingPout：播放一轮拎住翅膀"),
            new(
                PetMood.DraggedHeadAngry,
                "拎住头部",
                PetAnimationPreviewCategory.Drag,
                "DraggedHeadAngry：播放一轮拎住头部")
        ]);

    public static PetMood DragMoodForGrabRegion(PetGrabRegion region) =>
        region switch
        {
            PetGrabRegion.Head => PetMood.DraggedHeadAngry,
            PetGrabRegion.Wing => PetMood.DraggedWingPout,
            PetGrabRegion.Body => PetMood.Dragged,
            _ => throw new ArgumentOutOfRangeException(nameof(region), region, null)
        };

    public static bool IsIdleGestureMood(PetMood mood) =>
        mood is PetMood.IdleVariant or PetMood.IdleHeadTilt or
            PetMood.IdleHeadTiltAlt or PetMood.IdleChinShake or PetMood.StandingRamNibble or
            PetMood.SelfPlayPeekaboo or PetMood.SelfPlayAirplane or PetMood.SelfPlayTail;

    public static IReadOnlyList<PetMood> CreateIdleGestureCandidates(
        IEnumerable<PetMood> availableMoods,
        PetMood? lastMood)
    {
        ArgumentNullException.ThrowIfNull(availableMoods);
        var available = availableMoods
            .Where(IsIdleGestureMood)
            .Distinct()
            .ToArray();
        var withoutAdjacentRepeat = available
            .Where(mood => mood != lastMood)
            .ToArray();

        // Production has several gestures. If a partial install has
        // only the previously played asset, repeating it is better than making
        // the sole valid animation permanently unreachable.
        return Array.AsReadOnly(
            withoutAdjacentRepeat.Length > 0
                ? withoutAdjacentRepeat
                : available);
    }

    public static PetMood SelectWeightedIdleGesture(
        IReadOnlyList<PetMood> candidates,
        double randomSample)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            throw new ArgumentException(
                "At least one idle gesture candidate is required.",
                nameof(candidates));
        }

        if (!double.IsFinite(randomSample) || randomSample is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(randomSample));
        }

        if (candidates.Distinct().Count() != candidates.Count ||
            candidates.Any(mood => !IsIdleGestureMood(mood)))
        {
            throw new ArgumentException(
                "Candidates must be distinct idle gesture moods.",
                nameof(candidates));
        }

        var totalWeight = candidates.Sum(mood => IdleGestureWeights[mood]);
        var target = randomSample * totalWeight;
        var accumulated = 0d;
        foreach (var mood in candidates)
        {
            accumulated += IdleGestureWeights[mood];
            if (target < accumulated && accumulated - target > 1e-12)
            {
                return mood;
            }
        }

        return candidates[^1];
    }

    public static bool IsSeatedMood(PetMood mood) =>
        mood is PetMood.SeatedSitDown or PetMood.SeatedIdle or
            PetMood.SeatedHeadTilt or PetMood.SeatedBlink or
            PetMood.SeatedCoverHead or PetMood.SeatedChinShake or PetMood.SeatedDoubleCheekCute or
            PetMood.SeatedStandUp;

    public static bool IsSeatedGestureMood(PetMood mood) =>
        mood is PetMood.SeatedHeadTilt or PetMood.SeatedBlink or
            PetMood.SeatedCoverHead or PetMood.SeatedChinShake or PetMood.SeatedDoubleCheekCute;

    public static IReadOnlyList<PetMood> CreateSeatedGestureOrder(
        int firstGestureIndex,
        bool reverse)
    {
        if (firstGestureIndex < 0 || firstGestureIndex >= SeatedGestureMoods.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(firstGestureIndex));
        }

        var order = new PetMood[SeatedGestureMoods.Count];
        for (var offset = 0; offset < order.Length; offset++)
        {
            var index = reverse
                ? (firstGestureIndex - offset + order.Length) % order.Length
                : (firstGestureIndex + offset) % order.Length;
            order[offset] = SeatedGestureMoods[index];
        }

        return Array.AsReadOnly(order);
    }

    public static IReadOnlyList<IReadOnlyList<PetMood>> CreateSeatedGestureRounds(
        int roundCount,
        int firstGestureIndex,
        bool reverseFirstRound)
    {
        if (roundCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(roundCount));
        }

        if (firstGestureIndex < 0 || firstGestureIndex >= SeatedGestureMoods.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(firstGestureIndex));
        }

        var rounds = new IReadOnlyList<PetMood>[roundCount];
        PetMood? previousLastGesture = null;
        for (var roundIndex = 0; roundIndex < roundCount; roundIndex++)
        {
            var roundFirstIndex =
                (firstGestureIndex + roundIndex) % SeatedGestureMoods.Count;
            if (previousLastGesture == SeatedGestureMoods[roundFirstIndex])
            {
                roundFirstIndex = (roundFirstIndex + 1) % SeatedGestureMoods.Count;
            }

            var round = CreateSeatedGestureOrder(
                roundFirstIndex,
                reverseFirstRound ^ (roundIndex % 2 == 1));
            rounds[roundIndex] = round;
            previousLastGesture = round[^1];
        }

        return Array.AsReadOnly(rounds);
    }

    public static bool IsJumpingDownTravelFrame(int frameIndex) =>
        frameIndex >= JumpingDownTravelStartFrame &&
        frameIndex < JumpingDownTravelEndFrameExclusive;

    public static bool IsRunFallTravelFrame(int frameIndex) =>
        frameIndex >= RunFallTravelStartFrame &&
        frameIndex < RunFallTravelEndFrameExclusive;

    public static bool IsRunFallImpactFrame(int frameIndex) =>
        frameIndex == RunFallImpactFrame;

    public static bool IsRunFallRightAutonomousCandidate(
        bool forceRightWalkPreview,
        double availableRight,
        bool usesExternalFrames) =>
        IsRunFallAutonomousCandidate(
            forceRightWalkPreview,
            availableRight,
            usesExternalFrames);

    public static bool IsRunFallLeftAutonomousCandidate(
        bool forceRightWalkPreview,
        double availableLeft,
        bool usesExternalFrames) =>
        IsRunFallAutonomousCandidate(
            forceRightWalkPreview,
            availableLeft,
            usesExternalFrames);

    private static bool IsRunFallAutonomousCandidate(
        bool forceRightWalkPreview,
        double availableDistance,
        bool usesExternalFrames) =>
        !forceRightWalkPreview &&
        availableDistance >= MinimumRunFallTravel &&
        usesExternalFrames;

    public static PetMood ResolveAutonomousEntryMood(
        PetMood selectedMood,
        int walkingDirection) =>
        selectedMood switch
        {
            PetMood.Walking or PetMood.WalkingLeft =>
                walkingDirection < 0 ? PetMood.WalkingLeft : PetMood.Walking,
            PetMood.ClimbingUp or PetMood.JumpingDown or
            PetMood.RunFallRight or PetMood.RunFallLeft => selectedMood,
            _ => throw new ArgumentOutOfRangeException(nameof(selectedMood))
        };

    public static bool ShouldStopAutonomousAtLoopBoundary(
        PetMood mood,
        int completedCycleCount,
        bool stopRequested,
        bool enforceWalkingCycleLimit = true)
    {
        if (completedCycleCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedCycleCount));
        }

        return mood switch
        {
            PetMood.ClimbingUp => completedCycleCount >= ClimbingAutonomousCycleCount,
            PetMood.Walking or PetMood.WalkingLeft =>
                enforceWalkingCycleLimit
                    ? completedCycleCount >= WalkingPlaybackCycleCount
                    : stopRequested,
            _ => false
        };
    }

    public static int GetAnimationPreviewCycleCount(PetMood mood) =>
        mood is PetMood.Walking or PetMood.WalkingLeft
            ? WalkingPlaybackCycleCount
            : 1;

    public static TimeSpan CalculateIdleGestureDelay(double randomSample)
    {
        if (!double.IsFinite(randomSample) || randomSample is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(randomSample));
        }

        return TimeSpan.FromMilliseconds(
            MinimumIdleGestureDelayMilliseconds +
            (randomSample *
             (MaximumIdleGestureDelayMilliseconds - MinimumIdleGestureDelayMilliseconds)));
    }

    public static TimeSpan CalculatePreviewDuration(int frameCount, TimeSpan frameDuration)
    {
        if (frameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        if (frameDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(frameDuration));
        }

        return TimeSpan.FromTicks(checked(frameDuration.Ticks * frameCount));
    }

    public static TimeSpan CalculateSeatedAnimationWatchdog(
        int frameCount,
        TimeSpan frameDuration) =>
        CalculateAnimationWatchdog(frameCount, frameDuration);

    public static TimeSpan CalculateAnimationWatchdog(
        int frameCount,
        TimeSpan frameDuration)
    {
        var expectedDuration = CalculatePreviewDuration(frameCount, frameDuration);
        return TimeSpan.FromTicks(checked(
            expectedDuration.Ticks + AnimationCompletionWatchdogGrace.Ticks));
    }

    public static double FitMovementSpeedToDistance(
        double desiredSpeed,
        double availableDistance,
        TimeSpan duration,
        double edgePadding = 2)
    {
        if (!double.IsFinite(desiredSpeed) || desiredSpeed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desiredSpeed));
        }

        if (!double.IsFinite(availableDistance) || availableDistance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableDistance));
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        if (!double.IsFinite(edgePadding) || edgePadding < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(edgePadding));
        }

        var usableDistance = Math.Max(0, availableDistance - edgePadding);
        return Math.Min(desiredSpeed, usableDistance / duration.TotalSeconds);
    }

    public static readonly IReadOnlyDictionary<PetMood, string> ExternalFrameDirectories =
        new Dictionary<PetMood, string>
        {
            [PetMood.Idle] = "idle-video",
            [PetMood.IdleVariant] = "idle-blink-video",
            [PetMood.IdleHeadTilt] = "idle-head-tilt-video",
            [PetMood.IdleHeadTiltAlt] = "idle-head-tilt-alt-video",
            [PetMood.IdleChinShake] = "idle-chin-shake-video",
            [PetMood.MessageFeedback] = "message-feedback-video",
            [PetMood.ClickFlickFall] = "click-flick-fall",
            [PetMood.CuteAngry] = "cute-angry-video",
            [PetMood.HeadPatHappy] = "head-pat-happy-video",
            [PetMood.StandingRamNibble] = "standing-ram-nibble-video",
            [PetMood.SelfPlayPeekaboo] = "self-play-peekaboo-video",
            [PetMood.SelfPlayAirplane] = "self-play-airplane-video",
            [PetMood.SelfPlayTail] = "self-play-tail-video",
            [PetMood.RecycleBinPeek] = "recycle-bin-peek-video",
            [PetMood.DragLanding] = "drag-soft-landing-video",
            [PetMood.SeatedDoubleCheekCute] = "seated-double-cheek-cute-video",
            [PetMood.WorkloadConfident] = "workload-confident-video",
            [PetMood.WorkloadPanting] = "workload-panting-video",
            [PetMood.WorkloadWipeSweat] = "workload-wipe-sweat-video",
            [PetMood.WorkloadComputerEnter] = "workload-computer-enter-video",
            [PetMood.WorkloadCryTyping] = "workload-cry-typing-video",
            [PetMood.WorkloadWipeTears] = "workload-wipe-tears-video",
            [PetMood.WorkloadDeskSlump] = "workload-desk-slump-video",
            [PetMood.WorkloadDeskBonk] = "workload-desk-bonk-video",
            [PetMood.WorkloadComputerExit] = "workload-computer-exit-video",
            [PetMood.WorkloadCollapse] = "workload-collapse-video",
            [PetMood.WorkloadStandUp] = "workload-stand-up-video",
            [PetMood.TaskCelebrate] = "task-celebrate-video",
            [PetMood.Dragged] = "dragged",
            [PetMood.DraggedWingPout] = "dragged-wing-pout",
            [PetMood.DraggedHeadAngry] = "dragged-head-angry",
            [PetMood.Walking] = "walking-right-video",
            [PetMood.WalkingLeft] = "walking-left-video",
            [PetMood.ClimbingUp] = "climbing-up",
            [PetMood.JumpingDown] = "jumping-down",
            [PetMood.PantingAfterEffort] = "panting-after-effort",
            [PetMood.RunFallRight] = "run-fall-right",
            [PetMood.RunFallLeft] = "run-fall-left",
            [PetMood.SeatedSitDown] = "seated-sit-down-video",
            [PetMood.SeatedIdle] = "seated-idle-video",
            [PetMood.SeatedHeadTilt] = "seated-head-tilt-video",
            [PetMood.SeatedBlink] = "seated-blink-video",
            [PetMood.SeatedCoverHead] = "seated-cover-head-video",
            [PetMood.SeatedChinShake] = "seated-chin-shake-video",
            [PetMood.SeatedStandUp] = "seated-stand-up-video"
        };

    public static readonly IReadOnlyDictionary<PetMood, PetMood> VisualFallbackMoods =
        new Dictionary<PetMood, PetMood>
        {
            [PetMood.Waving] = PetMood.Idle,
            [PetMood.Jumping] = PetMood.Idle,
            [PetMood.Failed] = PetMood.Idle,
            [PetMood.Waiting] = PetMood.Idle,
            [PetMood.Working] = PetMood.Idle,
            [PetMood.Review] = PetMood.Idle
        };

    public static PetMood ResolveVisualMood(PetMood mood) =>
        VisualFallbackMoods.TryGetValue(mood, out var fallbackMood)
            ? fallbackMood
            : mood;

    public static TimeSpan GetExternalFrameDuration(PetMood mood) =>
        mood is PetMood.Idle or PetMood.IdleVariant or
            PetMood.Walking or PetMood.WalkingLeft or
            PetMood.RunFallRight or PetMood.RunFallLeft or
            PetMood.ClickFlickFall or
            PetMood.SeatedBlink
            ? Definitions[mood].FrameDuration
            : ExternalAnimationFrameDuration;

    public static double CalculateContentScaledWindowDimension(
        double restingWindowDimension,
        double contentScale)
    {
        if (!double.IsFinite(restingWindowDimension) ||
            restingWindowDimension <= PetImageTotalMargin)
        {
            throw new ArgumentOutOfRangeException(nameof(restingWindowDimension));
        }

        if (!double.IsFinite(contentScale) || contentScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentScale));
        }

        return PetImageTotalMargin +
            ((restingWindowDimension - PetImageTotalMargin) * contentScale);
    }

    public static readonly IReadOnlyDictionary<PetMood, PetAnimationDefinition> Definitions =
        new Dictionary<PetMood, PetAnimationDefinition>
        {
            [PetMood.Idle] = new(0, 47, CalmAnimationFrameDuration),
            [PetMood.IdleVariant] = new(9, 53, ExternalAnimationFrameDuration, false),
            [PetMood.IdleHeadTilt] = new(0, 97, ExternalAnimationFrameDuration, false),
            [PetMood.IdleHeadTiltAlt] = new(0, 97, ExternalAnimationFrameDuration, false),
            [PetMood.IdleChinShake] = new(0, 121, ExternalAnimationFrameDuration, false),
            [PetMood.MessageFeedback] = new(0, 97, ExternalAnimationFrameDuration, false),
            [PetMood.ClickFlickFall] = new(0, 193, ClickFlickFallFrameDuration, false),
            [PetMood.CuteAngry] = new(0, 121, ExternalAnimationFrameDuration, false),
            [PetMood.HeadPatHappy] = new(0, 121, ExternalAnimationFrameDuration, false),
            [PetMood.StandingRamNibble] = new(0, 121, ExternalAnimationFrameDuration, false),
            // Full local videos, calibrated once to the ordinary viewport.
            [PetMood.SelfPlayPeekaboo] = new(0, 141, ExternalAnimationFrameDuration, false),
            [PetMood.SelfPlayAirplane] = new(0, 141, ExternalAnimationFrameDuration, false),
            [PetMood.SelfPlayTail] = new(0, 141, ExternalAnimationFrameDuration, false),
            [PetMood.RecycleBinPeek] = new(0, 141, ExternalAnimationFrameDuration, false),
            [PetMood.DragLanding] = new(0, 97, ExternalAnimationFrameDuration, false),
            [PetMood.SeatedDoubleCheekCute] = new(0, 125, ExternalAnimationFrameDuration, false),
            [PetMood.WorkloadConfident] = new(0, 121, ExternalAnimationFrameDuration, false),
            // User-approved fallback: the existing 53-frame no-tongue pant,
            // not the rejected new tongue-generation attempts.
            [PetMood.WorkloadPanting] = new(0, 53, ExternalAnimationFrameDuration, false),
            [PetMood.WorkloadWipeSweat] = new(0, 97, ExternalAnimationFrameDuration, false),
            [PetMood.WorkloadComputerEnter] = new(0, 137, ExternalAnimationFrameDuration, false),
            [PetMood.WorkloadCryTyping] = new(0, 121, ExternalAnimationFrameDuration),
            [PetMood.WorkloadWipeTears] = new(0, 125, ExternalAnimationFrameDuration, false),
            [PetMood.WorkloadDeskSlump] = new(0, 125, ExternalAnimationFrameDuration, false),
            [PetMood.WorkloadDeskBonk] = new(0, 125, ExternalAnimationFrameDuration, false),
            [PetMood.WorkloadComputerExit] = new(0, 137, ExternalAnimationFrameDuration, false),
            [PetMood.WorkloadCollapse] = new(0, 137, ExternalAnimationFrameDuration, false),
            // Approved local H3 rise: all 141 forward frames at 24 fps (5.875s).
            [PetMood.WorkloadStandUp] = new(0, 141, ExternalAnimationFrameDuration, false),
            [PetMood.TaskCelebrate] = new(0, 121, ExternalAnimationFrameDuration, false),
            [PetMood.Dragged] = new(1, 44, TimeSpan.FromMilliseconds(130)),
            [PetMood.DraggedWingPout] = new(11, 53, TimeSpan.FromMilliseconds(130)),
            [PetMood.DraggedHeadAngry] = new(12, 45, TimeSpan.FromMilliseconds(130)),
            [PetMood.Walking] = new(2, 8, TimeSpan.FromMilliseconds(182)),
            [PetMood.WalkingLeft] = new(10, 8, TimeSpan.FromMilliseconds(182)),
            [PetMood.ClimbingUp] = new(0, 49, ExternalAnimationFrameDuration),
            [PetMood.JumpingDown] = new(0, 97, ExternalAnimationFrameDuration, false),
            [PetMood.PantingAfterEffort] = new(0, 53, ExternalAnimationFrameDuration, false),
            [PetMood.RunFallRight] = new(0, 193, RunFallFrameDuration, false),
            [PetMood.RunFallLeft] = new(0, 193, RunFallFrameDuration, false),
            [PetMood.SeatedSitDown] = new(0, 97, ExternalAnimationFrameDuration, false),
            [PetMood.SeatedIdle] = new(0, 97, ExternalAnimationFrameDuration),
            [PetMood.SeatedHeadTilt] = new(0, 97, ExternalAnimationFrameDuration, false),
            [PetMood.SeatedBlink] = new(0, 97, ExternalAnimationFrameDuration, false),
            [PetMood.SeatedCoverHead] = new(0, 97, ExternalAnimationFrameDuration, false),
            [PetMood.SeatedChinShake] = new(0, 121, ExternalAnimationFrameDuration, false),
            [PetMood.SeatedStandUp] = new(0, 97, ExternalAnimationFrameDuration, false),
            [PetMood.Waving] = new(3, 4, TimeSpan.FromMilliseconds(210), false),
            [PetMood.Jumping] = new(4, 5, TimeSpan.FromMilliseconds(150), false),
            [PetMood.Failed] = new(5, 8, TimeSpan.FromMilliseconds(250)),
            [PetMood.Waiting] = new(6, 6, TimeSpan.FromMilliseconds(280)),
            [PetMood.Working] = new(7, 6, TimeSpan.FromMilliseconds(180)),
            [PetMood.Review] = new(8, 6, TimeSpan.FromMilliseconds(260))
        };
}
