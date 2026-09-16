namespace XiaobianPet.Models;

public static class PetActionDialogueContext
{
    private static readonly IReadOnlyDictionary<PetDialogueIntent, string[]> InstantReactions =
        new Dictionary<PetDialogueIntent, string[]>
        {
            [PetDialogueIntent.WingGrabStarted] =
            [
                "疼疼疼！",
                "呜哇，好疼！",
                "疼呀，轻一点！"
            ],
            [PetDialogueIntent.HeadGrabStarted] =
            [
                "呜哇，我的头！",
                "呀！脑袋会晕的！",
                "哎呦，别拎头！"
            ],
            [PetDialogueIntent.BodyGrabStarted] =
            [
                "呀！飞起来了！",
                "咦，我离地啦！",
                "呜哇，要飞走啦！"
            ],
            [PetDialogueIntent.WingGrabReleased] =
            [
                "哼！不许再碰我的翅膀！",
                "这次很过分，别想蒙混过去！",
                "主人退后，我真的生气了！",
                "再抓翅膀，我可真要翻脸啦！"
            ],
            [PetDialogueIntent.HeadGrabReleased] =
            [
                "哼！不许再拎我的头！",
                "先给我的脑袋道歉，立刻！",
                "这次很过分，我还气着呢！",
                "再抓脑袋，我可真要翻脸啦！"
            ],
            [PetDialogueIntent.BodyGrabReleased] =
            [
                "哼！不许再突然把我举起来！",
                "下次先问我，听见没有！",
                "我说放下就要放下，记住啦！",
                "这次很过分，别想装没事！"
            ],
            [PetDialogueIntent.HeadFlicked] =
            [
                "啊！",
                "哎呦！",
                "呀啊，好疼！"
            ],
            [PetDialogueIntent.RunningEffort] =
            [
                "嘿咻、嘿咻！",
                "嘿咻嘿咻嘿咻！",
                "哒哒哒，冲呀！"
            ],
            [PetDialogueIntent.RunFallImpact] =
            [
                "哎呀！好疼……",
                "呜哇，摔疼了！",
                "哎哟！疼疼疼……",
                "嘶……膝盖好疼。",
                "呀啊！摔得好疼！"
            ]
        };

    public static string? ChooseInstantReaction(PetDialogueIntent intent)
    {
        if (!InstantReactions.TryGetValue(intent, out var choices))
        {
            return null;
        }

        return choices[Random.Shared.Next(choices.Length)];
    }

    public static string? SpeechPerformance(PetDialogueIntent intent) => intent switch
    {
        PetDialogueIntent.HeadFlicked =>
            "脑门刚被突然弹中：只读未经思考的短促痛叫，爆发要真、像没防备；不要冷静叙述，也不要拖成长尖叫。",
        PetDialogueIntent.HeadFlickAngry =>
            "脑门痛叫后的气话：气鼓鼓、委屈、稍微提高音量，像被惹毛的小公主；不再重复痛叫。",
        PetDialogueIntent.RunningEffort =>
            "正在迈着小短腿跑：一拍一声，短促、有弹性、带一点换气和得意；可以主要念节奏拟声词，不要读成长句或运动解说。",
        PetDialogueIntent.RunFallImpact =>
            "刚刚结实地摔了一下：开头是没防备的短促痛叫，随后委屈地喊疼；声音要有真实撞击感，不要温柔解释，也不要夸张拖成长哭腔。",
        _ => null
    };
}
