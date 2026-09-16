namespace XiaobianPet.Models;

public enum VoiceConversationStage
{
    Preparing,
    Listening,
    Transcribing,
    Thinking,
    Speaking
}

public sealed record VoiceConversationProgress(
    VoiceConversationStage Stage,
    string Message);

public sealed record VoiceConversationResult(
    string Transcript,
    PetInteractionResult Interaction,
    string RecognitionBackend);

