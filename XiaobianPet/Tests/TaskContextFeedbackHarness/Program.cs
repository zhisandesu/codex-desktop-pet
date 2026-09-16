using System.Text.Json.Nodes;
using XiaobianPet.Models;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
(bool ResumeRequested, bool HasCodeEdit) Parse(string json) =>
    PetTaskContextSignals.Read(JsonNode.Parse(json)!.AsArray());

Check(Parse("""[{"type":"userMessage","content":[{"type":"text","text":"继续之前的任务"}]}]""").ResumeRequested,
    "structured user content must detect resume");
Check(Parse("""[{"type":"userMessage","text":"请继续修复之前的问题"}]""").ResumeRequested,
    "plain user text must detect resume");
Check(!Parse("""[{"type":"agentMessage","text":"继续之前的任务"},{"type":"commandExecution","command":"echo 修改代码"}]""").ResumeRequested,
    "assistant and logs must not invent a resume request");
Check(!Parse("""[{"type":"userMessage","text":"不要继续之前的任务"}]""").ResumeRequested, "negative request");
Check(!Parse("""[{"type":"userMessage","content":[{"type":"image","text":"继续"}]}]""").ResumeRequested,
    "attachments are not instructions");
Check(Parse("""[{"type":"fileChange","status":"completed","changes":[{"path":"src/App.tsx"}]}]""").HasCodeEdit,
    "code patch signal");
Check(!Parse("""[{"type":"fileChange","changes":[{"path":"secret.env"},{"path":"README.md"}]},{"type":"commandExecution","command":"rg app.cs"}]""").HasCodeEdit,
    "configuration, documents and readonly shell are not code edits");
Check(!Parse("""[{"type":"fileChange","status":"failed","changes":[{"path":"app.cs"}]}]""").HasCodeEdit, "failed edit");
Check(Parse("""[{"type":"dynamicToolCall","tool":"apply_patch","arguments":"*** Update File: src/app.cs\nprivate-text-not-forwarded"}]""").HasCodeEdit, "patch tool");
Check(!Parse("""[{"type":"userMessage","text":{"unexpected":true}},{"type":"fileChange","changes":false}]""").HasCodeEdit,
    "malformed data stays silent");

CodexDesktopTaskDetail Task(string turnId, bool active = true, bool resume = true, bool edit = false) => new(
    "task", "do not speak this private title", "private summary", active ? "active" : "idle", "local", "fixture", 0,
    null, [], active ? "inProgress" : "completed", null, "activity", turnId,
    [new(turnId, active ? "inProgress" : "completed", null, "activity", null, null, null, null)
        { ResumeRequested = resume, HasCodeEdit = edit }]);

var policy = new PetTaskContextFeedbackPolicy();
void Observe(CodexDesktopTask task, long now, bool connected = true, bool enabled = true) =>
    policy.Observe([task], connected, enabled, now);
Observe(Task("old", edit: true), 0);
Check(policy.PendingCount == 0, "startup must not narrate history");
Observe(Task("new", edit: true), 1000);
Check(policy.PendingCount == 2, "active-to-active new turn gets both semantic events");
for (var now = 2000; now <= 5000; now += 1000) Observe(Task("new", edit: true) with { Summary = $"stream {now}" }, now);
Check(policy.PendingCount == 2, "polls and streaming text cannot repeat events");
Check(policy.TakeNext(5000, false) is null && policy.PendingCount == 2, "interaction protection preserves queue");
var resumeEvent = policy.TakeNext(6000, true)!;
Check(resumeEvent.Reaction == PetTaskContextReaction.Resumed, "resume priority");
Check(policy.TakeNext(22000, true) is null, "global speech cooldown");
Check(policy.TakeNext(31000, true)?.Reaction == PetTaskContextReaction.EditingCode, "delayed coding complaint");
Observe(Task("new", edit: true), 130000);
Check(policy.PendingCount == 0, "one coding reaction per turn");
Observe(Task("third", edit: true), 131000);
Observe(Task("third", active: false), 132000);
Check(policy.PendingCount == 0 && !policy.IsCurrent(resumeEvent, 132000), "completion invalidates pending feedback");
Observe(Task("fourth"), 133000, connected: false);
Observe(Task("fourth", edit: true), 134000);
Check(policy.PendingCount == 0, "reconnection baseline must be silent");
Observe(Task("fifth"), 135000, enabled: false);
Observe(Task("fifth", edit: true), 136000);
Check(policy.PendingCount == 0, "reenabling must not replay old signals");
Observe(Task("sixth"), 137000);
Check(policy.TakeNext(180000, true) is null, "stale acknowledgment expires");
policy.Observe([], true, true, 181000);
Check(policy.PendingCount == 0, "removed tasks cannot speak");

policy = new();
var summaryOnly = new CodexDesktopTask("task", "title", null, "active", "local", null, 0, null, [], null, null, null);
Observe(summaryOnly, 0);
Observe(Task("late-details", edit: true), 1000);
Check(policy.PendingCount == 0, "late startup enrichment cannot replay history");
Observe(Task("fresh", edit: true) with { ActiveFlags = ["inProgress"] }, 2000);
Check(policy.PendingCount == 2, "inProgress is not a blocking flag");
Observe(Task("fresh", edit: true) with { ActiveFlags = ["waitingOnApproval"] }, 3000);
Check(policy.PendingCount == 0, "approval removes optimistic feedback");
policy.Observe([], true, true, 4000);
Observe(Task("fresh", edit: true), 5000);
Check(policy.PendingCount == 0, "temporarily absent task must not repeat a turn's lines");
policy = new();
Observe(Task("started-before-pet", edit: false), 0);
Observe(Task("started-before-pet", edit: true), 1000);
Check(policy.PendingCount == 1 && policy.TakeNext(22000, true)?.Reaction == PetTaskContextReaction.EditingCode,
    "new code activity after startup may react without replaying the old resume prompt");

var stroke = new PetHeadStrokeGesture();
bool Move(long now, double x, double y = 0.25, bool head = true, bool pressed = false) =>
    stroke.Observe(now, x, y, head, pressed);
Check(!Move(0, .4) && !Move(140, .5) && !Move(280, .4) && Move(420, .5), "three hover strokes trigger");
Check(!Move(560, .4) && !Move(700, .5), "head pat cooldown");
stroke = new();
Check(!Move(0, .4) && !Move(140, .5) && !Move(280, .6) && !Move(420, .7), "single pass must not pat");
stroke = new();
Check(!Move(0, .4, pressed: true) && !Move(140, .5, pressed: true) && !Move(280, .4, pressed: true), "grab never pats");
stroke = new();
Check(!Move(0, .4) && !Move(140, .5) && !Move(280, .4, head: false) && !Move(420, .5), "leaving head resets");
stroke = new();
Check(!Move(0, .4) && !Move(140, .5) && !Move(1000, .4) && !Move(1140, .5), "paused strokes reset");
stroke = new();
Check(!Move(0, .4) && !Move(140, .5) && !Move(280, .4, .45) && !Move(420, .5, .45), "vertical departure resets");
Console.WriteLine($"Task context signals, deduplication, privacy, priority, expiry and head-hover gestures: {checks} checks PASS");
