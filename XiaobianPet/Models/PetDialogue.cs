namespace XiaobianPet.Models;

public enum PetDialogueChannel
{
    Conversation,
    Ambient
}

public enum PetInputSource
{
    Text,
    Voice
}

public enum PetDialogueIntent
{
    Greeting,
    TaskStarted,
    TaskOverloaded,
    ApprovalRequired,
    TaskCompleted,
    TaskCompletedStillBusy,
    TaskInterrupted,
    TaskFailed,
    VoicePreview,
    TtsEnabled,
    PetClicked,
    PetClickedWhileBusy,
    HeadFlicked,
    HeadFlickAngry,
    RunningEffort,
    RunFallImpact,
    PetDoubleClicked,
    PetRightClicked,
    DragStarted,
    Dragging,
    DragDropped,
    WingGrabStarted,
    WingGrabHolding,
    WingGrabReleased,
    HeadGrabStarted,
    HeadGrabHolding,
    HeadGrabReleased,
    BodyGrabStarted,
    BodyGrabHolding,
    BodyGrabReleased,
    DirectChat,
    MemorySaved,
    MemoryAlreadyKnown,
    MemoryList,
    MemoryRecalled,
    MemoryNotFound,
    MemoryForgotten,
    MemoryForgetAmbiguous,
    MemoryClearConfirmation,
    MemoryCleared,
    MemoryRejected,
    MemoryFull,
    PersonaUpdated,
    PersonaList,
    PersonaClearConfirmation,
    PersonaCleared,
    PersonaRejected,
    TaskModeratelyBusy
}

public sealed record PetInteractionResult(
    string Reply,
    string? Detail = null);
