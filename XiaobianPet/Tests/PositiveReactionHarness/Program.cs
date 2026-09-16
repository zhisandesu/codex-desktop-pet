using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using XiaobianPet;
using XiaobianPet.Models;
using XiaobianPet.Services;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static MainWindow _window = null!;
    private static object? Call(string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, Private)!.Invoke(_window, args);
    private static T Get<T>(string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(_window)!;
    private static void Set(string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(_window, value);
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Tick()
    {
        Call("AnimationTimer_Tick", null, EventArgs.Empty);
        Get<DispatcherTimer>("_animationTimer").Stop();
        Pump();
    }

    private static void VerifyStandingIdleCadence(SpriteAtlas atlas)
    {
        Set("_persistentMood", PetMood.Working);
        Set("_nextAutonomousActionAtMilliseconds", long.MaxValue);
        Set("_nextFollowActionAtMilliseconds", long.MaxValue);
        Set("_nextSeatedRoutineAtMilliseconds", long.MaxValue);
        Call("ApplyMoodCore", PetMood.Working, PetVisualOwner.Persistent,
            atlas.PrepareSequence(PetMood.Working), false, false);
        Get<DispatcherTimer>("_animationTimer").Stop();

        PetMood? previous = null;
        for (var round = 0; round < 5; round++)
        {
            Set("_nextIdleGestureAtMilliseconds", Environment.TickCount64 - 1);
            // Alternate with no movement-timer poll. The animation wrap itself
            // must pick the due action, keeping the cadence under dispatcher load.
            if (round % 2 == 0) Call("MovementTimer_Tick", null, EventArgs.Empty);
            for (var i = 0; i < 47; i++) Tick();
            var gesture = Get<PetMood>("_currentMood");
            Check(PetAnimations.IsIdleGestureMood(gesture), "quiet standing loop did not change gesture");
            Check(gesture != previous, "adjacent standing gestures repeated");
            previous = gesture;
            Check(Get<long>("_nextIdleGestureAtMilliseconds") == long.MaxValue,
                "gesture should suspend the quiet-pause deadline until completion");

            // A desktop status refresh queues Persistent while the one-shot is
            // visible. This is the path that used to skip rescheduling entirely.
            _window.SetPersistentMood(PetMood.Working);
            var count = Get<PreparedAnimationSequence>("_currentSequence").Frames.Count;
            for (var i = 0; i < count - 1; i++)
            {
                Tick();
                Check(Get<PetMood>("_currentMood") == gesture, "status refresh cut a standing gesture short");
            }
            Tick();
            Check(Get<PetMood>("_currentMood") == PetMood.Working, "standing status did not restore");
            var next = Get<long>("_nextIdleGestureAtMilliseconds");
            Check(next != long.MaxValue, "BUG: queued status return left standing idle unscheduled forever");
            var remaining = next - Environment.TickCount64;
            Check(remaining > 0 && remaining <= 3000, "quiet pause must be due before the first idle wrap");
            _window.SetPersistentMood(PetMood.Working);
            Check(Get<long>("_nextIdleGestureAtMilliseconds") == next,
                "repeated status refresh pushed back the existing idle deadline");
        }
        Set("_nextIdleGestureAtMilliseconds", Environment.TickCount64 - 1);
        for (var i = 0; i < 47; i++)
        {
            var frameBefore = Get<int>("_frameIndex");
            var generationBefore = Get<long>("_playbackGeneration");
            _window.SetPersistentMood(i % 2 == 0 ? PetMood.Waiting : PetMood.Working);
            Check(Get<int>("_frameIndex") == frameBefore &&
                  Get<long>("_playbackGeneration") == generationBefore,
                "same-video task status update restarted the quiet loop");
            Tick();
        }
        Check(PetAnimations.IsIdleGestureMood(Get<PetMood>("_currentMood")),
            "rapid ambient status changes starved the next gesture at the first wrap");
        Set("_persistentMood", PetMood.Working);
        Call("ApplyMoodCore", PetMood.Working, PetVisualOwner.Persistent,
            atlas.PrepareSequence(PetMood.Working), false, false);
        Get<DispatcherTimer>("_animationTimer").Stop();
        Console.WriteLine("Standing cadence: 5 complete nonrepeating gestures survive queued status refreshes PASS");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // Dispatcher.PushFrame can run Application's pending startup callback.
        // Explicit preview mode bypasses single-instance activation and ensures
        // the harness cannot start a second live MainWindow or app-server.
        Environment.SetEnvironmentVariable("XIAOBIAN_UI_PREVIEW", "1");
        // Never Show(): no Window_Loaded, live services, settings writes, or
        // background task/dialogue connections are activated by this harness.
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        _window = new MainWindow();
        try
        {
            var atlas = new SpriteAtlas(Path.Combine(Path.GetFullPath(args[0]), "Assets", "animations"));
            Set("_spriteAtlas", atlas);
            _window.SetPersistentMood(PetMood.Idle);
            Get<DispatcherTimer>("_animationTimer").Stop();
            var size = (_window.Width, _window.Height);
            foreach (var mood in new[] { PetMood.HeadPatHappy, PetMood.TaskCelebrate, PetMood.StandingRamNibble,
                PetMood.SelfPlayPeekaboo, PetMood.SelfPlayAirplane, PetMood.SelfPlayTail, PetMood.RecycleBinPeek })
            {
                var expectedFrames = PetAnimations.Definitions[mood].FrameCount;
                var task = (Task)Call("PlayPositiveReactionAsync", mood, false)!;
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (Get<PetMood>("_currentMood") != mood && !task.IsCompleted && DateTime.UtcNow < deadline)
                {
                    Tick();
                    Thread.Sleep(2);
                }
                Check(Get<PetMood>("_currentMood") == mood, $"{mood} did not activate");
                Check(Get<PreparedAnimationSequence>("_currentSequence").Frames.Count == expectedFrames,
                    "all frames must be ready on activation");
                var duplicate = (Task)Call("PlayPositiveReactionAsync", mood, false)!;
                Check(duplicate.IsCompleted, "repeat command must not restart active reaction");
                Check((_window.Width, _window.Height) == size, "activation changed native window size");
                _window.SetPersistentMood(PetMood.Working);
                var generation = Get<long>("_playbackGeneration");
                for (var i = Get<int>("_frameIndex"); i < expectedFrames - 1; i++)
                {
                    Tick();
                    Check(Get<PetMood>("_currentMood") == mood, "status update truncated reaction");
                    Check((_window.Width, _window.Height) == size, "a frame changed host size");
                }
                Check(Get<int>("_frameIndex") == expectedFrames - 1, "final source frame was not displayed");
                if (mood == PetMood.RecycleBinPeek)
                    Check(!(bool)Call("DidCompleteRecycleBinAnimation", generation)!,
                        "recycle feedback was allowed before final frame dwell completed");
                while (!task.IsCompleted && DateTime.UtcNow < deadline) Tick();
                Check(task.IsCompletedSuccessfully, "reaction failed to finish at real boundary");
                Pump();
                Check(Get<PetMood>("_currentMood") == PetMood.Working, "latest persistent mood not restored");
                Check((_window.Width, _window.Height) == size, "completion changed native window size");
                if (mood == PetMood.RecycleBinPeek)
                    Check((bool)Call("DidCompleteRecycleBinAnimation", generation)!,
                        "recycle natural completion was not recorded");
                Console.WriteLine($"{mood}: preload, complete {expectedFrames} frames, fixed host, status protection PASS");
            }
            VerifyRecycleBinInterruptions(atlas);
            VerifyStandingIdleCadence(atlas);
            VerifyConnectionFailureCadence(atlas);
            VerifySeatedCheekSequence(atlas);
            VerifyClickFixedScale(atlas);
            VerifyReleaseWithoutLanding(atlas);
            VerifyWorkloadOwnership(atlas);
            VerifyWorkloadCompletionSignals();
            VerifyWorkloadClipBoundaries(atlas);
            VerifyWorkloadFamilyGeometryEdges();
            VerifyWorkloadProneHold(atlas);
            VerifyWorkloadFloorRecovery(atlas);
            VerifyWorkloadRealRestRoutine(atlas);
            VerifyWorkloadSemanticGrabEvidence(atlas, Path.GetFullPath(args[0]));
            VerifyWorkloadPackActivation(atlas, Path.GetFullPath(args[0]));
            foreach (var owner in new[] { PetVisualOwner.Grab, PetVisualOwner.ClickFlickFall, PetVisualOwner.CuteAngryReaction, PetVisualOwner.DragLanding, PetVisualOwner.WorkloadRoutine })
            {
                Set("_visualOwner", owner);
                Check(!(bool)Call("CanStartPositiveReaction", false)!, $"positive reaction could steal {owner}");
            }
            Console.WriteLine("Positive reaction runtime harness passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Get<DispatcherTimer>("_animationTimer").Stop();
            Set("_isClosing", true);
            app.Shutdown();
        }
    }

    private static void VerifyRecycleBinInterruptions(SpriteAtlas atlas)
    {
        Set("_recycleBinFeedbackPending", true);
        Check(!(bool)Call("CanStartPositiveReaction", false)!, "head strokes stole pending rummage");
        Set("_recycleBinFeedbackPending", false);
        Set("_taskContextConversationCount", 1);
        Check(!(bool)Call("CanPresentRecycleBinFeedback")!, "recycle stole existing conversation");
        Set("_taskContextConversationCount", 0);
        var task = (Task)Call("PlayPositiveReactionAsync", PetMood.RecycleBinPeek, false)!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Get<PetMood>("_currentMood") != PetMood.RecycleBinPeek && DateTime.UtcNow < deadline)
        { Tick(); Thread.Sleep(2); }
        Check(Get<PetMood>("_currentMood") == PetMood.RecycleBinPeek, "rummage did not start for interruption test");
        var interruptedGeneration = Get<long>("_playbackGeneration");
        Tick();
        Call("CancelTemporaryMoodAndRestoreScale", false, true);
        Call("ApplyForcedInteractionMood", PetMood.DraggedHeadAngry, PetVisualOwner.Grab,
            atlas.PrepareSequence(PetMood.DraggedHeadAngry), false);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(2); }
        Check(task.IsCompletedSuccessfully, "drag did not cancel rummage");
        Check(!(bool)Call("DidCompleteRecycleBinAnimation", interruptedGeneration)!,
            "interrupted rummage incorrectly permits feedback");
        Check(!(bool)Call("CanContinueRecycleBinFeedback", false)!, "feedback continued over grab");
        Check(Get<PetMood>("_currentMood") == PetMood.DraggedHeadAngry, "rummage cleanup stole grab");
        // End the synthetic grab owner before resetting the next test fixture.
        Set("_visualOwner", PetVisualOwner.Persistent);
        Call("ApplyMoodCore", PetMood.Working, PetVisualOwner.Persistent,
            atlas.PrepareSequence(PetMood.Working), false, false);
        Get<DispatcherTimer>("_animationTimer").Stop();
        Console.WriteLine("Recycle: conversation guard, natural completion, real grab cancels stale feedback PASS");
    }

    private static void VerifyWorkloadOwnership(SpriteAtlas atlas)
    {
        // Real decoded idle frames are only a visual fixture for owner tests;
        // this does not claim that missing workload media has passed visual QA.
        Set("_visualOwner", PetVisualOwner.Persistent);
        Call("ApplyMoodCore", PetMood.Idle, PetVisualOwner.WorkloadRoutine,
            atlas.PrepareSequence(PetMood.Idle), false, false);
        Get<DispatcherTimer>("_animationTimer").Stop();
        var before = Get<long>("_playbackGeneration");
        _window.SetPersistentMood(PetMood.Waiting);
        Check(Get<long>("_playbackGeneration") == before &&
            Get<PetMood>("_persistentMood") == PetMood.Waiting,
            "workload owner must record status without resetting current visual");
        Check(((Task)Call("PlayClickFlickFallAsync")!).IsCompleted,
            "single click must not reserve or replace the workload chain");
        Check(Get<long>("_playbackGeneration") == before,
            "click changed a desk/floor pose before grab");
        var ordinary = (Task<bool>)Call("RequestNormalVisualTransition", PetMood.Idle,
            PetVisualOwner.Temporary, CancellationToken.None, (Func<bool>)(() =>
                throw new Exception("ordinary transition stole workload")))!;
        Check(ordinary.IsCompletedSuccessfully && !ordinary.Result,
            "ordinary request must not overwrite the next workload clip");
        Call("CancelWorkloadForGrab");
        var grab = atlas.PrepareSequence(PetMood.DraggedHeadAngry);
        Check((bool)Call("ApplyForcedInteractionMood", PetMood.DraggedHeadAngry,
            PetVisualOwner.Grab, grab, false)!, "prepared real grab cannot interrupt workload");
        Pump();
        Check(Get<PetVisualOwner>("_visualOwner") == PetVisualOwner.Grab &&
            Get<PetMood>("_currentMood") == PetMood.DraggedHeadAngry,
            "old workload cleanup replaced the new grab");
        Set("_visualOwner", PetVisualOwner.Persistent);
        Call("ApplyMoodCore", PetMood.Idle, PetVisualOwner.Persistent,
            atlas.PrepareSequence(PetMood.Idle), false, false);
        Console.WriteLine("Workload owner: status stability, ordinary/click exclusion, prepared grab override PASS");
    }

    private static void VerifyWorkloadCompletionSignals()
    {
        // Exercise the real snapshot observer without displaying UI, starting
        // a routine, speaking, or connecting to the desktop task bridge.
        var policy = Get<PetWorkloadRoutinePolicy>("_workloadPolicy");
        var now = Environment.TickCount64;
        var decisionAt = now + 7000; // Past the preceding grab's grace period.
        policy.Observe(2, true, now);
        policy.TakeNextStandingAction(decisionAt);
        Set("_workloadSequences", new Dictionary<PetMood, PreparedAnimationSequence>());
        using var cts = new CancellationTokenSource();
        Set("_workloadRoutineCts", cts);
        CodexDesktopTask TaskState(string id, string state) => new(
            id, "test task", null, state, "test-host", null, 1, null,
            [], null, null, null);
        CodexDesktopMonitorSnapshot Snapshot(bool connected, params CodexDesktopTask[] tasks) =>
            new(tasks, null, 0, 0, 0, 0, connected, null, DateTimeOffset.UtcNow);
        void Observe(bool connected, params CodexDesktopTask[] tasks) =>
            Call("ObserveWorkloadDesktopCompletions", Snapshot(connected, tasks));
        void Expect(PetWorkloadAction action, string message) =>
            Check(policy.TakeNextStandingAction(decisionAt) == action, message);

        Observe(true, TaskState("success-one", "running"), TaskState("success-two", "active"));
        Observe(true, TaskState("success-one", "completed"), TaskState("success-two", "completed"));
        Expect(PetWorkloadAction.WipeSweat, "explicit observed completions need one coalesced wipe");
        Observe(true, TaskState("success-one", "completed"), TaskState("success-two", "completed"));
        Expect(PetWorkloadAction.None, "repeated completed snapshots replayed a success");
        Check(!policy.ObserveSuccessfulCompletion("desktop:success-one:observed-run-1", now) &&
              !policy.ObserveSuccessfulCompletion("desktop:success-two:observed-run-2", now),
            "observer lost a simultaneous completion behind the bubble-priority winner");

        Observe(true, TaskState("missing", "running"));
        Observe(true);
        Observe(true, TaskState("missing", "completed"));
        Expect(PetWorkloadAction.None, "missing task or old completion was invented as success");
        Observe(true, TaskState("failed", "running"));
        Observe(true, TaskState("failed", "failed"));
        Observe(true, TaskState("failed", "completed"));
        Expect(PetWorkloadAction.None, "failed task left a stale successful-completion epoch");
        Observe(true, TaskState("offline", "running"));
        Observe(false, TaskState("offline", "completed"));
        Expect(PetWorkloadAction.None, "offline payload was interpreted as completion");
        Observe(true, TaskState("offline", "completed"));
        Expect(PetWorkloadAction.WipeSweat, "explicit confirmed completion on reconnect was lost");
        Observe(true, TaskState("success-one", "running"));
        Observe(true, TaskState("success-one", "completed"));
        Expect(PetWorkloadAction.WipeSweat, "a new run of the same task was incorrectly deduplicated");
        Set("_workloadRoutineCts", null!);
        Set("_workloadSequences", null!);
        Console.WriteLine("Workload completion snapshots: simultaneous success, dedup, missing/failed/offline, rerun PASS");
    }

    private static void VerifyWorkloadClipBoundaries(SpriteAtlas atlas)
    {
        if (!atlas.UsesExternalFrames(PetMood.WorkloadCryTyping))
        {
            Console.WriteLine("Workload media boundary test SKIP: optional cry-typing asset not installed");
            return;
        }
        var expectedCounts = new Dictionary<PetMood, int>
        {
            [PetMood.WorkloadComputerEnter] = 137,
            [PetMood.WorkloadCryTyping] = 121,
            [PetMood.WorkloadWipeTears] = 125,
            [PetMood.WorkloadDeskSlump] = 125,
            [PetMood.WorkloadDeskBonk] = 125,
            [PetMood.WorkloadComputerExit] = 137
        };
        var sequences = expectedCounts.ToDictionary(pair => pair.Key,
            pair => atlas.PrepareSequence(pair.Key));
        foreach (var (mood, sequence) in sequences)
            Check(sequence.Frames.Count == expectedCounts[mood] &&
                sequence.Frames.All(frame => frame.IsFrozen),
                $"all actual {mood} video frames must be decoded and frozen before activation");
        // Isolated loop/interlude playback check. This bypasses the full-pack gate
        // only in the harness, and deliberately sends no speech/bubble.
        var packType = typeof(MainWindow).GetNestedType("WorkloadPack", BindingFlags.NonPublic)!;
        var pack = System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                QaApproved = true,
                Clips = expectedCounts.ToDictionary(pair => pair.Key.ToString(), pair => new
                {
                    FrameCount = pair.Value, ContentScale = 1.3208955223880596,
                    CenterOffsetX = 0.06077279617537313, CenterOffsetY = -0.03159988536785582
                })
            }), packType)!;
        Set("_workloadPack", pack);
        Set("_workloadSequences", sequences);
        using var cts = new CancellationTokenSource();
        Set("_workloadRoutineCts", cts);
        Set("_frameIndex", Get<PreparedAnimationSequence>("_currentSequence").Frames.Count - 1);
        Get<DispatcherTimer>("_animationTimer").Stop();
        var host = (_window.Width, _window.Height);
        var viewport = (FrameworkElement)_window.FindName("PetViewport");
        _window.Left = 200;
        _window.Top = 200;
        (double Width, double Height)? familySize = null;
        (double Left, double Top)? familyPosition = null;
        var playedFrames = 0;
        foreach (var mood in new[] { PetMood.WorkloadComputerEnter,
            PetMood.WorkloadCryTyping, PetMood.WorkloadCryTyping,
            PetMood.WorkloadWipeTears, PetMood.WorkloadCryTyping,
            PetMood.WorkloadDeskSlump, PetMood.WorkloadCryTyping,
            PetMood.WorkloadDeskBonk, PetMood.WorkloadCryTyping,
            PetMood.WorkloadComputerExit })
        {
            var sequence = sequences[mood];
            var playback = (Task)Call("PlayWorkloadClipAsync", mood,
                cts, null, null)!;
            Pump();
            Get<DispatcherTimer>("_animationTimer").Stop();
            Check(Get<PetVisualOwner>("_visualOwner") == PetVisualOwner.WorkloadRoutine &&
                Get<PetMood>("_currentMood") == mood && Get<int>("_frameIndex") == 0,
                $"next {mood} clip did not start atomically at F000");
            var size = (viewport.Width, viewport.Height);
            familySize ??= size;
            familyPosition ??= (_window.Left, _window.Top);
            Check(size == familySize.Value, "interlude changed the calibrated computer family size");
            Check((_window.Left, _window.Top) == familyPosition.Value,
                "computer clip transition accumulated or lost its one fixed family offset");
            for (var frame = 0; frame < sequence.Frames.Count; frame++)
            {
                Check(Get<int>("_frameIndex") == frame && !playback.IsCompleted,
                    $"{mood} lost a frame or ended before the real final interval");
                Check(ReferenceEquals(((System.Windows.Controls.Image)_window.FindName("PetImage")).Source,
                    sequence.Frames[frame]), $"{mood} displayed a stale or foreign source frame");
                _window.SetPersistentMood(frame % 2 == 0 ? PetMood.Waiting : PetMood.Working);
                Check((_window.Width, _window.Height) == host &&
                    (viewport.Width, viewport.Height) == size &&
                    (_window.Left, _window.Top) == familyPosition.Value,
                    "status update changed the head-matched computer viewport");
                Tick();
                playedFrames++;
            }
            for (var i = 0; !playback.IsCompleted && i < 100; i++) { Pump(); Thread.Sleep(1); }
            Check(playback.IsCompletedSuccessfully &&
                Get<int>("_frameIndex") == sequence.Frames.Count - 1 &&
                Get<PetMood>("_currentMood") == mood,
                "clip completion must hold its actual final frame, never insert Idle");
        }
        var interrupted = (Task)Call("PlayWorkloadClipAsync", PetMood.WorkloadCryTyping,
            cts, null, null)!;
        Pump();
        Call("CancelWorkloadForGrab");
        Call("RestoreWindowSizeAfterAutonomousMovement");
        Call("ApplyForcedInteractionMood", PetMood.DraggedHeadAngry,
            PetVisualOwner.Grab, atlas.PrepareSequence(PetMood.DraggedHeadAngry), false);
        var grabSize = (viewport.Width, viewport.Height);
        for (var i = 0; !interrupted.IsCompleted && i < 100; i++) { Pump(); Thread.Sleep(1); }
        Check(interrupted.IsCanceled && Get<PetVisualOwner>("_visualOwner") == PetVisualOwner.Grab &&
            (viewport.Width, viewport.Height) == grabSize,
            "canceled typing resumed an old frame or changed replacement grab geometry");
        Set("_workloadSequences", null!);
        Set("_workloadPack", null!);
        Set("_visualOwner", PetVisualOwner.Persistent);
        Call("ApplyMoodCore", PetMood.Idle, PetVisualOwner.Persistent,
            atlas.PrepareSequence(PetMood.Idle), false, false);
        Check(playedFrames == 1254, "computer family chain did not retain every source frame");
        Console.WriteLine("Workload real video: 1254 complete enter/typing/wipe-face/slump/bonk/exit frames, held endpoints, fixed family scale/offset, regrab PASS");
    }

    private static void VerifyWorkloadFamilyGeometryEdges()
    {
        Call("RestoreWindowSizeAfterAutonomousMovement");
        var original = (_window.Left, _window.Top);
        var viewport = (FrameworkElement)_window.FindName("PetViewport");
        var right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - _window.Width;
        var bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - _window.Height;
        foreach (var (left, top) in new[]
        {
            (SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop),
            (right, SystemParameters.VirtualScreenTop),
            (SystemParameters.VirtualScreenLeft, bottom), (right, bottom)
        })
        {
            _window.Left = left;
            _window.Top = top;
            (double Left, double Top, double Width, double Height)? stable = null;
            for (var i = 0; i < 12; i++)
            {
                Call("ApplyAutonomousContentScale", 1.3208955223880596,
                    13.856197527985074, -8.279169966378225);
                var actual = (_window.Left, _window.Top, viewport.Width, viewport.Height);
                stable ??= actual;
                Check(actual == stable.Value, "clamped computer-family offset accumulated across clips");
                var insetX = (_window.Width - viewport.Width) / 2;
                var insetY = (_window.Height - viewport.Height) / 2;
                Check(_window.Left + insetX >= SystemParameters.VirtualScreenLeft - .001 &&
                    _window.Top + insetY >= SystemParameters.VirtualScreenTop - .001 &&
                    _window.Left + insetX + viewport.Width <=
                        SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + .001 &&
                    _window.Top + insetY + viewport.Height <=
                        SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + .001,
                    "clamped presentation viewport escaped the virtual-screen bounds");
            }
            Call("RestoreWindowSizeAfterAutonomousMovement");
            Check(Math.Abs(_window.Left - left) < .001 && Math.Abs(_window.Top - top) < .001,
                "workload family restoration erased real position or retained its calibration offset");
        }
        _window.Left = original.Left;
        _window.Top = original.Top;
        Console.WriteLine("Workload geometry: four virtual-screen corners, 12 family changes each, exact restoration PASS");
    }

    private static void VerifyWorkloadProneHold(SpriteAtlas atlas)
    {
        if (!atlas.UsesExternalFrames(PetMood.WorkloadCollapse)) return;
        var sequence = atlas.PrepareSequence(PetMood.WorkloadCollapse);
        var packType = typeof(MainWindow).GetNestedType("WorkloadPack", BindingFlags.NonPublic)!;
        Set("_workloadPack", System.Text.Json.JsonSerializer.Deserialize(
            "{\"QaApproved\":true,\"Clips\":{\"WorkloadCollapse\":{\"FrameCount\":137,\"ContentScale\":1.3333333333333333,\"CenterOffsetX\":-0.02169997970779214,\"CenterOffsetY\":0.01586354961832061}}}", packType)!);
        Set("_workloadSequences", new Dictionary<PetMood, PreparedAnimationSequence>
            { [PetMood.WorkloadCollapse] = sequence });
        using var cts = new CancellationTokenSource();
        Set("_workloadRoutineCts", cts);
        Set("_frameIndex", Get<PreparedAnimationSequence>("_currentSequence").Frames.Count - 1);
        var play = (Task)Call("PlayWorkloadClipAsync", PetMood.WorkloadCollapse, cts, null, null)!;
        Pump();
        Get<DispatcherTimer>("_animationTimer").Stop();
        var image = (System.Windows.Controls.Image)_window.FindName("PetImage");
        var viewport = (FrameworkElement)_window.FindName("PetViewport");
        var geometry = (_window.Left, _window.Top, viewport.Width, viewport.Height);
        Check(sequence.Frames.Count == 137, "prone source count changed");
        for (var i = 0; i < 137; i++)
        {
            Check(Get<int>("_frameIndex") == i && ReferenceEquals(image.Source, sequence.Frames[i]) && !play.IsCompleted,
                "collapse lost source frames or ended early");
            _window.SetPersistentMood(PetMood.Working);
            Tick();
        }
        for (var i = 0; !play.IsCompleted && i < 100; i++) { Pump(); Thread.Sleep(1); }
        Check(play.IsCompletedSuccessfully, "collapse did not complete");
        for (var i = 0; i < 100; i++)
        {
            Tick();
            _window.SetPersistentMood(i % 2 == 0 ? PetMood.Idle : PetMood.Waiting);
            Check(ReferenceEquals(image.Source, sequence.Frames[^1]) &&
                Get<PetMood>("_currentMood") == PetMood.WorkloadCollapse &&
                (_window.Left, _window.Top, viewport.Width, viewport.Height) == geometry,
                "prone rest looped to standing or changed its fixed head geometry");
        }
        Call("CancelWorkloadForGrab");
        Call("RestoreWindowSizeAfterAutonomousMovement");
        Call("ApplyForcedInteractionMood", PetMood.DraggedHeadAngry,
            PetVisualOwner.Grab, atlas.PrepareSequence(PetMood.DraggedHeadAngry), false);
        Check(Get<PetVisualOwner>("_visualOwner") == PetVisualOwner.Grab,
            "held prone pose could not be grabbed");
        Set("_workloadSequences", null!);
        Set("_workloadPack", null!);
        Set("_visualOwner", PetVisualOwner.Persistent);
        Call("ApplyMoodCore", PetMood.Idle, PetVisualOwner.Persistent, atlas.PrepareSequence(PetMood.Idle), false, false);
        Console.WriteLine("Workload prone: full 137 frames, held actual endpoint across 100 status/timer updates, fixed head geometry, grab PASS");
    }

    private static void VerifyWorkloadFloorRecovery(SpriteAtlas atlas)
    {
        if (!atlas.UsesExternalFrames(PetMood.WorkloadStandUp))
        {
            Console.WriteLine("Workload floor recovery SKIP: approved stand-up not installed yet");
            return;
        }
        var sequences = new[] { PetMood.WorkloadCollapse, PetMood.WorkloadStandUp }
            .ToDictionary(mood => mood, atlas.PrepareSequence);
        var packType = typeof(MainWindow).GetNestedType("WorkloadPack", BindingFlags.NonPublic)!;
        Set("_workloadPack", System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                QaApproved = true,
                Clips = sequences.ToDictionary(pair => pair.Key.ToString(), pair => new
                {
                    FrameCount = pair.Value.Frames.Count, ContentScale = 4d / 3,
                    CenterOffsetX = -0.02169997970779214, CenterOffsetY = 0.01586354961832061
                })
            }), packType)!);
        Set("_workloadSequences", sequences);
        using var cts = new CancellationTokenSource();
        Set("_workloadRoutineCts", cts);
        Set("_frameIndex", Get<PreparedAnimationSequence>("_currentSequence").Frames.Count - 1);
        Get<DispatcherTimer>("_animationTimer").Stop();
        Call("RestoreWindowSizeAfterAutonomousMovement");
        var basePosition = (_window.Left, _window.Top);
        var viewport = (FrameworkElement)_window.FindName("PetViewport");
        var baseSize = (viewport.Width, viewport.Height);
        var image = (System.Windows.Controls.Image)_window.FindName("PetImage");
        (double Left, double Top, double Width, double Height)? fixedGeometry = null;
        var frameTotal = 0;
        foreach (var mood in new[] { PetMood.WorkloadCollapse, PetMood.WorkloadStandUp })
        {
            var sequence = sequences[mood];
            var play = (Task)Call("PlayWorkloadClipAsync", mood, cts, null, null)!;
            Pump();
            Get<DispatcherTimer>("_animationTimer").Stop();
            fixedGeometry ??= (_window.Left, _window.Top, viewport.Width, viewport.Height);
            Check(sequence.Frames.Count == (mood == PetMood.WorkloadCollapse ? 137 : 141),
                "floor recovery must retain both complete independent source videos");
            for (var frame = 0; frame < sequence.Frames.Count; frame++)
            {
                Check(Get<int>("_frameIndex") == frame && !play.IsCompleted &&
                    ReferenceEquals(image.Source, sequence.Frames[frame]),
                    $"{mood} skipped or replaced source frame {frame}");
                Check((_window.Left, _window.Top, viewport.Width, viewport.Height) == fixedGeometry.Value,
                    "collapse-to-stand-up changed the fixed family geometry");
                _window.SetPersistentMood(frame % 2 == 0 ? PetMood.Working : PetMood.Waiting);
                Tick();
                frameTotal++;
            }
            for (var i = 0; !play.IsCompleted && i < 100; i++) { Pump(); Thread.Sleep(1); }
            Check(play.IsCompletedSuccessfully && ReferenceEquals(image.Source, sequence.Frames[^1]),
                "floor clip did not hold its true final source frame");
            if (mood == PetMood.WorkloadCollapse)
            {
                // Frame-clock/status stress is separate from the 30-second
                // wall-clock policy. No live task monitor or speech is started.
                for (var i = 0; i < 720; i++)
                {
                    _window.SetPersistentMood(i % 2 == 0 ? PetMood.Working : PetMood.Idle);
                    Tick();
                    Check(ReferenceEquals(image.Source, sequence.Frames[^1]) &&
                        Get<PetVisualOwner>("_visualOwner") == PetVisualOwner.WorkloadRoutine,
                        "rest returned to a standing frame before the independent stand-up");
                }
            }
        }
        Check(frameTotal == 278, "floor chain was trimmed");
        Set("_workloadRoutineCts", null!);
        Set("_visualOwner", PetVisualOwner.Persistent);
        Call("ApplyMoodCore", PetMood.Waiting, PetVisualOwner.Persistent,
            atlas.PrepareSequence(PetMood.Waiting), false, false);
        Call("RestoreWindowSizeAfterAutonomousMovement");
        Check((viewport.Width, viewport.Height) == baseSize &&
            Math.Abs(_window.Left - basePosition.Left) < .001 &&
            Math.Abs(_window.Top - basePosition.Top) < .001 &&
            ReferenceEquals(image.Source, atlas.PrepareSequence(PetMood.Waiting).Frames[0]),
            "completed stand-up did not restore the latest standing visual and exact base geometry");

        using var grabCts = new CancellationTokenSource();
        Set("_workloadRoutineCts", grabCts);
        Set("_frameIndex", Get<PreparedAnimationSequence>("_currentSequence").Frames.Count - 1);
        var interrupted = (Task)Call("PlayWorkloadClipAsync", PetMood.WorkloadStandUp, grabCts, null, null)!;
        Pump();
        Get<DispatcherTimer>("_animationTimer").Stop();
        for (var i = 0; i < 62; i++) Tick();
        Call("CancelWorkloadForGrab");
        Call("RestoreWindowSizeAfterAutonomousMovement");
        Call("ApplyForcedInteractionMood", PetMood.DraggedHeadAngry,
            PetVisualOwner.Grab, atlas.PrepareSequence(PetMood.DraggedHeadAngry), false);
        var grabGeometry = (_window.Left, _window.Top, viewport.Width, viewport.Height);
        for (var i = 0; !interrupted.IsCompleted && i < 100; i++) { Pump(); Thread.Sleep(1); }
        Check(interrupted.IsCanceled && Get<PetVisualOwner>("_visualOwner") == PetVisualOwner.Grab &&
            (_window.Left, _window.Top, viewport.Width, viewport.Height) == grabGeometry,
            "canceled stand-up overwrote the new grab or resized it");
        Set("_workloadSequences", null!);
        Set("_workloadPack", null!);
        Set("_visualOwner", PetVisualOwner.Persistent);
        Call("ApplyMoodCore", PetMood.Idle, PetVisualOwner.Persistent,
            atlas.PrepareSequence(PetMood.Idle), false, false);
        Get<DispatcherTimer>("_animationTimer").Stop();
        Console.WriteLine("Workload floor recovery: complete 278 frames, 720 held-endpoint updates, fixed geometry, standing restore and mid-rise grab PASS");
    }

    private static void VerifyWorkloadPackActivation(SpriteAtlas atlas, string projectRoot)
    {
        var root = Path.Combine(projectRoot, "Assets", "animations");
        if (!File.Exists(Path.Combine(root, "workload-pack.json")))
        {
            Console.WriteLine("Workload complete-pack activation SKIP: release QA marker not created");
            return;
        }
        Set("_workloadSequences", null!);
        Set("_workloadPack", null!);
        // Prevent the final UpdateWorkloadRoutine callback from starting a live
        // reaction. The real gate still reads, validates and decodes all media.
        using var guard = new CancellationTokenSource();
        Set("_workloadRoutineCts", guard);
        var prepare = (Task)Call("PrepareWorkloadAnimationsAsync", root)!;
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!prepare.IsCompleted && DateTime.UtcNow < deadline)
        {
            var published = Get<IReadOnlyDictionary<PetMood, PreparedAnimationSequence>?>("_workloadSequences");
            Check(published is null || published.Count == 11, "full-pack gate exposed a partial family");
            Pump();
            Thread.Sleep(2);
        }
        Check(prepare.IsCompletedSuccessfully, "complete-pack preparation timed out or failed");
        var sequences = Get<IReadOnlyDictionary<PetMood, PreparedAnimationSequence>?>("_workloadSequences");
        Check(sequences is { Count: 11 }, "reviewed release pack did not activate all 11 required clips");
        var total = 0;
        foreach (var mood in PetAnimations.WorkloadMoods)
        {
            var sequence = sequences![mood];
            Check(sequence.VisualMood == mood && sequence.Frames.Count == PetAnimations.Definitions[mood].FrameCount &&
                sequence.Frames.All(frame => frame.IsFrozen) &&
                sequence.FrameDuration.TotalSeconds * sequence.Frames.Count <= 6.000001,
                $"{mood} was not a complete, correctly timed, frozen video on activation");
            Check(ReferenceEquals(sequence, atlas.PrepareSequence(mood)), "activation did not reuse the prepared source sequence");
            total += sequence.Frames.Count;
        }
        Set("_workloadRoutineCts", null!);
        Set("_workloadSequences", null!);
        Set("_workloadPack", null!);
        Console.WriteLine($"Workload real complete-pack gate: all 11 clips, {total} frozen frames, accurate counts and <=6s PASS");
    }

    private static void VerifyWorkloadRealRestRoutine(SpriteAtlas atlas)
    {
        if (!atlas.UsesExternalFrames(PetMood.WorkloadStandUp)) return;
        var sequences = new[] { PetMood.WorkloadCollapse, PetMood.WorkloadStandUp }
            .ToDictionary(mood => mood, atlas.PrepareSequence);
        var packType = typeof(MainWindow).GetNestedType("WorkloadPack", BindingFlags.NonPublic)!;
        Set("_workloadPack", System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                QaApproved = true,
                Clips = sequences.ToDictionary(pair => pair.Key.ToString(), pair => new
                {
                    FrameCount = pair.Value.Frames.Count, ContentScale = 4d / 3,
                    CenterOffsetX = -0.02169997970779214, CenterOffsetY = 0.01586354961832061
                })
            }), packType)!);
        Set("_workloadSequences", sequences);
        var previousPolicy = Get<PetWorkloadRoutinePolicy>("_workloadPolicy");
        var policy = new PetWorkloadRoutinePolicy();
        policy.Observe(6, true, 0);
        policy.Observe(5, true, 1);
        policy.Observe(5, true, 1501);
        Set("_workloadPolicy", policy);
        var cts = new CancellationTokenSource(); // The real routine owns disposal.
        Set("_workloadRoutineCts", cts);
        Set("_frameIndex", Get<PreparedAnimationSequence>("_currentSequence").Frames.Count - 1);
        var viewport = (FrameworkElement)_window.FindName("PetViewport");
        var image = (System.Windows.Controls.Image)_window.FindName("PetImage");
        Call("RestoreWindowSizeAfterAutonomousMovement");
        var original = (_window.Left, _window.Top, viewport.Width, viewport.Height);
        // Rest has no character line, so this executes the real async owner,
        // true Task.Delay and finally path without opening a bubble or using TTS.
        var run = (Task)Call("RunWorkloadRoutineAsync", cts)!;
        Pump();
        Get<DispatcherTimer>("_animationTimer").Stop();
        var collapse = sequences[PetMood.WorkloadCollapse];
        for (var i = 0; i < collapse.Frames.Count; i++)
        {
            Check(Get<PetMood>("_currentMood") == PetMood.WorkloadCollapse &&
                ReferenceEquals(image.Source, collapse.Frames[i]), "real rest routine skipped collapse source frame");
            Tick();
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var deadline = DateTime.UtcNow.AddSeconds(38);
        while (Get<PetMood>("_currentMood") == PetMood.WorkloadCollapse &&
            !run.IsCompleted && DateTime.UtcNow < deadline)
        {
            Check(ReferenceEquals(image.Source, collapse.Frames[^1]), "real 30-second hold lost prone endpoint");
            _window.SetPersistentMood(PetMood.Working);
            Pump();
            Get<DispatcherTimer>("_animationTimer").Stop();
            Thread.Sleep(10);
        }
        Check(watch.Elapsed.TotalSeconds >= 29.7 && watch.Elapsed.TotalSeconds < 38 &&
            Get<PetMood>("_currentMood") == PetMood.WorkloadStandUp,
            "real rest routine did not wait 30 seconds then use the independent stand-up");
        var stand = sequences[PetMood.WorkloadStandUp];
        for (var i = 0; i < stand.Frames.Count; i++)
        {
            Check(ReferenceEquals(image.Source, stand.Frames[i]) && !run.IsCompleted,
                "real stand-up routine skipped a frame or restored standing early");
            Tick();
        }
        for (var i = 0; !run.IsCompleted && i < 100; i++) { Pump(); Thread.Sleep(1); }
        Check(run.IsCompletedSuccessfully && Get<PetVisualOwner>("_visualOwner") == PetVisualOwner.Persistent &&
            Get<PetMood>("_currentMood") == PetMood.Working &&
            (_window.Left, _window.Top, viewport.Width, viewport.Height) == original &&
            policy.TakeNextStandingAction(Environment.TickCount64) == PetWorkloadAction.None,
            "real rest finally did not restore latest standing state, exact geometry or cleared rest policy");
        Set("_workloadPolicy", previousPolicy);
        Set("_workloadSequences", null!);
        Set("_workloadPack", null!);
        Get<DispatcherTimer>("_animationTimer").Stop();
        Console.WriteLine("Workload actual async routine: all collapse/stand frames, real 30s hold, owner/finally restoration PASS (no speech or live services)");
    }

    private static void VerifyWorkloadSemanticGrabEvidence(SpriteAtlas atlas, string projectRoot)
    {
        var jimeng = Path.Combine(projectRoot, "Assets", "animation-v8-preview", "video", "jimeng");
        var tested = 0;
        foreach (var evidenceFile in new[]
        {
            Path.Combine(jimeng, "workload-grab-qa-v1", "semantic-grab-keyframes.json"),
            Path.Combine(jimeng, "workload-collapse-v1", "semantic-grab-keyframes.json"),
            Path.Combine(projectRoot, "Assets", "animation-v9-preview", "local-h3-stand-up-720p-v2", "semantic-grab-keyframes.json")
        })
        {
            if (!File.Exists(evidenceFile)) continue;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(evidenceFile));
            var clips = new Dictionary<string, object>();
            var sequences = new Dictionary<PetMood, PreparedAnimationSequence>();
            foreach (var clip in document.RootElement.GetProperty("clips").EnumerateArray())
            {
                var mood = Enum.Parse<PetMood>(clip.GetProperty("id").GetString()!);
                if (!atlas.UsesExternalFrames(mood)) continue;
                var profile = System.Text.Json.JsonSerializer.Deserialize<PetWorkloadGrabProfile>(
                    clip.GetRawText(), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                var sequence = atlas.PrepareSequence(mood);
                profile.Validate(sequence.Frames.Count);
                sequences.Add(mood, sequence);
                clips.Add(mood.ToString(), new { FrameCount = sequence.Frames.Count,
                    ContentScale = mood is PetMood.WorkloadCollapse or PetMood.WorkloadStandUp ? 4d / 3 : 1.3208955223880596,
                    GrabRegions = profile });
            }
            var packType = typeof(MainWindow).GetNestedType("WorkloadPack", BindingFlags.NonPublic)!;
            Set("_workloadPack", System.Text.Json.JsonSerializer.Deserialize(
                System.Text.Json.JsonSerializer.Serialize(new { QaApproved = true, Clips = clips }), packType)!);
            foreach (var test in document.RootElement.GetProperty("regressionCases").EnumerateArray())
            {
                var mood = Enum.Parse<PetMood>(test.GetProperty("clip").GetString()!);
                if (!sequences.TryGetValue(mood, out var sequence)) continue;
                var frame = test.GetProperty("frame").GetInt32();
                var x = test.GetProperty("x").GetDouble();
                var y = test.GetProperty("y").GetDouble();
                Call("ApplyMoodCore", mood, PetVisualOwner.WorkloadRoutine, sequence, false, false);
                Call("ApplyAutonomousContentScale", mood is PetMood.WorkloadCollapse or PetMood.WorkloadStandUp ? 4d / 3 : 1.3208955223880596,
                    0d, 0d);
                Get<DispatcherTimer>("_animationTimer").Stop();
                Set("_frameIndex", frame);
                var image = (System.Windows.Controls.Image)_window.FindName("PetImage");
                image.Source = sequence.Frames[frame];
                // A never-shown Window is Collapsed and does not run root
                // layout. Arrange the Image directly inside the same viewport
                // rectangle; do not Show() or start loaded/live services.
                var viewport = (FrameworkElement)_window.FindName("PetViewport");
                image.Measure(new Size(viewport.Width, viewport.Height));
                image.Arrange(new Rect(0, 0, viewport.Width, viewport.Height));
                var scale = Math.Min(image.ActualWidth / 384, image.ActualHeight / 416);
                Check(scale > 0, "hidden WPF hit-test layout was not measured");
                var origin = image.TranslatePoint(new Point(0, 0), _window);
                var pointer = new Point(origin.X + (image.ActualWidth - 384 * scale) / 2 + x * scale,
                    origin.Y + (image.ActualHeight - 416 * scale) / 2 + y * scale);
                object?[] arguments = [pointer, PetGrabRegion.Body];
                var hit = (bool)typeof(MainWindow).GetMethod("TryClassifyVisibleGrabRegion", Private)!
                    .Invoke(_window, arguments)!;
                var actual = hit ? ((PetGrabRegion)arguments[1]!).ToString().ToLowerInvariant() : "none";
                Check(actual == test.GetProperty("expected").GetString(),
                    $"real {mood} F{frame} pixel ({x},{y}) hit {actual}, expected {test.GetProperty("expected").GetString()}");
                tested++;
            }
        }
        Set("_workloadPack", null!);
        Call("RestoreWindowSizeAfterAutonomousMovement");
        Set("_visualOwner", PetVisualOwner.Persistent);
        Call("ApplyMoodCore", PetMood.Idle, PetVisualOwner.Persistent, atlas.PrepareSequence(PetMood.Idle), false, false);
        Get<DispatcherTimer>("_animationTimer").Stop();
        Console.WriteLine($"Workload semantic grab: {tested} real source-frame alpha/viewport/profile regression points PASS (available reviewed media only)");
    }

    private static void VerifySeatedCheekSequence(SpriteAtlas atlas)
    {
        Set("_persistentMood", PetMood.Idle);
        Call("ApplyMoodCore", PetMood.Idle, PetVisualOwner.Persistent,
            atlas.PrepareSequence(PetMood.Idle), false, false);
        Get<DispatcherTimer>("_animationTimer").Stop();
        var viewport = (FrameworkElement)_window.FindName("PetViewport");
        var baseSize = (viewport.Width, viewport.Height);
        var hostSize = (_window.Width, _window.Height);
        var task = (Task)Call("PlaySeatedPreviewAsync", PetMood.SeatedDoubleCheekCute, CancellationToken.None)!;
        foreach (var (mood, count) in new[] {
            (PetMood.SeatedSitDown, 97), (PetMood.SeatedDoubleCheekCute, 125), (PetMood.SeatedStandUp, 97) })
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (Get<PetMood>("_currentMood") != mood && !task.IsCompleted && DateTime.UtcNow < deadline)
                { Tick(); Thread.Sleep(1); }
            Check(Get<PetMood>("_currentMood") == mood, $"seated chain skipped {mood}");
            Check(Get<PreparedAnimationSequence>("_currentSequence").Frames.Count == count,
                "seated target was not fully decoded on activation");
            var familySize = (viewport.Width, viewport.Height);
            for (var i = Get<int>("_frameIndex"); i < count - 1; i++)
            {
                _window.SetPersistentMood(PetMood.Working);
                Tick();
                Check(Get<PetMood>("_currentMood") == mood &&
                    (viewport.Width, viewport.Height) == familySize &&
                    (_window.Width, _window.Height) == hostSize,
                    "seated gesture or stand-up was cut short/rescaled by task status");
            }
            Check(Get<int>("_frameIndex") == count - 1, $"{mood} did not display its true final frame");
            Tick();
        }
        for (var i = 0; !task.IsCompleted && i < 100; i++) { Pump(); Thread.Sleep(1); }
        Check(task.IsCompletedSuccessfully && !Get<bool>("_isSeatedRoutineActive") &&
              Get<PetMood>("_currentMood") == PetMood.Working &&
              (viewport.Width, viewport.Height) == baseSize,
            "seated cute action did not complete its stand-up and restore the latest status");
        Console.WriteLine("Seated cute: complete 97 sit + 125 supported-cheek + 97 stand, fixed family scale PASS");
    }

    private static void VerifyClickFixedScale(SpriteAtlas atlas)
    {
        var viewport = (FrameworkElement)_window.FindName("PetViewport");
        var baseSize = (viewport.Width, viewport.Height);
        var hostSize = (_window.Width, _window.Height);
        var sequence = atlas.PrepareSequence(PetMood.ClickFlickFall);
        Call("ApplyMoodCore", PetMood.ClickFlickFall, PetVisualOwner.ClickFlickFall, sequence, false, true);
        Call("ApplyClickFlickFallWindowScale");
        var fixedSize = (viewport.Width, viewport.Height);
        Check(Math.Abs((fixedSize.Width - 14) / (baseSize.Width - 14) -
            (213d / 165d) * .98) < 1e-10, "click content scale did not use the last-frame head ratio");
        for (var i = 0; i < 193; i++)
        {
            Check(Get<int>("_frameIndex") == i, "click sequence lost source order");
            Check((viewport.Width, viewport.Height) == fixedSize &&
                (_window.Width, _window.Height) == hostSize, "click frame resized its viewport or host");
            if (i < 192) Tick();
        }
        Set("_visualOwner", PetVisualOwner.Persistent);
        Call("ApplyMoodCore", PetMood.Idle, PetVisualOwner.Persistent,
            atlas.PrepareSequence(PetMood.Idle), false, false);
        Call("RestoreWindowSizeAfterClickFlickFall");
        Check((viewport.Width, viewport.Height) == baseSize, "click did not restore resting geometry");
        Console.WriteLine("Click scale: all 193 source frames use the same final-head-aligned -2% viewport PASS");
    }

    private static void VerifyReleaseWithoutLanding(SpriteAtlas atlas)
    {
        var viewport = (FrameworkElement)_window.FindName("PetViewport");
        var baseSize = (viewport.Width, viewport.Height);
        foreach (var grab in PetAnimations.DragMoods)
        foreach (var angry in new[] { false, true })
        {
            Set("_persistentMood", PetMood.Idle);
            _window.Left = 400;
            _window.Top = 400;
            Call("ApplyMoodCore", grab, PetVisualOwner.Grab, atlas.PrepareSequence(grab), false, false);
            var scale = PetAnimations.DragVisualProfiles[grab].WindowScale;
            Call("SetPetViewportSize", baseSize.Width * scale, baseSize.Height * scale);
            var beforeCenter = _window.Top + _window.Height / 2;
            Task<PreparedAnimationSequence?>? prepared = angry
                ? Task.FromResult<PreparedAnimationSequence?>(atlas.PrepareSequence(PetMood.CuteAngry)) : null;
            var release = (Task)Call("PlayDragReleaseAsync", prepared)!;
            Check(release.IsCompletedSuccessfully, "prepared release must be immediate, without fall easing");
            Check(Get<PetMood>("_currentMood") == (angry ? PetMood.CuteAngry : PetMood.Idle),
                "release must go straight to angry or idle, never landing");
            var afterCenter = _window.Top + _window.Height / 2;
            Check(Math.Abs(beforeCenter - afterCenter) < 0.01, "release drifted vertically");
            Get<DispatcherTimer>("_animationTimer").Stop();
            if (angry)
            {
                for (var i = 0; i < 122; i++) Tick();
                for (var i = 0; Get<PetVisualOwner>("_visualOwner") != PetVisualOwner.Persistent && i < 100; i++)
                    { Pump(); Thread.Sleep(1); }
            }
            Check((viewport.Width, viewport.Height) == baseSize, "release must restore head-calibrated resting size");
        }
        Check(!PetAnimations.AnimationPreviewOptions.Any(option => option.Mood == PetMood.DragLanding),
            "retired landing must not appear in settings");
        Set("_taskContextBubbleProtectedUntil", Environment.TickCount64 + 7000);
        Set("_activeBubbleSemanticKey", "pet-speech:fixture");
        Check(!(bool)Call("CanRenderTaskProgress", CodexTaskProgressState.Running)!, "polls erased the contextual speech bubble");
        Check((bool)Call("CanRenderTaskProgress", CodexTaskProgressState.Waiting)! &&
              (bool)Call("CanRenderTaskProgress", CodexTaskProgressState.Failed)!, "important task signals must remain visible");
        Set("_taskContextBubbleProtectedUntil", 0L);
        Set("_activeBubbleSemanticKey", null!);
        Set("_desktopSnapshot", CodexDesktopMonitorSnapshot.Empty with { IsConnected = true });
        Set("_lastCharacterSpeechAt", -60000L);
        Set("_importantVoiceProtectionUntilMilliseconds", 0L);
        Check((bool)Call("CanPresentTaskContextFeedback", Environment.TickCount64)!, "unblocked task feedback should be available");
        using (var conversation = (IDisposable)Call("PauseTaskContextForConversation")!)
            Check(!(bool)Call("CanPresentTaskContextFeedback", Environment.TickCount64)!, "long direct chat must block task chatter even beyond voice cooldown");
        Check(Get<int>("_taskContextConversationCount") == 0, "conversation scope must release on exit");
        Set("_desktopSnapshot", CodexDesktopMonitorSnapshot.Empty);
        Console.WriteLine("Real grab release: all three profiles, angry/idle, no landing or drift, fixed scale; context bubble priority PASS");
    }

    private static void VerifyLanding(SpriteAtlas atlas)
    {
        var viewport = (FrameworkElement)_window.FindName("PetViewport");
        var baseSize = (viewport.Width, viewport.Height);
        var hostSize = (_window.Width, _window.Height);
        foreach (var angry in new[] { false, true })
        {
            Set("_persistentMood", PetMood.Idle);
            Call("ApplyMoodCore", PetMood.Dragged, PetVisualOwner.Grab,
                atlas.PrepareSequence(PetMood.Dragged), false, false);
            using var cts = new CancellationTokenSource();
            Set("_dragReleaseCts", cts);
            var task = (Task<bool>)Call("PlayPreparedDragLandingAsync",
                atlas.PrepareSequence(PetMood.DragLanding),
                angry ? atlas.PrepareSequence(PetMood.CuteAngry) : null,
                400d, 400d, cts.Token)!;
            Get<DispatcherTimer>("_animationTimer").Stop();
            var landingSize = (viewport.Width, viewport.Height);
            Check(Get<PetVisualOwner>("_visualOwner") == PetVisualOwner.DragLanding,
                "release did not start the prepared landing atomically");
            Check(landingSize != baseSize && (_window.Width, _window.Height) == hostSize,
                "landing must use calibrated content inside a fixed host");
            var click = (Task)Call("PlayClickFlickFallAsync")!;
            Check(click.IsCompleted && Get<PetVisualOwner>("_visualOwner") == PetVisualOwner.DragLanding,
                "an ordinary click interrupted landing");
            _window.SetPersistentMood(PetMood.Working);
            for (var i = 0; i < 96; i++)
            {
                Tick();
                Check(Get<PetMood>("_currentMood") == PetMood.DragLanding && !task.IsCompleted,
                    "landing was truncated before F096 finished");
                Check((viewport.Width, viewport.Height) == landingSize &&
                    (_window.Width, _window.Height) == hostSize, "landing changed scale during playback");
            }
            Check(Get<int>("_frameIndex") == 96, "landing did not expose its final frame");
            Tick();
            for (var i = 0; !task.IsCompleted && i < 100; i++) { Pump(); Thread.Sleep(1); }
            Check(task.IsCompletedSuccessfully && task.Result, "landing failed at its true boundary");
            Check(Get<PetMood>("_currentMood") == (angry ? PetMood.CuteAngry : PetMood.Working),
                "landing did not enter its correct optional follow-up");
            if (angry)
            {
                for (var i = 0; i < 121; i++) Tick();
                for (var i = 0; Get<PetVisualOwner>("_visualOwner") != PetVisualOwner.Persistent && i < 100; i++)
                    { Pump(); Thread.Sleep(1); }
            }
            Check((viewport.Width, viewport.Height) == baseSize, "landing chain did not restore resting scale");
            Set("_dragReleaseCts", null!);
        }
        Call("ApplyMoodCore", PetMood.Dragged, PetVisualOwner.Grab,
            atlas.PrepareSequence(PetMood.Dragged), false, false);
        using var interruptCts = new CancellationTokenSource();
        Set("_dragReleaseCts", interruptCts);
        var interrupted = (Task<bool>)Call("PlayPreparedDragLandingAsync",
            atlas.PrepareSequence(PetMood.DragLanding), null, 400d, 400d, interruptCts.Token)!;
        for (var i = 0; i < 10; i++) Tick();
        Call("CancelDragLandingForGrab");
        Call("ApplyMoodCore", PetMood.DraggedHeadAngry, PetVisualOwner.Grab,
            atlas.PrepareSequence(PetMood.DraggedHeadAngry), false, false);
        Call("SetPetViewportSize", hostSize.Width, hostSize.Height);
        for (var i = 0; !interrupted.IsCompleted && i < 100; i++) { Pump(); Thread.Sleep(1); }
        Check(interrupted.IsCompletedSuccessfully &&
              Get<PetVisualOwner>("_visualOwner") == PetVisualOwner.Grab &&
              Get<PetMood>("_currentMood") == PetMood.DraggedHeadAngry &&
              (viewport.Width, viewport.Height) == hostSize,
            "canceled landing finalizer changed the new grab frame or scale");
        Set("_dragReleaseCts", null!);
        Console.WriteLine("Landing: full 97 frames, fixed scale, status/click protection, optional angry, regrab cancellation PASS");
    }

    private static void VerifyConnectionFailureCadence(SpriteAtlas atlas)
    {
        Set("_persistentMood", PetMood.Idle);
        Call("ApplyMoodCore", PetMood.Idle, PetVisualOwner.Persistent,
            atlas.PrepareSequence(PetMood.Idle), false, false);
        Get<DispatcherTimer>("_animationTimer").Stop();
        Set("_nextAutonomousActionAtMilliseconds", long.MaxValue);
        Set("_nextFollowActionAtMilliseconds", long.MaxValue);
        Set("_nextSeatedRoutineAtMilliseconds", long.MaxValue);
        var offline = CodexDesktopMonitorSnapshot.Empty with { Error = "test: connection unavailable" };
        for (var round = 0; round < 3; round++)
        {
            Set("_nextIdleGestureAtMilliseconds", Environment.TickCount64 - 1);
            for (var i = 0; i < 47; i++)
            {
                var frame = Get<int>("_frameIndex");
                var generation = Get<long>("_playbackGeneration");
                if (i % 2 == 0) Call("ApplyDesktopAggregateState", offline);
                else Call("Codex_ConnectionStateChanged", null, CodexConnectionState.Error);
                Check(Get<int>("_frameIndex") == frame && Get<long>("_playbackGeneration") == generation,
                    "repeated connection error restarted the standing loop");
                Tick();
            }
            var gesture = Get<PetMood>("_currentMood");
            Check(PetAnimations.IsIdleGestureMood(gesture), "disconnected idle did not rotate on first wrap");
            Check(Get<PetMood>("_persistentMood") == PetMood.Failed, "failure semantics were hidden");
            Check(((System.Windows.Controls.TextBlock)_window.FindName("StatusText")).Text == "连接异常",
                "connection error badge must remain visible");
            var count = Get<PreparedAnimationSequence>("_currentSequence").Frames.Count;
            for (var i = 0; i < count - 1; i++)
            {
                Call("ApplyDesktopAggregateState", offline);
                Tick();
                Check(Get<PetMood>("_currentMood") == gesture, "connection retry cut a gesture short");
            }
            Tick();
            Check(Get<PetMood>("_currentMood") == PetMood.Failed, "failure semantics not restored after gesture");
            var next = Get<long>("_nextIdleGestureAtMilliseconds");
            Check(next != long.MaxValue && next - Environment.TickCount64 <= 3000,
                "disconnected gesture completion did not schedule the next opportunity");
        }
        var before = Get<long>("_playbackGeneration");
        var deadline = Get<long>("_nextIdleGestureAtMilliseconds");
        Call("ApplyDesktopAggregateState", CodexDesktopMonitorSnapshot.Empty with { IsConnected = true });
        Check(Get<PetMood>("_persistentMood") == PetMood.Idle &&
              Get<long>("_playbackGeneration") == before &&
              Get<long>("_nextIdleGestureAtMilliseconds") == deadline,
            "connection recovery restarted playback or postponed the next gesture");
        Set("_isSeatedRoutineActive", true);
        Call("ApplyDesktopAggregateState", offline);
        Check(Get<bool>("_isSeatedRoutineActive") && !Get<bool>("_seatedExitRequested"),
            "connection loss interrupted a seated visit");
        Set("_isSeatedRoutineActive", false);
        Console.WriteLine("Connection loss/recovery: 3 complete gestures, intact frame clock, visible error, seated continuity PASS");
    }
}
