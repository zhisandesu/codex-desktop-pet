using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using XiaobianPet.Models;

namespace XiaobianPet.Services;

public sealed class CodexProgressTracker : IDisposable
{
    private const int MaxActivities = 8;
    private static readonly TimeSpan DeltaPublishInterval = TimeSpan.FromMilliseconds(250);

    private readonly CodexAppServerClient _client;
    private readonly object _gate = new();
    private readonly List<CodexPlanStepProgress> _planSteps = [];
    private readonly List<CodexActivityProgress> _activities = [];
    private readonly StringBuilder _reasoningSummary = new();
    private readonly Dictionary<string, StringBuilder> _agentMessages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingApprovalActivities = new(StringComparer.Ordinal);

    private string? _threadId;
    private string? _turnId;
    private CodexTaskProgressState _state = CodexTaskProgressState.Idle;
    private bool _isVisible;
    private bool _isActive;
    private string _statusText = "等待任务";
    private string _currentOperation = "等待你的要求";
    private DateTime _lastDeltaPublishedUtc = DateTime.MinValue;
    private bool _disposed;

    public CodexProgressTracker(CodexAppServerClient client)
    {
        _client = client;
        _client.NotificationReceived += Client_NotificationReceived;
        _client.TurnCompleted += Client_TurnCompleted;
        _client.ApprovalRequested += Client_ApprovalRequested;
    }

    public event EventHandler<CodexTaskProgressSnapshot>? ProgressChanged;

