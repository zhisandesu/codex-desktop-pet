using XiaobianPet.Models;
using XiaobianPet.Services;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine("PASS " + name);
}

Check(RecycleBinFeedback.CreateLine(new(true, 0, 0)).Contains("这么干净"), "empty is clean");
Check(RecycleBinFeedback.CreateLine(new(true, 1, 30)).Contains("1 项"), "one item is not empty");
Check(RecycleBinFeedback.CreateLine(new(true, 19, 30)).Contains("不算多"), "small count is factual");
Check(RecycleBinFeedback.CreateLine(new(true, 20, 30)).Contains("太多"), "twenty triggers full reaction");
Check(RecycleBinFeedback.CreateLine(new(true, 1, 1024L*1024*1024)).Contains("太多"), "one GiB triggers full reaction");
Check(!RecycleBinFeedback.CreateLine(RecycleBinSnapshot.Unavailable).Contains("干净"), "unavailable is never empty");
Check(RecycleBinFeedback.CreateLine(new(true, -1, 0)).Contains("暂时"), "invalid statistics rejected");
Check((await new RecycleBinStatisticsService(() => throw new IOException()).QueryAsync()).IsAvailable == false,
    "native failure stays unavailable");
var delayed = new TaskCompletionSource<RecycleBinSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
var calls = 0;
var service = new RecycleBinStatisticsService(() => { Interlocked.Increment(ref calls); return delayed.Task.GetAwaiter().GetResult(); });
var first = service.QueryAsync();
var second = service.QueryAsync();
await Task.Delay(50);
Check(calls == 1, "overlapping requests share one native query");
delayed.SetResult(new(true, 3, 90));
Check((await first).ItemCount == 3 && (await second).ItemCount == 3, "both callers receive statistics");
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try { await service.QueryAsync(cancelled.Token); throw new Exception("Cancellation swallowed"); }
catch (OperationCanceledException) { Check(true, "caller cancellation preserved"); }
var timeoutGate = new TaskCompletionSource<RecycleBinSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
var timeoutService = new RecycleBinStatisticsService(() => timeoutGate.Task.GetAwaiter().GetResult());
Check(!(await timeoutService.QueryAsync()).IsAvailable, "native timeout yields unavailable");
timeoutGate.SetResult(new(true, 1, 1));

var policy = new RecycleBinReactionPolicy();
Check(!policy.TryReserveAutomatic(0, true, 0), "no automatic speech at startup");
Check(!policy.TryReserveAutomatic(599999, true, 0), "automatic minimum ten-minute interval");
Check(!policy.TryReserveAutomatic(600000, false, 0), "busy state blocks automatic check");
Check(policy.TryReserveAutomatic(700000, true, 0), "idle becomes eligible after delay");
Check(!policy.TryReserveAutomatic(700001, true, 0), "no burst on repeated idle boundaries");
policy.MarkPresented(800000, 0);
Check(!policy.TryReserveAutomatic(1399999, true, 0), "manual check postpones next automatic check");
Check(policy.TryReserveAutomatic(1400000, true, 1), "next opportunity reserves jittered delay");
Check(!policy.TryReserveAutomatic(2179999, true, 0), "three-minute jitter respected");
Check(policy.TryReserveAutomatic(2180000, true, 0), "jittered opportunity is finite");

if (args.Contains("--native"))
{
    var native = await new RecycleBinStatisticsService().QueryAsync();
    Check(native.IsAvailable && native.ItemCount >= 0 && native.TotalBytes >= 0,
        "real Windows aggregate query succeeds (no paths or contents)");
    Console.WriteLine("NATIVE_FEEDBACK " + RecycleBinFeedback.CreateLine(native));
}
Console.WriteLine($"RECYCLE_BIN_FEEDBACK_OK {passed} checks");
