namespace XiaobianPet.Models;

public sealed record TtsVoiceOption(
    string DisplayName,
    string Id,
    string? Instruction = null,
    int SpeechRate = 0,
    int Pitch = 0);

public static class TtsVoiceCatalog
{
    public const string YingTaoWanZiId = "zh_female_yingtaowanzi_uranus_bigtts";
    public const string JiaoRuoLuoLiId = "ICL_uranus_zh_female_jiaoruoluoli_tob";
    public const string PeiQiId = "zh_female_peiqi_uranus_bigtts";
    public const string MiZaiId = "zh_female_mizai_uranus_bigtts";
    public const string TiaoPiGongZhuId = "ICL_uranus_zh_female_tiaopigongzhu_tob";

    public const string DefaultId = TiaoPiGongZhuId;

    public const string LittleGirlInstruction =
        "请像五六岁的小女孩一样自然说话，奶声奶气、软乎乎、童真可爱；" +
        "跟随句意改变重音、停顿和语速，先读出当下反应，再自然收住，疼、惊讶、委屈、生气和开心必须有明显区别；" +
        "不要逐字匀速平读。年龄感一定要小，不要少女感、成人感、播音腔，也不要夸张夹子音或持续尖叫。";

    public const string TiaoPiGongZhuInstruction =
        LittleGirlInstruction +
        " 嗓音保留一点很轻的气声和细小颗粒感，像刚玩累时自然微沙；不要故意压嗓、低沉、成熟或做烟嗓。" +
        " 日常仍然软糯清亮；生气时微沙感可以稍明显，咬字更脆、更硬气。";

    public static IReadOnlyList<TtsVoiceOption> All { get; } =
    [
        new("娇弱萝莉 · 奶幼", JiaoRuoLuoLiId, LittleGirlInstruction, -6, 1),
        new("樱桃丸子 · 奶幼", YingTaoWanZiId, LittleGirlInstruction, -6, 1),
        new("佩奇猪 · 奶幼", PeiQiId, LittleGirlInstruction, -4, 1),
        new("咪仔 · 奶幼", MiZaiId, LittleGirlInstruction, -6, 1),
        new("调皮公主 · 微沙奶幼", TiaoPiGongZhuId, TiaoPiGongZhuInstruction, -5, 0)
    ];

    public static TtsVoiceOption GetOrDefault(string? id) =>
        All.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.Ordinal))
        ?? All.First(option => string.Equals(option.Id, DefaultId, StringComparison.Ordinal));

    public static string Sanitize(string? id) => GetOrDefault(id).Id;
}
