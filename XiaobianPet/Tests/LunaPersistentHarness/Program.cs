using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using XiaobianPet.Models;
using XiaobianPet.Services;

static void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

var testRoot = Path.Combine(Path.GetTempPath(), $"XiaobianPet.LunaPersistent.{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);
try
{
    string firstThreadId;
    await using (var firstClient = new CodexPetDialogueClient())
    {
        var observedThreads = new ConcurrentDictionary<string, PetDialogueChannel>(
            StringComparer.Ordinal);
        firstClient.CompanionThreadObserved += (_, observed) =>
            observedThreads[observed.ThreadId] = observed.Channel;
        var firstDialogue = new PetDialogueService(
            firstClient,
            new PetMemoryStore(Path.Combine(testRoot, "memory-1.json")));
        await firstDialogue.WarmUpAsync();
        var first = await firstDialogue.CreateLineAsync(
            PetDialogueIntent.DirectChat,
            "我给今天的小目标起名叫琥珀，等会儿要考考你",
            memories: null,
            dynamicDialogueEnabled: true,
            shareMemoryWithDialogue: false);
        Expect(!string.IsNullOrWhiteSpace(first), "first persistent Luna reply was empty");
        firstThreadId = firstClient.ConversationThreadId
            ?? throw new InvalidOperationException("persistent Luna thread id was unavailable");
        Expect(
            observedThreads.TryGetValue(firstThreadId, out var firstChannel) &&
            firstChannel == PetDialogueChannel.Conversation,
            "persistent companion thread id was not broadcast for monitor exclusion");
        Console.WriteLine($"FIRST_THREAD={firstThreadId}");
        Console.WriteLine($"FIRST_REPLY={first}");
    }

    await using (var diagnostic = new CodexAppServerClient(marshalEventsToCurrentContext: false))
    {
        await diagnostic.ConnectAsync();
        var metadata = await diagnostic.ReadThreadMetadataAsync(firstThreadId);
        Console.WriteLine(
            $"METADATA=cwd:{metadata.Cwd};source:{metadata.ThreadSource};" +
            $"ephemeral:{metadata.Ephemeral};provider:{metadata.ModelProvider};name:{metadata.Name}");
    }

    await using (var secondClient = new CodexPetDialogueClient())
    {
        var observedThreads = new ConcurrentDictionary<string, PetDialogueChannel>(
            StringComparer.Ordinal);
        secondClient.CompanionThreadObserved += (_, observed) =>
            observedThreads[observed.ThreadId] = observed.Channel;
        var secondMemory = new PetMemoryStore(Path.Combine(testRoot, "memory-2.json"));
        var secondDialogue = new PetDialogueService(
            secondClient,
            secondMemory);
        await secondDialogue.WarmUpAsync();
        Expect(secondClient.ConversationThreadId == firstThreadId,
            $"persistent Luna room did not resume the same thread id: " +
            $"expected={firstThreadId}; actual={secondClient.ConversationThreadId}");
        Expect(
            observedThreads.TryGetValue(firstThreadId, out var resumedChannel) &&
            resumedChannel == PetDialogueChannel.Conversation,
            "resumed companion thread id was not broadcast for monitor exclusion");

        var click = await secondDialogue.CreateLineAsync(
            PetDialogueIntent.PetClicked,
            null,
            null,
            dynamicDialogueEnabled: true,
            shareMemoryWithDialogue: false);
        Expect(!string.IsNullOrWhiteSpace(click), "ambient Luna reply was empty");
        Expect(
            observedThreads.Any(pair =>
                pair.Value == PetDialogueChannel.Ambient &&
                !string.Equals(pair.Key, firstThreadId, StringComparison.Ordinal)),
            "ambient companion thread id was not broadcast for monitor exclusion");

        var recalled = await secondDialogue.CreateLineAsync(
            PetDialogueIntent.DirectChat,
            "我刚才给小目标起的名字是什么",
            memories: null,
            dynamicDialogueEnabled: true,
            shareMemoryWithDialogue: false);
        Expect(StringInfo.ParseCombiningCharacters(recalled).Length <= 42,
            "persistent Luna reply exceeded voice length");
        Expect(recalled.Contains("琥珀", StringComparison.Ordinal),
            $"persistent Luna room lost cross-restart context: {recalled}");
        Console.WriteLine($"RESUMED_THREAD={secondClient.ConversationThreadId}");
        Console.WriteLine($"RECALLED_REPLY={recalled}");

        var memoryTurn = await secondDialogue.TryHandleInputAsync(
            "柯朵，这是自动化测试：我最喜欢的饮料是桂花乌龙，这件事很重要",
            dynamicDialogueEnabled: true,
            shareMemoryWithDialogue: true,
            automaticMemoryEnabled: true,
            PetInputSource.Text);
        Expect(memoryTurn is not null && secondMemory.Count > 0,
            $"real Luna did not produce a validated memory candidate: {memoryTurn?.Reply}");
        Expect(secondMemory.Snapshot.Entries.Any(entry =>
                entry.Content.Contains("桂花乌龙", StringComparison.Ordinal)),
            "real Luna memory candidate lost the stated preference");
        Console.WriteLine($"AUTO_MEMORY={secondMemory.Snapshot.Entries[0].Content}");

        // The room contains synthetic test facts. Archive it and clear only the
        // routing id so the shipped pet starts with a clean, persistent room.
        await secondClient.ResetSessionAsync();
    }

    Expect(new CompanionSessionStore().Load().ConversationThreadId is null,
        "synthetic Luna test room was not retired");
    Console.WriteLine("LUNA_PERSISTENT_HARNESS_OK");
}
finally
{
    var resolved = Path.GetFullPath(testRoot);
    var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) +
               Path.DirectorySeparatorChar;
    if (resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(resolved).StartsWith("XiaobianPet.LunaPersistent.", StringComparison.Ordinal))
    {
        Directory.Delete(resolved, recursive: true);
    }
}
