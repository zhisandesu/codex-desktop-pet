namespace XiaobianPet.Models;

// Aggregate statistics only. This model has no paths, names or file contents.
public sealed record RecycleBinSnapshot(bool IsAvailable, long ItemCount, long TotalBytes)
{
    public static RecycleBinSnapshot Unavailable { get; } = new(false, 0, 0);
}

public static class RecycleBinFeedback
{
    public const long ManyItemsThreshold = 20;
    public const long LargeBytesThreshold = 1024L * 1024 * 1024;

    public static string CreateLine(RecycleBinSnapshot snapshot)
    {
        if (!snapshot.IsAvailable || snapshot.ItemCount < 0 || snapshot.TotalBytes < 0)
            return "咦，暂时看不到回收站，等会儿再看看吧。";
        if (snapshot.ItemCount == 0)
            return "主人的垃圾桶竟然这么干净！一件垃圾都没有耶。";
        if (snapshot.ItemCount >= ManyItemsThreshold || snapshot.TotalBytes >= LargeBytesThreshold)
            return $"主人，你垃圾桶的垃圾太多啦！已经攒了 {snapshot.ItemCount} 项，要记得整理哦。";
        return $"翻到啦，主人垃圾桶里有 {snapshot.ItemCount} 项东西，还不算多嘛。";
    }
}
