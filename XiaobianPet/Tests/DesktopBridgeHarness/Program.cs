using System.Reflection;
using System.Text.Json.Nodes;
using XiaobianPet.Models;
using XiaobianPet.Services;

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}

var parseTurns = typeof(CodexDesktopBridge).GetMethod("ParseTurns", BindingFlags.NonPublic | BindingFlags.Static)!;
var parsedTurns = (IReadOnlyList<CodexDesktopTurnSummary>)parseTurns.Invoke(null,
    [JsonNode.Parse("""[{"id":"current","status":"inProgress","items":[{"type":"userMessage","content":[{"type":"text","text":"继续之前的任务"}]},{"type":"fileChange","id":"patch","status":"completed","changes":[{"path":"src/app.cs"}]}]},{"id":"old","status":"completed","items":[]}]""")!.AsArray()])!;
Check(parsedTurns[0].ResumeRequested && parsedTurns[0].HasCodeEdit && !parsedTurns[1].ResumeRequested,
    "bridge must extract signals within each turn, never from historical turns");
Check(parsedTurns[0].CurrentActivityKey == "patch:fileChange:completed", "existing progress identity must be preserved");
Console.WriteLine("Task content parser: resume, code edit, turn isolation, existing progress compatibility PASS");

var fixture = Directory.CreateTempSubdirectory("pet-bridge-test-");
try
{
    Check(CodexDesktopInstallation.FindCachedPluginDirectory(null) is null, "missing home must be safe");
    Check(CodexDesktopInstallation.FindCachedPluginDirectory(fixture.FullName) is null, "empty cache must be safe");
    var cache = Path.Combine(fixture.FullName, "plugins", "cache", "openai-bundled", "codex-app-tools");
    foreach (var version in new[] { "0.9.0", "0.10.0", "0.11.0" })
    {
        var directory = Path.Combine(cache, version);
        Directory.CreateDirectory(Path.Combine(directory, "scripts"));
        File.WriteAllText(Path.Combine(directory, "server.mjs"), "// test fixture; never executed");
        if (version != "0.11.0")
            File.WriteAllText(Path.Combine(directory, "scripts", "launch_codex_app_tools_mcp.cmd"), "@exit /b 0");
    }
    Check(CodexDesktopInstallation.FindCachedPluginDirectory(fixture.FullName) == Path.Combine(cache, "0.10.0"),
        "select newest complete semantic version, not incomplete or lexicographically newest version");
    Check(CodexDesktopInstallation.IsAppToolsPipeCandidate(@"\\.\pipe\codex-browser-use\test"), "current pipe name missing");
    Check(CodexDesktopInstallation.IsAppToolsPipeCandidate(@"\\.\pipe\codex-browser-use-test"), "legacy pipe name missing");
    Check(!CodexDesktopInstallation.IsAppToolsPipeCandidate(@"\\.\pipe\codex-ipc"), "unrelated pipe accepted");
    Console.WriteLine("Plugin discovery: missing cache, numeric versions, incomplete install, current/legacy pipes PASS");
}
finally
{
    // Only remove this harness's freshly created fixture, never a user cache.
    fixture.Delete(recursive: true);
}

if (args.Contains("--probe", StringComparer.Ordinal))
{
    var anchor = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
    Check(!string.IsNullOrWhiteSpace(anchor), "live probe requires the executor's current thread ID");
    if (args.Contains("--without-executor-paths", StringComparer.Ordinal))
    {
        // Child-process-only simulation of launching the pet from Explorer.
        // Never change the user's saved environment or CODEX_HOME.
        Environment.SetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH", null);
        Environment.SetEnvironmentVariable("CODEX_MCP_NODE_PATH", null);
        Environment.SetEnvironmentVariable("CODEX_ELECTRON_RESOURCES_PATH", null);
    }
    var discover = typeof(CodexDesktopBridge).GetMethod("DiscoverEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!;
    var endpoints = ((System.Collections.IEnumerable)discover.Invoke(null, null)!).Cast<object>().ToArray();
    Check(endpoints.Length > 0, "no live endpoint discovered");
    Console.WriteLine($"Live endpoint discovery: {endpoints.Length} candidates");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    await using var bridge = new CodexDesktopBridge { RequestTimeout = TimeSpan.FromSeconds(5) };
    for (var round = 0; round < 2; round++)
    {
        // Only list summaries; never read a task or submit messages/changes.
        await bridge.ConnectAsync([anchor!], timeout.Token);
        var tasks = await bridge.ListTasksAsync(1, timeout.Token);
        Check(bridge.IsConnected && bridge.LastError is null, "bridge did not become healthy");
        Console.WriteLine($"Live {(round == 0 ? "connection" : "reconnection")}: connected=True, error=None, summaryCount={tasks.Count} PASS");
    }
}
