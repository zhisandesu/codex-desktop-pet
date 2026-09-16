using System.Runtime.InteropServices;
using XiaobianPet.Models;

namespace XiaobianPet.Services;

/// <summary>Reads only the current user's aggregate Windows Recycle Bin statistics.</summary>
public sealed class RecycleBinStatisticsService
{
    private readonly object _gate = new();
    private readonly Func<RecycleBinSnapshot> _query;
    private Task<RecycleBinSnapshot>? _inFlight;

    public RecycleBinStatisticsService() : this(QueryNative) { }

    // Injectable read makes timeout, cancellation and error behavior testable
    // without creating, opening or deleting anything in the real Recycle Bin.
    public RecycleBinStatisticsService(Func<RecycleBinSnapshot> query) => _query = query;

    public async Task<RecycleBinSnapshot> QueryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<RecycleBinSnapshot> query;
        lock (_gate)
        {
            // A timed-out native query cannot be safely aborted. Share that
            // one call rather than accumulate blocked worker threads on clicks.
            if (_inFlight is null || _inFlight.IsCompleted)
                _inFlight = Task.Run(() =>
                {
                    try { return _query(); }
                    catch { return RecycleBinSnapshot.Unavailable; }
                });
            query = _inFlight;
        }
        try { return await query.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); }
        catch (TimeoutException) { return RecycleBinSnapshot.Unavailable; }
    }

    private static RecycleBinSnapshot QueryNative()
    {
        if (!OperatingSystem.IsWindows()) return RecycleBinSnapshot.Unavailable;
        var info = new ShellQueryRecycleBinInfo { Size = (uint)Marshal.SizeOf<ShellQueryRecycleBinInfo>() };
        // Microsoft documents null as aggregating all drives. SHQueryRecycleBinW
        // returns counts/bytes only; it does not enumerate, empty or restore files.
        // https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-shqueryrecyclebinw
        var result = SHQueryRecycleBinW(null, ref info);
        return result == 0 && info.ItemCount >= 0 && info.TotalBytes >= 0
            ? new(true, info.ItemCount, info.TotalBytes)
            : RecycleBinSnapshot.Unavailable;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct ShellQueryRecycleBinInfo
    {
        public uint Size;
        public long TotalBytes;
        public long ItemCount;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHQueryRecycleBinW(string? rootPath, ref ShellQueryRecycleBinInfo info);
}
