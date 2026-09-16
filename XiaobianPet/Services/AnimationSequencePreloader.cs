using XiaobianPet.Models;

namespace XiaobianPet.Services;

public static class AnimationSequencePreloader
{
    public static Task WarmUpAsync(
        SpriteAtlas spriteAtlas,
        IEnumerable<PetMood> moods,
        Action<PetMood, Exception>? failureObserver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spriteAtlas);
        ArgumentNullException.ThrowIfNull(moods);

        // Snapshot and de-duplicate on the caller while preserving priority.
        // Decoding itself always runs on the thread pool and never captures the
        // WPF dispatcher. SpriteAtlas publishes a mood only after every frame
        // has decoded, validated, and frozen successfully.
        var orderedMoods = moods.Distinct().ToArray();
        return Task.Run(
            () =>
            {
                foreach (var mood in orderedMoods)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        spriteAtlas.PrepareSequence(mood);
                    }
                    catch (Exception exception) when (
                        exception is not OperationCanceledException ||
                        !cancellationToken.IsCancellationRequested)
                    {
                        failureObserver?.Invoke(mood, exception);
                    }
                }
            },
            cancellationToken);
    }
}