    public CodexTaskProgressSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return BuildSnapshot();
            }
        }
    }

    public void BindTask(string threadId, string? turnId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        lock (_gate)
        {
            _threadId = threadId;
            _turnId = string.IsNullOrWhiteSpace(turnId) ? null : turnId;
        }
    }

    private void Client_NotificationReceived(object? sender, CodexNotification notification)
    {
        var shouldPublish = false;
        lock (_gate)
        {
            if (!MatchesCurrentTask(notification.Params, notification.Method == "turn/started"))
            {
                return;
            }

            switch (notification.Method)
            {
                case "turn/started":
                    ResetForTurn(notification.Params);
                    shouldPublish = true;
                    break;
                case "turn/plan/updated":
                    ApplyPlan(notification.Params);
                    shouldPublish = true;
                    break;
                case "item/started":
                    ApplyItem(notification.Params, completedEvent: false);
                    shouldPublish = true;
                    break;
                case "item/completed":
                    ApplyItem(notification.Params, completedEvent: true);
                    shouldPublish = true;
                    break;
                case "item/reasoning/summaryTextDelta":
                    ApplyReasoningDelta(notification.Params);
                    shouldPublish = ShouldPublishDelta();
                    break;
                case "item/agentMessage/delta":
                    ApplyAgentMessageDelta(notification.Params);
                    shouldPublish = ShouldPublishDelta();
                    break;
                case "item/commandExecution/outputDelta":
                    ApplyItemDelta(notification.Params, "命令输出");
                    shouldPublish = ShouldPublishDelta();
                    break;
                case "item/mcpToolCall/progress":
                    ApplyItemDelta(notification.Params, "工具进度", "message");
                    shouldPublish = ShouldPublishDelta();
                    break;
                case "item/fileChange/patchUpdated":
                    ApplyPatchUpdated(notification.Params);
                    shouldPublish = ShouldPublishDelta();
                    break;
                case "item/plan/delta":
                    _isVisible = true;
                    _isActive = true;
                    _state = CodexTaskProgressState.Running;
                    _statusText = "正在制定计划";
                    _currentOperation = "Codex 正在拆分任务步骤…";
                    shouldPublish = ShouldPublishDelta();
                    break;
                case "turn/diff/updated":
                    UpsertSyntheticActivity("turn-diff", "文件", "更新文件改动预览", "已生成最新差异", true);
                    shouldPublish = true;
                    break;
                case "hook/started":
                    UpsertSyntheticActivity(
                        HookId(notification.Params),
                        "钩子",
                        HookTitle(notification.Params),
                        string.Empty,
                        false);
                    shouldPublish = true;
                    break;
                case "hook/completed":
                    CompleteHook(notification.Params);
                    shouldPublish = true;
                    break;
                case "serverRequest/resolved":
                    if (CompleteResolvedApproval(notification.Params) && _isActive)
                    {
                        _state = CodexTaskProgressState.Running;
                        _statusText = PlanStatusText();
                        _currentOperation = CurrentPlanOperation("Codex 正在继续任务…");
                        shouldPublish = true;
                    }

                    break;
                case "thread/status/changed":
                    shouldPublish = ApplyThreadStatus(notification.Params);
                    break;
                case "error":
                    ApplyError(notification.Params);
                    shouldPublish = true;
                    break;
                case "model/safetyBuffering/updated":
                    _isVisible = true;
                    _statusText = "正在进行安全检查";
                    _currentOperation = "模型响应暂时缓冲中…";
                    shouldPublish = true;
                    break;
                case "model/rerouted":
                    UpsertSyntheticActivity(
                        "model-rerouted",
                        "模型",
                        "切换执行模型",
                        $"{ReadString(notification.Params, "fromModel") ?? "未知"} → {ReadString(notification.Params, "toModel") ?? "未知"}",
                        true);
                    shouldPublish = true;
                    break;
            }
        }

        if (shouldPublish)
        {
            PublishSnapshot();
        }
    }

    private void Client_TurnCompleted(object? sender, CodexTurnCompleted completed)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(_threadId) &&
                !string.IsNullOrWhiteSpace(completed.ThreadId) &&
                !string.Equals(_threadId, completed.ThreadId, StringComparison.Ordinal))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_turnId) &&
                !string.IsNullOrWhiteSpace(completed.TurnId) &&
                !string.Equals(_turnId, completed.TurnId, StringComparison.Ordinal))
            {
                return;
            }

            _threadId = string.IsNullOrWhiteSpace(completed.ThreadId) ? _threadId : completed.ThreadId;
            _turnId = string.IsNullOrWhiteSpace(completed.TurnId) ? _turnId : completed.TurnId;
            _isVisible = true;
            _isActive = false;

            switch (completed.Status)
            {
                case "completed":
                    _state = CodexTaskProgressState.Completed;
                    _statusText = "任务完成";
                    _currentOperation = "全部工作已经结束";
                    break;
                case "interrupted":
                    _state = CodexTaskProgressState.Interrupted;
                    _statusText = "任务已停止";
                    _currentOperation = "已按要求停止当前任务";
                    break;
                default:
                    _state = CodexTaskProgressState.Failed;
                    _statusText = "任务失败";
                    _currentOperation = string.IsNullOrWhiteSpace(completed.ErrorMessage)
                        ? "Codex 遇到了问题"
                        : Shorten(completed.ErrorMessage, 150);
                    break;
            }
        }

        PublishSnapshot();
    }

    private void Client_ApprovalRequested(object? sender, CodexApprovalRequest request)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(_threadId) &&
                !string.IsNullOrWhiteSpace(request.ThreadId) &&
                !string.Equals(_threadId, request.ThreadId, StringComparison.Ordinal))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_turnId) &&
                !string.IsNullOrWhiteSpace(request.TurnId) &&
                !string.Equals(_turnId, request.TurnId, StringComparison.Ordinal))
            {
                return;
            }

            _isVisible = true;
            _isActive = true;
            _state = CodexTaskProgressState.Waiting;
            _statusText = "等待你的确认";
            _currentOperation = request.Title;
            var activityId = $"approval:{request.RequestId.ToJsonString()}";
            _pendingApprovalActivities.Add(activityId);
            UpsertSyntheticActivity(
                activityId,
                "确认",
                request.Title,
                Shorten(request.Detail, 120),
                false,
                "等待确认");
        }

        PublishSnapshot();
    }

    private bool MatchesCurrentTask(JsonObject parameters, bool allowNewTurn)
    {
        var threadId = ReadString(parameters, "threadId", "thread_id");
        var turnId = ReadString(parameters, "turnId", "turn_id")
                     ?? ReadNestedString(parameters, "turn", "id");

        if (!string.IsNullOrWhiteSpace(_threadId) &&
            !string.IsNullOrWhiteSpace(threadId) &&
            !string.Equals(_threadId, threadId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!allowNewTurn &&
            !string.IsNullOrWhiteSpace(_turnId) &&
            !string.IsNullOrWhiteSpace(turnId) &&
            !string.Equals(_turnId, turnId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private void ResetForTurn(JsonObject parameters)
    {
        _threadId = ReadString(parameters, "threadId", "thread_id") ?? _client.CurrentThreadId;
        _turnId = ReadNestedString(parameters, "turn", "id")
                  ?? ReadString(parameters, "turnId", "turn_id")
                  ?? _client.CurrentTurnId;
        _state = CodexTaskProgressState.Running;
        _isVisible = true;
        _isActive = true;
        _statusText = "任务已开始";
        _currentOperation = "正在理解任务要求…";
        _planSteps.Clear();
        _activities.Clear();
        _reasoningSummary.Clear();
        _agentMessages.Clear();
        _pendingApprovalActivities.Clear();
        _lastDeltaPublishedUtc = DateTime.MinValue;
    }

    private void ApplyPlan(JsonObject parameters)
    {
        _turnId ??= ReadString(parameters, "turnId", "turn_id");
        _isVisible = true;
        _isActive = true;
        _state = CodexTaskProgressState.Running;
        _planSteps.Clear();

        if (parameters["plan"] is JsonArray plan)
        {
            foreach (var node in plan)
            {
                if (node is not JsonObject stepObject)
                {
                    continue;
                }

                var step = ReadString(stepObject, "step", "text") ?? "未命名步骤";
                var status = NormalizePlanStatus(ReadString(stepObject, "status"));
                _planSteps.Add(new CodexPlanStepProgress(step, status.Label, status.Icon));
            }
        }

        _statusText = PlanStatusText();
        _currentOperation = CurrentPlanOperation(
            ReadString(parameters, "explanation") ?? "正在按计划执行…");
    }

    private void ApplyItem(JsonObject parameters, bool completedEvent)
    {
        if (parameters["item"] is not JsonObject item)
        {
            return;
        }

        _threadId ??= ReadString(parameters, "threadId", "thread_id");
        _turnId ??= ReadString(parameters, "turnId", "turn_id");
        var type = ReadString(item, "type") ?? "unknown";
        var id = ReadString(item, "id") ?? $"{type}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        if (type == "agentMessage")
        {
            var authoritativeText = ReadString(item, "text");
            if (!_agentMessages.TryGetValue(id, out var message))
            {
                message = new StringBuilder();
                _agentMessages[id] = message;
            }

            if (!string.IsNullOrEmpty(authoritativeText) && (completedEvent || message.Length == 0))
            {
                message.Clear();
                message.Append(authoritativeText);
            }
        }

        var description = DescribeItem(item, type);
        if (description is null)
        {
            return;
        }

        _isVisible = true;
        _isActive = true;
        if (_state != CodexTaskProgressState.Waiting)
        {
            _state = CodexTaskProgressState.Running;
        }

        var finalStatus = completedEvent ? NormalizeItemStatus(ReadString(item, "status")) : ("进行中", "●");
        var activity = new CodexActivityProgress(
            id,
            description.Value.Kind,
            description.Value.Summary,
            description.Value.Detail,
            finalStatus.Item1,
            finalStatus.Item2,
            DateTimeOffset.Now);
        UpsertActivity(activity);

        if (completedEvent)
        {
            _statusText = PlanStatusText();
            _currentOperation = FindRunningActivity()?.Summary
                                ?? CurrentPlanOperation("正在整理下一步…");
        }
        else
        {
            _statusText = description.Value.Kind switch
            {
                "分析" => "正在分析",
                "命令" => "正在执行命令",
                "文件" => "正在处理文件",
                "搜索" => "正在搜索",
                "工具" => "正在调用工具",
                "协作" => "正在协作",
                "汇报" => "正在汇报进展",
                "回复" => "正在整理回复",
                _ => "任务进行中"
            };
            _currentOperation = description.Value.Summary;
        }
    }

    private void ApplyReasoningDelta(JsonObject parameters)
    {
        var delta = ReadString(parameters, "delta", "text");
        if (string.IsNullOrWhiteSpace(delta))
        {
            return;
        }

        _reasoningSummary.Append(delta);
        if (_reasoningSummary.Length > 500)
        {
            _reasoningSummary.Remove(0, _reasoningSummary.Length - 500);
        }

        _isVisible = true;
        _isActive = true;
        _state = CodexTaskProgressState.Running;
        _statusText = "正在分析";
        _currentOperation = $"分析：{Shorten(CollapseWhitespace(_reasoningSummary.ToString()), 150)}";
    }

    private void ApplyAgentMessageDelta(JsonObject parameters)
    {
        var delta = ReadString(parameters, "delta", "text");
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        var itemId = ReadString(parameters, "itemId", "item_id") ?? "agent-message";
        if (!_agentMessages.TryGetValue(itemId, out var message))
        {
            message = new StringBuilder();
            _agentMessages[itemId] = message;
        }

        message.Append(delta);
        if (message.Length > 800)
        {
            message.Remove(0, message.Length - 800);
        }

        var text = Shorten(CollapseWhitespace(message.ToString()), 150);
        var index = FindActivityIndex(itemId);
        if (index >= 0)
        {
            var existing = _activities[index];
            _activities[index] = existing with { Detail = text, UpdatedAt = DateTimeOffset.Now };
            _statusText = existing.Kind switch
            {
                "汇报" => "正在汇报进展",
                "回复" => "正在整理回复",
                _ => "正在回复"
            };
            _currentOperation = string.IsNullOrWhiteSpace(text) ? existing.Summary : text;
        }
        else
        {
            _statusText = "正在汇报进展";
            _currentOperation = text;
        }

        _isVisible = true;
        _isActive = true;
        _state = CodexTaskProgressState.Running;
    }

    private void ApplyItemDelta(JsonObject parameters, string fallback, string propertyName = "delta")
    {
        var itemId = ReadString(parameters, "itemId", "item_id");
        var delta = ReadString(parameters, propertyName, "delta", "text");
        var index = FindActivityIndex(itemId);
        if (index >= 0)
        {
            var existing = _activities[index];
            var detail = string.IsNullOrWhiteSpace(delta)
                ? fallback
                : Shorten(LastNonEmptyLine(delta), 120);
            _activities[index] = existing with { Detail = detail, UpdatedAt = DateTimeOffset.Now };
            _currentOperation = existing.Summary;
        }
        else
        {
            _currentOperation = fallback;
        }
    }

    private void ApplyPatchUpdated(JsonObject parameters)
    {
        var itemId = ReadString(parameters, "itemId", "item_id");
        var count = parameters["changes"] is JsonArray changes ? changes.Count : 0;
        var index = FindActivityIndex(itemId);
        if (index >= 0)
        {
            var existing = _activities[index];
            _activities[index] = existing with
            {
                Detail = count == 0 ? "正在更新改动预览" : $"当前包含 {count} 项文件改动",
                UpdatedAt = DateTimeOffset.Now
            };
            _currentOperation = existing.Summary;
        }
    }

    private bool CompleteResolvedApproval(JsonObject parameters)
    {
        var requestId = parameters["requestId"];
        if (requestId is null)
        {
            return false;
        }

        var activityId = $"approval:{requestId.ToJsonString()}";
        if (!_pendingApprovalActivities.Remove(activityId))
        {
            return false;
        }

        var index = FindActivityIndex(activityId);
        if (index >= 0)
        {
            var existing = _activities[index];
            _activities[index] = existing with
            {
                Status = "已处理",
                Icon = "✓",
                UpdatedAt = DateTimeOffset.Now
            };
        }

        return true;
    }

    private void ApplyError(JsonObject parameters)
    {
        var error = parameters["error"] as JsonObject;
        var message = error is null
            ? ReadString(parameters, "message", "detail")
            : ReadString(error, "message", "detail");
        var willRetry = ReadBoolean(parameters, "willRetry", "will_retry")
                        ?? (error is null ? null : ReadBoolean(error, "willRetry", "will_retry"))
                        ?? false;

        _isVisible = true;
        _isActive = true;
        _state = CodexTaskProgressState.Running;
        _statusText = willRetry ? "连接波动，正在重试" : "任务遇到错误";
        _currentOperation = string.IsNullOrWhiteSpace(message)
            ? (willRetry ? "Codex 正在自动重试…" : "等待任务返回最终状态…")
            : Shorten(message, 150);
        UpsertSyntheticActivity(
            $"error:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            "错误",
            _statusText,
            _currentOperation,
            false,
            willRetry ? "重试中" : "待确认");
        _currentOperation = string.IsNullOrWhiteSpace(message)
            ? (willRetry ? "Codex 正在自动重试…" : "等待任务返回最终状态…")
            : Shorten(message, 150);
    }

    private bool ApplyThreadStatus(JsonObject parameters)
    {
        if (parameters["status"] is not JsonObject status)
        {
            return false;
        }

        var activeFlags = status["activeFlags"] as JsonArray;
        var waitingApproval = activeFlags?.Any(node =>
            string.Equals(NodeAsString(node), "waitingOnApproval", StringComparison.OrdinalIgnoreCase)) == true;
        var waitingInput = activeFlags?.Any(node =>
            string.Equals(NodeAsString(node), "waitingOnUserInput", StringComparison.OrdinalIgnoreCase)) == true;

        if (waitingApproval || waitingInput)
        {
            _isVisible = true;
            _isActive = true;
            _state = CodexTaskProgressState.Waiting;
            _statusText = waitingInput ? "等待你的输入" : "等待你的确认";
            _currentOperation = waitingInput
                ? "Codex 需要你补充信息后才能继续"
                : "Codex 需要你确认一项操作";
            return true;
        }

        var type = ReadString(status, "type");
        if (_state == CodexTaskProgressState.Waiting &&
            string.Equals(type, "active", StringComparison.OrdinalIgnoreCase))
        {
            _state = CodexTaskProgressState.Running;
            _isActive = true;
            _statusText = PlanStatusText();
            _currentOperation = CurrentPlanOperation("Codex 正在继续任务…");
            return true;
        }

        return false;
    }

    private (string Kind, string Summary, string Detail)? DescribeItem(JsonObject item, string type)
    {
        switch (type)
        {
            case "userMessage":
                return null;
            case "reasoning":
                return ("分析", "分析任务与代码", "正在生成可读的分析摘要");
            case "plan":
                return ("计划", "制定任务计划", Shorten(ReadString(item, "text") ?? string.Empty, 120));
            case "agentMessage":
            {
                var text = Shorten(ReadString(item, "text") ?? string.Empty, 150);
                return ReadString(item, "phase")?.ToLowerInvariant() switch
                {
                    "commentary" => ("汇报", "汇报阶段进展", text),
                    "final_answer" => ("回复", "整理最终回复", text),
                    _ => ("消息", "Codex 正在回复", text)
                };
            }
            case "commandExecution":
            {
                var command = FormatNode(item["command"]);
                var cwd = ReadString(item, "cwd") ?? string.Empty;
                var exitCode = ReadString(item, "exitCode");
                var detail = string.IsNullOrWhiteSpace(exitCode)
                    ? cwd
                    : $"退出码 {exitCode}{(string.IsNullOrWhiteSpace(cwd) ? string.Empty : $" · {cwd}")}";
                return ("命令", $"执行：{Shorten(command, 110)}", Shorten(detail, 120));
            }
            case "fileChange":
            {
                var changes = item["changes"] as JsonArray;
                var count = changes?.Count ?? 0;
                var firstPath = changes?.OfType<JsonObject>()
                    .Select(change => ReadString(change, "path"))
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
                return ("文件", $"处理 {count} 项文件改动", Shorten(firstPath ?? string.Empty, 120));
            }
            case "mcpToolCall":
            {
                var app = ReadNestedString(item, "appContext", "appName")
                          ?? ReadString(item, "server")
                          ?? "MCP";
                var tool = ReadString(item, "tool") ?? "工具";
                return ("工具", $"调用 {app}：{tool}", string.Empty);
            }
            case "dynamicToolCall":
                return ("工具", $"调用工具：{ReadString(item, "tool") ?? "未知工具"}", string.Empty);
            case "collabToolCall":
            case "collabAgentToolCall":
                return ("协作", $"协作任务：{ReadString(item, "tool") ?? "代理协作"}", string.Empty);
            case "webSearch":
            {
                var query = ReadString(item, "query")
                            ?? ReadNestedString(item, "action", "query")
                            ?? "检索资料";
                return ("搜索", $"搜索：{Shorten(query, 110)}", string.Empty);
            }
            case "imageView":
                return ("图片", $"查看图片：{Shorten(ReadString(item, "path") ?? "图片", 100)}", string.Empty);
            case "enteredReviewMode":
                return ("审查", "开始代码审查", ReadString(item, "review") ?? string.Empty);
            case "exitedReviewMode":
                return ("审查", "完成代码审查", string.Empty);
            case "contextCompaction":
                return ("整理", "整理对话上下文", string.Empty);
            default:
                return ("操作", $"执行 {type}", string.Empty);
        }
    }

    private static string HookId(JsonObject parameters) => $"hook:{
        ReadNestedString(parameters, "run", "id")
        ?? ReadString(parameters, "runId", "turnId")
        ?? "unknown"}";

    private static string HookTitle(JsonObject parameters)
    {
        var eventName = ReadNestedString(parameters, "run", "eventName")
                        ?? ReadString(parameters, "eventName");
        return string.IsNullOrWhiteSpace(eventName)
            ? "运行生命周期钩子"
            : $"运行钩子：{Shorten(eventName, 90)}";
    }

    private void CompleteHook(JsonObject parameters)
    {
        var id = HookId(parameters);
        var index = FindActivityIndex(id);
        if (index < 0)
        {
            return;
        }

        var run = parameters["run"] as JsonObject;
        var status = run is null ? null : ReadString(run, "status");
        var statusMessage = run is null ? null : ReadString(run, "statusMessage");
        var mapped = status?.ToLowerInvariant() switch
        {
            "failed" => ("失败", "✕"),
            "blocked" => ("已阻止", "!"),
            "stopped" => ("已停止", "–"),
            _ => ("已完成", "✓")
        };
        var existing = _activities[index];
        _activities[index] = existing with
        {
            Status = mapped.Item1,
            Icon = mapped.Item2,
            Detail = Shorten(statusMessage ?? existing.Detail, 120),
            UpdatedAt = DateTimeOffset.Now
        };
        _currentOperation = CurrentPlanOperation("正在继续任务…");
    }

    private void UpsertActivity(CodexActivityProgress activity)
    {
        var index = FindActivityIndex(activity.Id);
        if (index >= 0)
        {
            _activities.RemoveAt(index);
        }

        _activities.Insert(0, activity);
        while (_activities.Count > MaxActivities)
        {
            _activities.RemoveAt(_activities.Count - 1);
        }
    }

    private void UpsertSyntheticActivity(
        string id,
        string kind,
        string summary,
        string detail,
        bool completed,
        string? explicitStatus = null)
    {
        var status = explicitStatus ?? (completed ? "已完成" : "进行中");
        var icon = status switch
        {
            "已完成" => "✓",
            "等待确认" => "!",
            _ => "●"
        };
        UpsertActivity(new CodexActivityProgress(id, kind, summary, detail, status, icon, DateTimeOffset.Now));
        _isVisible = true;
        _currentOperation = summary;
    }

    private void CompleteSyntheticActivity(string id)
    {
        var index = FindActivityIndex(id);
        if (index < 0)
        {
            return;
        }

        var existing = _activities[index];
        _activities[index] = existing with { Status = "已完成", Icon = "✓", UpdatedAt = DateTimeOffset.Now };
        _currentOperation = CurrentPlanOperation("正在继续任务…");
    }

    private CodexActivityProgress? FindRunningActivity() =>
        _activities.FirstOrDefault(activity => activity.Status is "进行中" or "等待确认");

    private int FindActivityIndex(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return -1;
        }

        return _activities.FindIndex(activity => string.Equals(activity.Id, id, StringComparison.Ordinal));
    }

    private string PlanStatusText()
    {
        if (_planSteps.Count == 0)
        {
            return _isActive ? "任务进行中" : _statusText;
        }

        var completed = _planSteps.Count(step => step.Status == "已完成");
        return $"已完成 {completed}/{_planSteps.Count} 步";
    }

    private string CurrentPlanOperation(string fallback)
    {
        var current = _planSteps.FirstOrDefault(step => step.Status == "进行中")
                      ?? _planSteps.FirstOrDefault(step => step.Status == "待处理");
        return current is null ? fallback : current.Step;
    }

    private CodexTaskProgressSnapshot BuildSnapshot()
    {
        int? percent = null;
        if (_state == CodexTaskProgressState.Completed)
        {
            percent = 100;
        }
        else if (_planSteps.Count > 0)
        {
            var completed = _planSteps.Count(step => step.Status == "已完成");
            percent = (int)Math.Round(completed * 100d / _planSteps.Count);
        }

        var currentStep = CurrentPlanOperation(
            _state switch
            {
                CodexTaskProgressState.Completed => "所有步骤已完成",
                CodexTaskProgressState.Failed => "任务未能完成",
                CodexTaskProgressState.Interrupted => "任务已停止",
                _ when _planSteps.Count > 0 => "计划步骤已完成，正在收尾",
                _ => "等待 Codex 给出计划"
            });

        return new CodexTaskProgressSnapshot(
            _threadId,
            _turnId,
            _state,
            _isVisible,
            _isActive,
            _statusText,
            currentStep,
            _currentOperation,
            percent,
            percent is null ? "—" : $"{percent}%",
            _planSteps.ToArray(),
            _activities.ToArray());
    }

    private bool ShouldPublishDelta()
    {
        var now = DateTime.UtcNow;
        if (now - _lastDeltaPublishedUtc < DeltaPublishInterval)
        {
            return false;
        }

        _lastDeltaPublishedUtc = now;
        return true;
    }

    private void PublishSnapshot()
    {
        CodexTaskProgressSnapshot snapshot;
        lock (_gate)
        {
            snapshot = BuildSnapshot();
        }

        var handlers = ProgressChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<CodexTaskProgressSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Progress event handler failed: {ex}");
            }
        }
    }

    private static (string Label, string Icon) NormalizePlanStatus(string? status) =>
        status?.ToLowerInvariant() switch
        {
            "completed" => ("已完成", "✓"),
            "inprogress" or "in_progress" => ("进行中", "●"),
            _ => ("待处理", "○")
        };

    private static (string, string) NormalizeItemStatus(string? status) =>
        status?.ToLowerInvariant() switch
        {
            "failed" => ("失败", "✕"),
            "declined" or "cancelled" or "canceled" => ("已跳过", "–"),
            _ => ("已完成", "✓")
        };

    private static string FormatNode(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return string.Join(" ", array.Select(NodeAsString).Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return NodeAsString(node) ?? string.Empty;
    }

    private static string? ReadNestedString(JsonObject source, string property, params string[] nestedProperties) =>
        source[property] is JsonObject nested ? ReadString(nested, nestedProperties) : null;

    private static string? ReadString(JsonObject source, params string[] properties)
    {
        foreach (var property in properties)
        {
            var value = NodeAsString(source[property]);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static bool? ReadBoolean(JsonObject source, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (source[property] is JsonValue value && value.TryGetValue<bool>(out var result))
            {
                return result;
            }
        }

        return null;
    }

    private static string? NodeAsString(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return value.ToJsonString().Trim('"');
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string LastNonEmptyLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? value;

    private static string Shorten(string value, int maxLength)
    {
        var cleaned = CollapseWhitespace(value);
        return cleaned.Length <= maxLength ? cleaned : $"{cleaned[..Math.Max(1, maxLength - 1)]}…";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.NotificationReceived -= Client_NotificationReceived;
        _client.TurnCompleted -= Client_TurnCompleted;
        _client.ApprovalRequested -= Client_ApprovalRequested;
    }
}
