using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using XiaobianPet.Models;
using XiaobianPet.Services;

static void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

var testRoot = Path.Combine(Path.GetTempPath(), $"XiaobianPet.MemoryV2.{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);
try
{
    var legacyMemoryPath = Path.Combine(testRoot, "legacy-memory.json");
    var legacyId = Guid.NewGuid().ToString("N");
    var now = DateTimeOffset.UtcNow;
    await File.WriteAllTextAsync(
        legacyMemoryPath,
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            entries = new[]
            {
                new { id = legacyId, content = "主人喜欢桂花糕", createdAt = now, updatedAt = now }
            }
        }),
        Encoding.UTF8);
    var migratedMemory = new PetMemoryStore(legacyMemoryPath);
    var migrated = migratedMemory.Snapshot.Entries.Single();
    Expect(migratedMemory.Snapshot.SchemaVersion == 2, "v1 memory did not migrate to v2 in memory");
    Expect(migrated.Origin == PetMemoryOrigin.Explicit && migrated.Importance == 3,
        "v1 memory migration metadata was wrong");

    var settingsPath = Path.Combine(testRoot, "settings.json");
    await File.WriteAllTextAsync(
        settingsPath,
        "{\"dynamicDialogueEnabled\":true,\"shareMemoryWithDialogue\":false,\"memorySharingConsentRecorded\":false}",
        Encoding.UTF8);
    var migratedSettings = new SettingsStore(settingsPath).Load();
    Expect(migratedSettings.MemorySystemVersion == 2,
        "long-term-memory settings schema did not migrate");
    Expect(!migratedSettings.ShareMemoryWithDialogue &&
           !migratedSettings.MemorySharingConsentRecorded &&
           !migratedSettings.AutomaticMemoryEnabled,
        "settings migration manufactured memory-sharing consent");

    var missingSettings = new SettingsStore(Path.Combine(testRoot, "missing-settings.json")).Load();
    Expect(missingSettings.MemorySystemVersion == 2 &&
           !missingSettings.ShareMemoryWithDialogue &&
           !missingSettings.MemorySharingConsentRecorded &&
           !missingSettings.AutomaticMemoryEnabled,
        "a new installation enabled long-term-memory sharing without consent");

    var damagedSettingsPath = Path.Combine(testRoot, "damaged-settings.json");
    await File.WriteAllTextAsync(damagedSettingsPath, "{not-json", Encoding.UTF8);
    var damagedSettings = new SettingsStore(damagedSettingsPath).Load();
    Expect(damagedSettings.MemorySystemVersion == 2 &&
           !damagedSettings.ShareMemoryWithDialogue &&
           !damagedSettings.MemorySharingConsentRecorded &&
           !damagedSettings.AutomaticMemoryEnabled,
        "a damaged settings file enabled long-term-memory sharing without consent");

    var authorizedSettingsPath = Path.Combine(testRoot, "authorized-settings.json");
    await File.WriteAllTextAsync(
        authorizedSettingsPath,
        "{\"memorySystemVersion\":2,\"shareMemoryWithDialogue\":true," +
        "\"memorySharingConsentRecorded\":true,\"automaticMemoryEnabled\":true}",
        Encoding.UTF8);
    var authorizedSettings = new SettingsStore(authorizedSettingsPath).Load();
    Expect(authorizedSettings.ShareMemoryWithDialogue &&
           authorizedSettings.MemorySharingConsentRecorded &&
           authorizedSettings.AutomaticMemoryEnabled,
        "an explicit long-term-memory authorization was not preserved");

    Expect(!PetCommandRouter.LooksLikePetAddress("小龙虾怎么养"),
        "a normal phrase beginning with 小龙 was routed to the pet");
    Expect(PetCommandRouter.WakeWords[0] == "柯朵",
        "the current character name is not the primary wake word");
    Expect(PetCommandRouter.RouteToPetConversation("今天有点累") == "柯朵，今天有点累",
        "plain companion UI text was not mapped to the pet conversation");
    Expect(PetCommandRouter.RouteToPetConversation("  柯朵，今天有点累  ") == "柯朵，今天有点累",
        "an existing pet address was duplicated by companion routing");
    Expect(PetCommandRouter.LooksLikePetAddress("小编龙，今天有点累"),
        "the former character name no longer works as a compatibility alias");
    Expect(PetCommandRouter.RouteToPetConversation("/codex 帮我写代码") ==
           "柯朵，/codex 帮我写代码",
        "the dedicated companion UI leaked a message into the Codex task escape route");
    Expect(PetCommandRouter.TryParse(
               PetCommandRouter.RouteToPetConversation("/codex 帮我写代码"),
               out var dedicatedSlashCommand) &&
           dedicatedSlashCommand?.Kind == PetCommandKind.Chat,
        "a dedicated companion message became a Codex task command after routing");
    Expect(PetCommandRouter.TryParse(
               "柯朵，设定一个目标有用吗？",
               out var settingQuestion) &&
           settingQuestion?.Kind == PetCommandKind.Chat,
        "a natural question beginning with 设定 was persisted as persona");
    Expect(PetCommandRouter.TryParse(
               "柯朵，记住过去有什么意义？",
               out var rememberQuestion) &&
           rememberQuestion?.Kind == PetCommandKind.Chat,
        "a natural question beginning with 记住 was persisted as memory");
    Expect(PetCommandRouter.TryParse(
               "柯朵，设定：回答时再害羞一点",
               out var explicitPersonaCommand) &&
           explicitPersonaCommand?.Kind == PetCommandKind.UpdatePersona,
        "an explicit persona command with a delimiter was not recognized");
    Expect(PetCommandRouter.TryParse(
               "柯朵，记住：我喜欢无糖拿铁",
               out var explicitMemoryCommand) &&
           explicitMemoryCommand?.Kind == PetCommandKind.Remember,
        "an explicit memory command with a delimiter was not recognized");

    var capacityMemoryPath = Path.Combine(testRoot, "capacity-memory.json");
    var capacityMemory = new PetMemoryStore(capacityMemoryPath);
    for (var index = 0; index < PetMemoryLimits.MaxEntries; index++)
    {
        var maximumLengthChineseMemory = new string('龙', 76) + index.ToString("D4");
        var capacityMutation = await capacityMemory.RememberAsync(maximumLengthChineseMemory);
        Expect(capacityMutation.Status == PetMemoryMutationStatus.Remembered,
            $"legal capacity entry {index} could not be written: {capacityMutation.Status}");
    }

    Expect(new FileInfo(capacityMemoryPath).Length > 64 * 1024,
        "capacity regression fixture did not exceed the former 64 KiB ceiling");
    var reloadedCapacityMemory = new PetMemoryStore(capacityMemoryPath);
    Expect(reloadedCapacityMemory.Snapshot.State == PetMemoryStoreState.Ready &&
           reloadedCapacityMemory.Count == PetMemoryLimits.MaxEntries,
        "a legal full Chinese memory library did not survive reload");

    var memory = new PetMemoryStore(Path.Combine(testRoot, "memory.json"));
    var profile = new CharacterProfileStore(Path.Combine(testRoot, "profile.json"));
    var archive = new ConversationArchiveStore(Path.Combine(testRoot, "history.dpapi.jsonl"));
    var context = new ConversationContextStore(Path.Combine(testRoot, "context.json"));
    var generator = new FakeDialogueGenerator();
    var dialogue = new PetDialogueService(generator, memory, profile, archive, context);

    Expect(CompanionThreadIdentity.ConversationTitle == "柯朵的房间" &&
           CompanionThreadIdentity.ConversationSource == "xiaobian_pet_companion_chat",
        "the room title changed without preserving its stable internal identity");

    var roomIndex = new PetMemoryStore(Path.Combine(testRoot, "room-index.json"));
    _ = await roomIndex.RememberAsync("主人喜欢看星星");
    var roomGenerator = new FakeDialogueGenerator { Mode = FakeMode.Empty };
    var roomDialogue = new PetDialogueService(roomGenerator, roomIndex);
    _ = await roomDialogue.CreateLineAsync(
        PetDialogueIntent.DirectChat,
        "第一次房间测试",
        null,
        dynamicDialogueEnabled: true,
        shareMemoryWithDialogue: true);
    using (var firstRoomPayload = JsonDocument.Parse(roomGenerator.LastPayload))
    {
        Expect(firstRoomPayload.RootElement.GetProperty("memory_ledger").GetArrayLength() == 1,
            "the local index was not restored once when the persistent room became active");
    }

    _ = await roomDialogue.CreateLineAsync(
        PetDialogueIntent.DirectChat,
        "第二次房间测试",
        null,
        dynamicDialogueEnabled: true,
        shareMemoryWithDialogue: true);
    using (var nextRoomPayload = JsonDocument.Parse(roomGenerator.LastPayload))
    {
        Expect(nextRoomPayload.RootElement.GetProperty("memory_ledger").GetArrayLength() == 0,
            "the full local memory index was retransmitted on every room turn");
    }

    _ = await roomDialogue.TryHandleInputAsync(
        "柯朵，记住：主人喜欢月亮",
        dynamicDialogueEnabled: true,
        shareMemoryWithDialogue: false,
        automaticMemoryEnabled: false,
        PetInputSource.Text);
    using (var explicitRoomMemoryPayload = JsonDocument.Parse(roomGenerator.LastPayload))
    {
        Expect(explicitRoomMemoryPayload.RootElement.GetProperty("user_text").GetString() ==
               "主人喜欢月亮",
            "an explicit memory was saved locally without entering the persistent room");
    }

    var remembered = await dialogue.TryHandleInputAsync(
        "柯朵，我喜欢无糖拿铁",
        dynamicDialogueEnabled: true,
        shareMemoryWithDialogue: true,
        automaticMemoryEnabled: true,
        PetInputSource.Text);
    Expect(remembered?.Reply == "好嘛，我记住一点点。", "structured speech was not parsed");
    Expect(memory.Count == 1 && memory.Snapshot.Entries[0].Category == PetMemoryCategory.Preference,
        "automatic preference was not persisted");
    Expect(remembered?.Detail?.Contains("无糖拿铁", StringComparison.Ordinal) == true,
        "automatic memory was not surfaced to the user");
    Expect(generator.LastChannel == PetDialogueChannel.Conversation,
        "direct chat did not use conversation channel");

    generator.Mode = FakeMode.ForgedEvidence;
    _ = await dialogue.TryHandleInputAsync(
        "柯朵，今天心情不错",
        true,
        true,
        true,
        PetInputSource.Text);
    Expect(memory.Count == 1, "forged source quote was accepted");

    generator.Mode = FakeMode.ForgedContentWithRealEvidence;
    _ = await dialogue.TryHandleInputAsync(
        "柯朵，我喜欢香蕉牛奶",
        true,
        true,
        true,
        PetInputSource.Text);
    Expect(memory.Find("香菜").Count == 0,
        "model-supplied content overrode the verified source quote");
    Expect(memory.Find("主人喜欢香蕉牛奶").Count == 1,
        "verified source evidence was not canonicalized into memory");

    var explicitResult = await memory.RememberAsync("主人明确喜欢咖啡");
    Expect(explicitResult.Status == PetMemoryMutationStatus.Remembered, "explicit memory setup failed");
    generator.Mode = FakeMode.UpdateExplicit;
    generator.TargetMemoryId = explicitResult.Entry!.Id;
    _ = await dialogue.TryHandleInputAsync(
        "柯朵，我现在喜欢红茶",
        true,
        true,
        true,
        PetInputSource.Text);
    Expect(memory.Find("主人明确喜欢咖啡").Count == 1,
        "automatic update overwrote an explicit memory");

    var automaticTarget = await memory.UpsertAutomaticAsync(
        new PetAutomaticMemoryProposal(
            "add",
            string.Empty,
            PetMemoryCategory.Preference,
            "主人喜欢蓝莓蛋糕",
            "我喜欢蓝莓蛋糕",
            0.99,
            4));
    Expect(automaticTarget.Status == PetMemoryMutationStatus.Remembered,
        "automatic update target setup failed");
    var beforeUnrelatedUpdate = memory.Count;
    generator.Mode = FakeMode.UpdateUnrelatedAutomatic;
    generator.TargetMemoryId = automaticTarget.Entry!.Id;
    _ = await dialogue.TryHandleInputAsync(
        "柯朵，我现在喜欢红茶",
        true,
        true,
        true,
        PetInputSource.Text);
    Expect(memory.Count == beforeUnrelatedUpdate &&
           memory.Find("主人喜欢蓝莓蛋糕").Count == 1 &&
           memory.Find("主人现在喜欢红茶").Count == 0,
        "an unrelated automatic memory target was rewritten");

    generator.Mode = FakeMode.Empty;
    var clicked = await dialogue.CreateLineAsync(
        PetDialogueIntent.PetClicked,
        null,
        null,
        dynamicDialogueEnabled: true,
        shareMemoryWithDialogue: true);
    Expect(!string.IsNullOrWhiteSpace(clicked) && generator.LastChannel == PetDialogueChannel.Ambient,
        "desktop interaction did not use ambient channel");

    var overloaded = await dialogue.CreateLineAsync(
        PetDialogueIntent.TaskOverloaded,
        null,
        null,
        dynamicDialogueEnabled: false,
        shareMemoryWithDialogue: false);
    Expect(overloaded.Contains("累", StringComparison.Ordinal) ||
           overloaded.Contains("不开心", StringComparison.Ordinal) ||
           overloaded.Contains("排成", StringComparison.Ordinal) ||
           overloaded.Contains("数乱", StringComparison.Ordinal),
        $"task overload fallback did not express situational displeasure: {overloaded}");

    var dropped = await dialogue.CreateLineAsync(
        PetDialogueIntent.DragDropped,
        null,
        null,
        dynamicDialogueEnabled: false,
        shareMemoryWithDialogue: false);
    Expect(!dropped.Contains("谢谢", StringComparison.Ordinal) &&
           !dropped.Contains("乖乖", StringComparison.Ordinal) &&
           !dropped.Contains("还不错", StringComparison.Ordinal),
        $"drag-drop fallback became immediately cheerful again: {dropped}");

    var wingGrab = await dialogue.CreateLineAsync(
        PetDialogueIntent.WingGrabStarted,
        null,
        null,
        dynamicDialogueEnabled: false,
        shareMemoryWithDialogue: false);
    Expect(wingGrab.Contains("疼", StringComparison.Ordinal) ||
           wingGrab.Contains("痛", StringComparison.Ordinal) ||
           wingGrab.Contains("翅膀", StringComparison.Ordinal),
        $"wing-grab fallback lost its pain context: {wingGrab}");

    var wingRelease = await dialogue.CreateLineAsync(
        PetDialogueIntent.WingGrabReleased,
        null,
        null,
        dynamicDialogueEnabled: false,
        shareMemoryWithDialogue: false);
    Expect(wingRelease.Contains("不许", StringComparison.Ordinal) ||
           wingRelease.Contains("生气", StringComparison.Ordinal) ||
           wingRelease.Contains("退后", StringComparison.Ordinal) ||
           wingRelease.Contains("过分", StringComparison.Ordinal) ||
           wingRelease.Contains("翻脸", StringComparison.Ordinal),
        $"wing-release fallback was not firm enough: {wingRelease}");

    var headGrab = await dialogue.CreateLineAsync(
        PetDialogueIntent.HeadGrabStarted,
        null,
        null,
        dynamicDialogueEnabled: false,
        shareMemoryWithDialogue: false);
    Expect(headGrab.Contains("头", StringComparison.Ordinal) ||
           headGrab.Contains("脑袋", StringComparison.Ordinal) ||
           headGrab.Contains("聪明", StringComparison.Ordinal),
        $"head-grab fallback lost its head context: {headGrab}");

    var bodyGrab = await dialogue.CreateLineAsync(
        PetDialogueIntent.BodyGrabStarted,
        null,
        null,
        dynamicDialogueEnabled: true,
        shareMemoryWithDialogue: false);
    using (var grabPayload = JsonDocument.Parse(generator.LastPayload))
    {
        Expect(grabPayload.RootElement.GetProperty("event_key").GetString() ==
               nameof(PetDialogueIntent.BodyGrabStarted),
            "the body-grab scene was not sent to the dialogue model");
        Expect(grabPayload.RootElement.GetProperty("event_name").GetString()?
                   .Contains("身体", StringComparison.Ordinal) == true,
            "the body-grab payload did not explain which part was grabbed");
    }

    var flicked = await dialogue.CreateLineAsync(
        PetDialogueIntent.HeadFlicked,
        null,
        null,
        dynamicDialogueEnabled: false,
        shareMemoryWithDialogue: false);
    Expect(flicked.Contains("啊", StringComparison.Ordinal) ||
           flicked.Contains("呀", StringComparison.Ordinal) ||
           flicked.Contains("疼", StringComparison.Ordinal) ||
           flicked.Contains("哎呦", StringComparison.Ordinal),
        $"head-flick fallback did not begin from a physical reaction: {flicked}");

    _ = await dialogue.CreateLineAsync(
        PetDialogueIntent.HeadFlickAngry,
        null,
        null,
        dynamicDialogueEnabled: true,
        shareMemoryWithDialogue: false);
    using (var flickAngerPayload = JsonDocument.Parse(generator.LastPayload))
    {
        Expect(flickAngerPayload.RootElement.GetProperty("event_key").GetString() ==
               nameof(PetDialogueIntent.HeadFlickAngry),
            "the Luna head-flick anger follow-up was not sent as its own event");
    }

    var running = await dialogue.CreateLineAsync(
        PetDialogueIntent.RunningEffort,
        null,
        null,
        dynamicDialogueEnabled: true,
        shareMemoryWithDialogue: false);
    using (var runningPayload = JsonDocument.Parse(generator.LastPayload))
    {
        Expect(runningPayload.RootElement.GetProperty("event_key").GetString() ==
               nameof(PetDialogueIntent.RunningEffort),
            "the running-effort scene was not sent to the dialogue model");
        Expect(runningPayload.RootElement.GetProperty("event_name").GetString()?
                   .Contains("跑", StringComparison.Ordinal) == true,
            "the running-effort payload did not explain the physical action");
    }

    _ = await dialogue.CreateLineAsync(
        PetDialogueIntent.RunFallImpact,
        null,
        null,
        dynamicDialogueEnabled: true,
        shareMemoryWithDialogue: false);
    using (var impactPayload = JsonDocument.Parse(generator.LastPayload))
    {
        Expect(impactPayload.RootElement.GetProperty("event_key").GetString() ==
               nameof(PetDialogueIntent.RunFallImpact),
            "the running-fall impact scene was not sent as its own event");
        Expect(impactPayload.RootElement.GetProperty("event_name").GetString()?
                   .Contains("摔", StringComparison.Ordinal) == true,
            "the running-fall impact payload did not explain the fall");
    }
    var runFallFallback = await dialogue.CreateLineAsync(
        PetDialogueIntent.RunFallImpact,
        null,
        null,
        dynamicDialogueEnabled: false,
        shareMemoryWithDialogue: false);
    Expect(runFallFallback.Contains("疼", StringComparison.Ordinal),
        $"running-fall impact fallback did not express pain: {runFallFallback}");

    _ = await dialogue.CreateLineAsync(
        PetDialogueIntent.TaskCompletedStillBusy,
        null,
        null,
        dynamicDialogueEnabled: true,
        shareMemoryWithDialogue: false);
    using (var scenePayload = JsonDocument.Parse(generator.LastPayload))
    {
        Expect(scenePayload.RootElement.GetProperty("event_key").GetString() ==
               nameof(PetDialogueIntent.TaskCompletedStillBusy),
            "the still-busy scene was not sent to the dialogue model");
    }

    var persona = await dialogue.TryHandleInputAsync(
        "柯朵，设定：回答时再害羞一点",
        true,
        true,
        true,
        PetInputSource.Text);
    Expect(profile.Snapshot.OwnerDirectives.Contains("回答时再害羞一点", StringComparer.Ordinal),
        $"persona directive did not persist: {string.Join(" | ", profile.Snapshot.OwnerDirectives)}; " +
        $"reply={persona?.Reply}; detail={persona?.Detail}");
    Expect(persona?.Detail?.Contains("角色设定已更新", StringComparison.Ordinal) == true,
        "persona update was not explained");

    var beforeVoiceDelete = memory.Count;
    var voiceDelete = await dialogue.TryHandleInputAsync(
        "柯朵，忘掉主人明确喜欢咖啡",
        false,
        true,
        true,
        PetInputSource.Voice);
    Expect(memory.Count == beforeVoiceDelete, "voice command destructively deleted memory");
    Expect(voiceDelete?.Detail?.Contains("语音误识别", StringComparison.Ordinal) == true,
        "voice deletion protection was not visible");

    var history = await archive.ReadRecentAsync(20);
    Expect(history.Count >= 4, "encrypted conversation history did not round-trip");
    Expect(history.Any(entry => entry.Source == PetInputSource.Voice),
        "voice source was not preserved in encrypted history");
    var rawArchive = await File.ReadAllTextAsync(archive.ArchivePath, Encoding.UTF8);
    Expect(!rawArchive.Contains("无糖拿铁", StringComparison.Ordinal) &&
           !rawArchive.Contains("回答时再害羞一点", StringComparison.Ordinal),
        "conversation archive leaked plaintext");

    await dialogue.ResetSessionAsync();
    generator.ConversationThreadIdValue = null;
    generator.Mode = FakeMode.Empty;
    _ = await dialogue.TryHandleInputAsync(
        "柯朵，今天只是测试",
        true,
        true,
        false,
        PetInputSource.Text);
    using (var payload = JsonDocument.Parse(generator.LastPayload))
    {
        Expect(payload.RootElement.GetProperty("recent_local_history").GetArrayLength() == 0,
            "retired conversation history was injected into the new context epoch");
    }

    var sessionPath = Path.Combine(testRoot, "session.json");
    var sessionStore = new CompanionSessionStore(sessionPath);
    var threadId = Guid.NewGuid().ToString();
    await sessionStore.SaveAsync(
        threadId,
        "gpt-5.6-luna",
        CompanionThreadIdentity.ConversationSource);
    Expect(sessionStore.Load().ConversationThreadId == threadId,
        "persistent companion session id did not round-trip");
    await sessionStore.ClearAsync();
    var retiredSession = sessionStore.Load();
    Expect(retiredSession.ConversationThreadId is null,
        "persistent companion session id did not clear");
    Expect(retiredSession.RetiredThreadIds.Contains(threadId, StringComparer.Ordinal),
        "cleared companion session did not leave a durable retirement tombstone");
    Expect(retiredSession.PendingArchiveThreadIds.Contains(threadId, StringComparer.Ordinal),
        "cleared companion session did not queue the thread for archival");
    await sessionStore.CompleteRetirementAsync(threadId);
    var archivedSession = sessionStore.Load();
    Expect(archivedSession.RetiredThreadIds.Contains(threadId, StringComparer.Ordinal) &&
           archivedSession.PendingArchiveThreadIds.Count == 0,
        "completed archive lost its durable retirement tombstone");

    Expect(CompanionThreadIdentity.IsCompanionWorkspace(
            CompanionThreadIdentity.ConversationWorkspace),
        "companion workspace filter did not recognize its own room");

    var initialFrame = ArkAgentPlanAsrClient.BuildClientFrame(
        0x11,
        1,
        Encoding.UTF8.GetBytes("{\"request\":{}}"));
    Expect(initialFrame.AsSpan(0, 4).SequenceEqual(new byte[] { 0x11, 0x11, 0x11, 0x00 }),
        "Agent Plan ASR initial frame header was wrong");
    Expect(BinaryPrimitives.ReadInt32BigEndian(initialFrame.AsSpan(4, 4)) == 1,
        "Agent Plan ASR initial frame sequence was wrong");

    var audioFrame = ArkAgentPlanAsrClient.BuildClientFrame(
        0x21,
        2,
        new byte[] { 1, 2, 3, 4 });
    Expect(audioFrame.AsSpan(0, 4).SequenceEqual(new byte[] { 0x11, 0x21, 0x01, 0x00 }),
        "Agent Plan ASR audio frame header was wrong");
    Expect(BinaryPrimitives.ReadInt32BigEndian(audioFrame.AsSpan(4, 4)) == 2,
        "Agent Plan ASR audio frame sequence was wrong");

    var finalAudioFrame = ArkAgentPlanAsrClient.BuildClientFrame(
        0x23,
        -3,
        new byte[] { 5, 6, 7, 8 });
    Expect(finalAudioFrame.AsSpan(0, 4).SequenceEqual(new byte[] { 0x11, 0x23, 0x01, 0x00 }),
        "Agent Plan ASR final audio frame header was wrong");
    Expect(BinaryPrimitives.ReadInt32BigEndian(finalAudioFrame.AsSpan(4, 4)) == -3,
        "Agent Plan ASR final audio frame sequence was wrong");

    IReadOnlyDictionary<string, IEnumerable<string>> responseHeaders =
        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Tt-Logid"] = new[] { "  asr-log-123  " }
        };
    Expect(ArkAgentPlanAsrClient.TryReadLogId(responseHeaders) == "asr-log-123",
        "Agent Plan ASR X-Tt-Logid was not captured and normalized");

    Expect(ArkAgentPlanAsrClient.NormalizeLogId("bad\rlog") is null,
        "Agent Plan ASR accepted a control character in X-Tt-Logid");
    Expect(ArkAgentPlanAsrClient.NormalizeLogId(new string('x', 129)) is null,
        "Agent Plan ASR accepted an oversized X-Tt-Logid");
    var diagnosticException = new ArkAgentPlanAsrException(
        "ASR failed",
        logId: "asr-log-123");
    Expect(diagnosticException.ToString().Contains(
            "X-Tt-Logid: asr-log-123",
            StringComparison.Ordinal),
        "Agent Plan ASR diagnostic exception lost X-Tt-Logid");

    var finalPayload = Encoding.UTF8.GetBytes("{\"result\":{\"text\":\"主人你好\"}}");
    byte[] compressedFinalPayload;
    using (var compressedStream = new MemoryStream())
    {
        using (var gzip = new GZipStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(finalPayload);
        }

        compressedFinalPayload = compressedStream.ToArray();
    }

    var serverFrame = new byte[12 + compressedFinalPayload.Length];
    serverFrame[0] = 0x11;
    serverFrame[1] = 0x93;
    serverFrame[2] = 0x11;
    BinaryPrimitives.WriteInt32BigEndian(serverFrame.AsSpan(4, 4), 3);
    BinaryPrimitives.WriteUInt32BigEndian(
        serverFrame.AsSpan(8, 4),
        checked((uint)compressedFinalPayload.Length));
    compressedFinalPayload.CopyTo(serverFrame, 12);
    var parsedServerFrame = ArkAgentPlanAsrClient.ParseServerFrame(serverFrame);
    Expect(parsedServerFrame.IsFinal && parsedServerFrame.Text == "主人你好",
        "Agent Plan ASR final response parser was wrong");

    var liveAsrSample = Environment.GetEnvironmentVariable("XIAOBIAN_LIVE_ASR_SAMPLE");
    if (!string.IsNullOrWhiteSpace(liveAsrSample))
    {
        var credential = WindowsCredentialStore.GetArkAgentPlanApiKey()
            ?? throw new InvalidOperationException("Agent Plan credential was unavailable for live ASR test");
        var asr = new ArkAgentPlanAsrClient();
        var transcript = await asr.TranscribeAsync(credential, liveAsrSample);
        Expect(!string.IsNullOrWhiteSpace(transcript), "live Agent Plan ASR transcript was empty");
        Console.WriteLine($"LIVE_ASR_TRANSCRIPT={transcript}");
        Console.WriteLine($"LIVE_ASR_BACKEND={asr.BackendDescription}");
        Console.WriteLine($"LIVE_ASR_LOG_ID={asr.LastLogId ?? "<missing>"}");
    }

    Console.WriteLine("MEMORY_CONVERSATION_HARNESS_OK");
}
finally
{
    var resolved = Path.GetFullPath(testRoot);
    var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) +
               Path.DirectorySeparatorChar;
    if (resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(resolved).StartsWith("XiaobianPet.MemoryV2.", StringComparison.Ordinal))
    {
        Directory.Delete(resolved, recursive: true);
    }
}

