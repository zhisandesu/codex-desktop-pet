namespace XiaobianPet.Models;

public enum PetGrabPhase
{
    Started,
    Holding,
    Released
}

/// <summary>
/// Keeps the physical grab target in the dialogue event instead of reducing all
/// drags to the same generic reaction.
/// </summary>
public static class PetGrabDialogueContext
{
    public static bool IsGrabIntent(PetDialogueIntent intent) => intent is
        PetDialogueIntent.WingGrabStarted or
        PetDialogueIntent.WingGrabHolding or
        PetDialogueIntent.WingGrabReleased or
        PetDialogueIntent.HeadGrabStarted or
        PetDialogueIntent.HeadGrabHolding or
        PetDialogueIntent.HeadGrabReleased or
        PetDialogueIntent.BodyGrabStarted or
        PetDialogueIntent.BodyGrabHolding or
        PetDialogueIntent.BodyGrabReleased;

    public static bool IsReleasedIntent(PetDialogueIntent intent) => intent is
        PetDialogueIntent.WingGrabReleased or
        PetDialogueIntent.HeadGrabReleased or
        PetDialogueIntent.BodyGrabReleased;

    public static PetDialogueIntent Resolve(PetGrabRegion region, PetGrabPhase phase) =>
        (region, phase) switch
        {
            (PetGrabRegion.Wing, PetGrabPhase.Started) => PetDialogueIntent.WingGrabStarted,
            (PetGrabRegion.Wing, PetGrabPhase.Holding) => PetDialogueIntent.WingGrabHolding,
            (PetGrabRegion.Wing, PetGrabPhase.Released) => PetDialogueIntent.WingGrabReleased,
            (PetGrabRegion.Head, PetGrabPhase.Started) => PetDialogueIntent.HeadGrabStarted,
            (PetGrabRegion.Head, PetGrabPhase.Holding) => PetDialogueIntent.HeadGrabHolding,
            (PetGrabRegion.Head, PetGrabPhase.Released) => PetDialogueIntent.HeadGrabReleased,
            (PetGrabRegion.Body, PetGrabPhase.Started) => PetDialogueIntent.BodyGrabStarted,
            (PetGrabRegion.Body, PetGrabPhase.Holding) => PetDialogueIntent.BodyGrabHolding,
            (PetGrabRegion.Body, PetGrabPhase.Released) => PetDialogueIntent.BodyGrabReleased,
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
        };

    public static string? SpeechPerformance(PetDialogueIntent intent) => intent switch
    {
        PetDialogueIntent.WingGrabStarted =>
            "翅膀突然被抓疼了：不要铺垫，开头直接连续喊两到四拍疼，短促、明显吃痛；随后奶声委屈地抗议，但不要持续尖叫。",
        PetDialogueIntent.WingGrabHolding =>
            "翅膀还被捏着：疼痛稍缓但更委屈、更不耐烦，催主人轻一点或松手；不要平读。",
        PetDialogueIntent.WingGrabReleased =>
            "翅膀刚被松开：立即硬气地发火，短、脆、重音明确；不要先缓气，不要撒娇，不要求主人。仍是奶幼女声，但态度像被踩到底线的小公主。",
        PetDialogueIntent.HeadGrabStarted =>
            "头突然被抓住：先闷闷地惊呼，再晕乎又认真地抗议，像担心自己会被弄笨的小女孩。",
        PetDialogueIntent.HeadGrabHolding =>
            "头还被拎着：声音晕乎、软萌又不高兴，略带嘴硬；重音放在护住脑袋和想下来。",
        PetDialogueIntent.HeadGrabReleased =>
            "头刚被放开：立刻硬气地责怪主人，短促、清楚、带火气；不要小声缓气，不要委屈求情。奶幼音色保留，态度必须站得住。",
        PetDialogueIntent.BodyGrabStarted =>
            "身体突然离地：开头是新鲜又慌张的惊讶，随后发现自己被托起来；语调上扬，不要念成疼痛。",
        PetDialogueIntent.BodyGrabHolding =>
            "身体悬在半空：奶声慌张、怕高又有点嘴硬，想让主人放她下来；语速稍快。",
        PetDialogueIntent.BodyGrabReleased =>
            "身体刚落地：马上用短句硬气地设边界，重音干脆、怒气明确；不要先松气，不要温柔道谢，也不要把生气说成撒娇。",
        _ => null
    };
}
