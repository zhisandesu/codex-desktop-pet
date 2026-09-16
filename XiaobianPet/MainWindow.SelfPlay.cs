using System.IO;
using System.Text.Json;
using XiaobianPet.Models;

namespace XiaobianPet;

public partial class MainWindow
{
    private async Task PrepareSelfPlayAnimationsAsync()
    {
        var atlas = _spriteAtlas!;
        try
        {
            var sequences = await Task.Run(() => new[]
                {
                    PetMood.SelfPlayPeekaboo, PetMood.SelfPlayAirplane,
                    PetMood.SelfPlayTail, PetMood.RecycleBinPeek
                }.Select(atlas.PrepareSequence).ToArray(), _dialogueLifetimeCts.Token);
            var receipt = new
            {
                status = "ready", processId = Environment.ProcessId,
                updatedAt = DateTimeOffset.UtcNow, allClipsFullyDecoded = true,
                totalFrames = sequences.Sum(sequence => sequence.Frames.Count),
                clips = sequences.Select(sequence => new
                {
                    mood = sequence.VisualMood.ToString(), frameCount = sequence.Frames.Count,
                    seconds = sequence.Frames.Count * sequence.FrameDuration.TotalSeconds
                }),
                window = new { isLoaded = IsLoaded, isVisible = IsVisible, left = Left, top = Top },
                bugAnimationEnabled = false
            };
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaobianPet");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "self-play-runtime-status.json"),
                JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }),
                _dialogueLifetimeCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.WriteErrorLog("Self-play animation preparation", ex); }
    }
}