internal enum FakeMode
{
    Valid,
    ForgedEvidence,
    ForgedContentWithRealEvidence,
    UpdateExplicit,
    UpdateUnrelatedAutomatic,
    Empty
}

internal sealed class FakeDialogueGenerator : IPetDialogueGenerator
{
    public FakeMode Mode { get; set; } = FakeMode.Valid;

    public string TargetMemoryId { get; set; } = string.Empty;

    public PetDialogueChannel LastChannel { get; private set; }

    public string? ConversationThreadIdValue { get; set; } =
        "00000000-0000-0000-0000-000000000001";

    public string? ConversationThreadId => ConversationThreadIdValue;

    public string LastPayload { get; private set; } = string.Empty;

    public Task<string?> GenerateAsync(
        string systemPrompt,
        string userPayload,
        PetDialogueChannel channel,
        CancellationToken cancellationToken = default)
    {
        LastChannel = channel;
        LastPayload = userPayload;
        object[] candidates = Mode switch
        {
            FakeMode.Valid =>
            [
                new
                {
                    operation = "add",
                    target_memory_id = "",
                    category = "preference",
                    content = "主人喜欢无糖拿铁",
                    source_quote = "我喜欢无糖拿铁",
                    confidence = 0.97,
                    importance = 4
                }
            ],
            FakeMode.ForgedEvidence =>
            [
                new
                {
                    operation = "add",
                    target_memory_id = "",
                    category = "preference",
                    content = "主人讨厌香菜",
                    source_quote = "我讨厌香菜",
                    confidence = 0.99,
                    importance = 5
                }
            ],
            FakeMode.ForgedContentWithRealEvidence =>
            [
                new
                {
                    operation = "add",
                    target_memory_id = "",
                    category = "preference",
                    content = "主人讨厌香菜",
                    source_quote = "我喜欢香蕉牛奶",
                    confidence = 0.99,
                    importance = 4
                }
            ],
            FakeMode.UpdateExplicit =>
            [
                new
                {
                    operation = "update",
                    target_memory_id = TargetMemoryId,
                    category = "preference",
                    content = "主人现在喜欢红茶",
                    source_quote = "我现在喜欢红茶",
                    confidence = 0.99,
                    importance = 4
                }
            ],
            FakeMode.UpdateUnrelatedAutomatic =>
            [
                new
                {
                    operation = "update",
                    target_memory_id = TargetMemoryId,
                    category = "preference",
                    content = "主人现在喜欢红茶",
                    source_quote = "我现在喜欢红茶",
                    confidence = 0.99,
                    importance = 4
                }
            ],
            _ => []
        };

        return Task.FromResult<string?>(JsonSerializer.Serialize(new
        {
            speech = "好嘛，我记住一点点。",
            memory_candidates = candidates
        }));
    }
}
