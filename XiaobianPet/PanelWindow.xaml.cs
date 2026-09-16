using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using XiaobianPet.Models;
using XiaobianPet.Services;

namespace XiaobianPet;

public partial class PanelWindow : Window
{
    private readonly MainWindow _petWindow;
    private readonly CodexAppServerClient _codex;
    private readonly CodexProgressTracker _progress;
    private readonly CodexDesktopBridge _desktopBridge;
    private readonly CodexDesktopTaskMonitor _desktopMonitor;
    private readonly SettingsStore _settingsStore;
    private readonly PetSettings _settings;
    private readonly ObservableCollection<CodexDesktopTask> _threads = [];
    private readonly StringBuilder _output = new();

    private CodexApprovalRequest? _approval;
    private string? _selectedThreadId;
    private bool _allowClose;
    private bool _isBusy;
    private bool _syncingTts;
    private bool _wasProgressActive;
    private bool _newTaskMode;
    private bool _voiceConversationActive;
    private bool _companionChatSending;
    private bool _characterHistoryLoaded;
    private CancellationTokenSource? _voiceConversationCts;

    public PanelWindow(
        MainWindow petWindow,
        CodexAppServerClient codex,
        CodexProgressTracker progress,
        CodexDesktopBridge desktopBridge,
        CodexDesktopTaskMonitor desktopMonitor,
        SettingsStore settingsStore,
        PetSettings settings)
    {
        InitializeComponent();
        Height = Math.Min(720, Math.Max(620, SystemParameters.WorkArea.Height - 20));
        _petWindow = petWindow;
        _codex = codex;
        _progress = progress;
        _desktopBridge = desktopBridge;
        _desktopMonitor = desktopMonitor;
        _settingsStore = settingsStore;
        _settings = settings;

        Owner = petWindow;
        ThreadListBox.ItemsSource = _threads;
        ProjectPathTextBox.Text = settings.LastCwd ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        SyncTtsEnabled(settings.TtsEnabled);

        _codex.ConnectionStateChanged += Codex_ConnectionStateChanged;
        _codex.AgentMessageDelta += Codex_AgentMessageDelta;
        _codex.TurnCompleted += Codex_TurnCompleted;
        _progress.ProgressChanged += Codex_ProgressChanged;
        _desktopMonitor.SnapshotChanged += DesktopMonitor_SnapshotChanged;
        TaskTabs.SelectionChanged += TaskTabs_SelectionChanged;
        ApplyDesktopSnapshot(_desktopMonitor.Snapshot);
        ApplyProgress(_progress.Snapshot);
        UpdateComposerVisibility();
        UpdateCompanionChatAvailability();
    }

    public async void ShowNearPet()
    {
        ApplyProgress(_progress.Snapshot);
        WorkStatusExpander.IsExpanded = false;
        UpdateComposerVisibility();
        FollowPet();
        if (!IsVisible)
        {
            Show();
        }

        Activate();
        CompanionChatInput.Focus();

        await EnsureCharacterHistoryLoadedAsync();
        OutputTextBlock.Text = _output.Length == 0
            ? "柯朵已经准备好连接 Codex。"
            : _output.ToString();

        if (_threads.Count == 0)
        {
            await RefreshThreadsAsync();
        }
    }

    private async Task EnsureCharacterHistoryLoadedAsync()
    {
        if (_characterHistoryLoaded)
        {
            return;
        }

        _characterHistoryLoaded = true;
        try
        {
            var history = await _petWindow.ReadRecentCharacterConversationAsync(40);
            foreach (var entry in history)
            {
                AddCompanionMessage(
                    isUser: true,
                    entry.UserText,
                    entry.Timestamp,
                    entry.Source);
                AddCompanionMessage(
                    isUser: false,
                    entry.AssistantText,
                    entry.Timestamp,
                    detail: entry.MemoryChanges.Count == 0
                        ? null
                        : $"已记住：{string.Join("；", entry.MemoryChanges)}");
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.Security.Cryptography.CryptographicException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Character conversation history could not be loaded: {exception.GetType().Name}");
            AddCompanionSystemMessage("以前的聊天记录暂时没能打开，新消息仍会正常保存。");
        }
    }

    private async Task SendCompanionMessageAsync()
    {
        var userText = CompanionChatInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(userText) || _companionChatSending || _voiceConversationActive)
        {
            return;
        }

        await EnsureCharacterHistoryLoadedAsync();
        CompanionChatInput.Clear();
        AddCompanionMessage(
            isUser: true,
            userText,
            DateTimeOffset.Now,
            PetInputSource.Text);
        SetCompanionChatBusy(true, "柯朵在想…");

        try
        {
            NoticeBorder.Visibility = Visibility.Collapsed;
            var interaction = await _petWindow.SendCompanionChatAsync(userText);
            if (interaction is null)
            {
                throw new InvalidOperationException("这句话没有进入柯朵的房间。");
            }

            AddCompanionMessage(
                isUser: false,
                interaction.Reply,
                DateTimeOffset.Now,
                detail: interaction.Detail);
            CompanionChatStatusText.Text = "她听见啦";
        }
        catch (OperationCanceledException)
        {
            CompanionChatStatusText.Text = "这句话已取消";
        }
        catch (Exception exception)
        {
            var message = exception.GetBaseException().Message;
            AddCompanionSystemMessage($"刚才没送到：{message}");
            CompanionChatStatusText.Text = "发送失败，可以再试一次";
            if (string.IsNullOrWhiteSpace(CompanionChatInput.Text))
            {
                CompanionChatInput.Text = userText;
                CompanionChatInput.SelectAll();
            }

            App.WriteErrorLog("Companion chat failure", exception);
        }
        finally
        {
            SetCompanionChatBusy(false);
            CompanionChatInput.Focus();
        }
    }

    private void AddCompanionMessage(
        bool isUser,
        string text,
        DateTimeOffset timestamp,
        PetInputSource? source = null,
        string? detail = null)
    {
        var content = text.Trim();
        if (content.Length == 0)
        {
            return;
        }

        CompanionEmptyState.Visibility = Visibility.Collapsed;
        var message = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(2, 2, 2, 10)
        };
        var sourceLabel = source == PetInputSource.Voice ? " · 语音" : string.Empty;
        message.Children.Add(new TextBlock
        {
            Text = $"{timestamp.ToLocalTime():HH:mm}{sourceLabel}",
            Margin = new Thickness(0, 0, 0, 12),
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(137, 129, 150)),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var bubbleContent = new StackPanel();
        bubbleContent.Children.Add(new TextBlock
        {
            Text = content,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 20,
            Foreground = new SolidColorBrush(isUser
                ? Color.FromRgb(75, 62, 112)
                : Color.FromRgb(64, 56, 79))
        });
        if (!string.IsNullOrWhiteSpace(detail))
        {
            bubbleContent.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 8, 0, 7),
                Background = new SolidColorBrush(Color.FromArgb(48, 116, 98, 217))
            });
            bubbleContent.Children.Add(new TextBlock
            {
                Text = detail.Trim(),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 10,
                LineHeight = 16,
                Foreground = new SolidColorBrush(Color.FromRgb(102, 88, 129))
            });
        }

        message.Children.Add(new Border
        {
            Padding = new Thickness(13, 10, 13, 10),
            CornerRadius = new CornerRadius(13),
            MinHeight = isUser ? 60 : 50,
            MaxWidth = isUser ? 300 : 215,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = new SolidColorBrush(isUser
                ? Color.FromRgb(238, 233, 255)
                : Color.FromRgb(248, 246, 255)),
            BorderBrush = new SolidColorBrush(isUser
                ? Color.FromArgb(90, 116, 98, 217)
                : Color.FromArgb(72, 140, 118, 216)),
            BorderThickness = new Thickness(1),
            Child = bubbleContent
        });
        CompanionMessagesPanel.Children.Add(message);
        Dispatcher.BeginInvoke(new Action(CompanionChatScrollViewer.ScrollToEnd));
    }

    private void AddCompanionSystemMessage(string text)
    {
        CompanionEmptyState.Visibility = Visibility.Collapsed;
        CompanionMessagesPanel.Children.Add(new Border
        {
            Margin = new Thickness(36, 5, 36, 9),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromRgb(246, 243, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(62, 140, 118, 216)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(103, 91, 128))
            }
        });
        Dispatcher.BeginInvoke(new Action(CompanionChatScrollViewer.ScrollToEnd));
    }

    private void SetCompanionChatBusy(bool busy, string? status = null)
    {
        _companionChatSending = busy;
        CompanionChatInput.IsEnabled = !busy && !_voiceConversationActive;
        if (status is not null)
        {
            CompanionChatStatusText.Text = status;
        }

        UpdateCompanionChatAvailability();
    }

    private void UpdateCompanionChatAvailability()
    {
        CompanionSendButton.IsEnabled = !_companionChatSending &&
                                        !_voiceConversationActive &&
                                        !string.IsNullOrWhiteSpace(CompanionChatInput.Text);
        CompanionVoiceButton.IsEnabled = !_companionChatSending;
    }

    private void UpdateComposerVisibility()
    {
        var workStatusOpen = WorkStatusExpander.IsExpanded;
        CompanionConversationSurface.Visibility = workStatusOpen
            ? Visibility.Collapsed
            : Visibility.Visible;
        CompanionComposer.Visibility = workStatusOpen
            ? Visibility.Collapsed
            : Visibility.Visible;
        WorkWorkspace.Visibility = workStatusOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        TaskCommandComposer.Visibility = workStatusOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void WorkStatusExpander_Expanded(object sender, RoutedEventArgs e)
    {
        UpdateComposerVisibility();
        if (!IsVisible)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => PromptTextBox.Focus()));
    }

    private void WorkStatusExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        UpdateComposerVisibility();
        if (!IsVisible)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => CompanionChatInput.Focus()));
    }

    public void FollowPet()
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        var petBounds = _petWindow.GetPetViewportScreenBounds();

        var preferredLeft = petBounds.Left - Width - 8;
        if (preferredLeft < virtualLeft)
        {
            preferredLeft = petBounds.Right + 8;
        }

        Left = Math.Max(virtualLeft, Math.Min(preferredLeft, virtualRight - Width));
        Top = Math.Max(virtualTop, Math.Min(petBounds.Bottom - Height, virtualBottom - Height));
    }

    public void ShowApproval(CodexApprovalRequest request)
    {
        _approval = request;
        ApprovalTitleText.Text = request.Title;
        ApprovalDetailText.Text = request.Detail;
        ApprovalDetailText.ToolTip = request.Detail;
        ApprovalBorder.Visibility = Visibility.Visible;
        TaskStatusText.Text = "等待你的确认";
        WorkStatusText.Text = "工作状态 · 1 项待确认";
        WorkStatusExpander.IsExpanded = true;
        TaskTabs.SelectedItem = ProgressTab;
    }

    public void ShowStartupError(string message)
    {
        ShowNotice(message, true);
        AppendOutput($"连接错误：{message}");
    }

    public void AllowClose() => _allowClose = true;

    public void SyncTtsEnabled(bool enabled)
    {
        _syncingTts = true;
        try
        {
            TtsCheckBox.IsChecked = enabled;
        }
        finally
        {
            _syncingTts = false;
        }
    }

    private async Task RefreshThreadsAsync()
    {
        try
        {
            SetBusy(true, "正在读取任务…");
            await _desktopMonitor.RefreshAsync();
            ApplyDesktopSnapshot(_desktopMonitor.Snapshot);
        }
        catch (Exception ex)
        {
            ShowNotice($"读取任务失败：{ex.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SendPromptAsync()
    {
        var prompt = PromptTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            ShowNotice("先告诉柯朵要让 Codex 做什么。", false);
            return;
        }

        var forceCodex = PetCommandRouter.TryStripCodexEscape(prompt, out var escapedPrompt);
        if (forceCodex)
        {
            prompt = escapedPrompt;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                ShowNotice("在 /codex 后面写上要发送给 Codex 的内容。", false);
                return;
            }
        }
        else if (PetCommandRouter.LooksLikePetAddress(prompt))
        {
            try
            {
                NoticeBorder.Visibility = Visibility.Collapsed;
                SetBusy(true, "柯朵在想…");
                await EnsureCharacterHistoryLoadedAsync();
                AddCompanionMessage(
                    isUser: true,
                    prompt,
                    DateTimeOffset.Now,
                    PetInputSource.Text);
                var interaction = await _petWindow.TryHandlePetInputAsync(prompt);
                if (interaction is not null)
                {
                    AddCompanionMessage(
                        isUser: false,
                        interaction.Reply,
                        DateTimeOffset.Now,
                        detail: interaction.Detail);
                    PromptTextBox.Clear();
                    TaskStatusText.Text = "柯朵听见啦";
                    CompanionChatStatusText.Text = "她听见啦";
                    WorkStatusExpander.IsExpanded = false;
                    return;
                }

                AddCompanionSystemMessage("这句话没有送进柯朵的房间，请再试一次。");
                ShowNotice("这句话没有送进柯朵的房间。", true);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                ShowNotice($"柯朵刚才走神了：{ex.GetBaseException().Message}", true);
                return;
            }
            finally
            {
                SetBusy(false);
            }
        }

        if (!_newTaskMode && ThreadListBox.SelectedItem is not CodexDesktopTask)
        {
            ShowNotice("请先选择一个 Codex 任务，或点“新建”创建任务。", false);
            return;
        }

        if (!_newTaskMode && !_desktopBridge.IsConnected)
        {
            ShowNotice(_desktopBridge.LastError ?? "尚未连接 Codex 桌面任务，请稍后重试。", true);
            return;
        }

        try
        {
            NoticeBorder.Visibility = Visibility.Collapsed;
            SetBusy(true, "正在发送给 Codex…");
            if (_newTaskMode)
            {
                await StartLocalTaskAsync(prompt);
            }
            else if (ThreadListBox.SelectedItem is CodexDesktopTask selected)
            {
                await _desktopBridge.SendMessageAsync(selected.Id, selected.HostId, prompt);
                _desktopMonitor.AddTaskIdCandidate(selected.Id);
                AppendOutput($"\n你 → {TaskTitle(selected)}：{prompt}\n[已发送到 Codex 桌面任务]\n");
                PromptTextBox.Clear();
                TaskStatusText.Text = "指令已发送，等待 Codex 响应";
                _ = RefreshDesktopAfterSendAsync();
            }
        }
        catch (Exception ex)
        {
            ShowNotice($"发送失败：{ex.Message}", true);
            AppendOutput($"\n发送失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task StartLocalTaskAsync(string prompt)
    {
        if (!_codex.IsConnected)
        {
            throw new InvalidOperationException(_codex.LastError ?? "新建任务服务尚未连接。");
        }

        var cwd = ProjectPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
        {
            throw new InvalidOperationException("请选择一个存在的工作文件夹。");
        }

        _settings.LastCwd = cwd;
        await _settingsStore.SaveAsync(_settings);

        var targetThreadId = await _codex.StartThreadAsync(cwd);
        _selectedThreadId = targetThreadId;
        _desktopMonitor.AddTaskIdCandidate(targetThreadId);
        _progress.BindTask(targetThreadId, null);

        AppendOutput($"\n你（新任务）：{prompt}\n柯朵：");
        await _codex.StartTurnAsync(targetThreadId, prompt);
        PromptTextBox.Clear();
        _petWindow.NotifyTaskSubmitted();
        StopButton.IsEnabled = true;
        TaskStatusText.Text = "新任务正在执行";
        _newTaskMode = false;
        NewTaskExpander.IsExpanded = false;
        _selectedThreadId = targetThreadId;
        _ = RefreshDesktopAfterSendAsync();
    }

    private async Task RefreshDesktopAfterSendAsync()
    {
        try
        {
            await Task.Delay(700);
            await _desktopMonitor.RefreshAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Desktop task refresh after send failed: {ex}");
        }
    }

    private void DesktopMonitor_SnapshotChanged(object? sender, CodexDesktopMonitorSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ApplyDesktopSnapshot(snapshot));
            return;
        }

        ApplyDesktopSnapshot(snapshot);
    }

    private void ApplyDesktopSnapshot(CodexDesktopMonitorSnapshot snapshot)
    {
        var selectedId = _selectedThreadId;
        var ordered = snapshot.Tasks
            .OrderByDescending(IsDesktopTaskActive)
            .ThenByDescending(task => task.UpdatedAt)
            .ToArray();

        _threads.Clear();
        foreach (var task in ordered)
        {
            _threads.Add(task);
        }

        if (!_newTaskMode)
        {
            var selected = ordered.FirstOrDefault(task => task.Id == selectedId)
                           ?? (snapshot.PrimaryTask is null
                               ? null
                               : ordered.FirstOrDefault(task => task.Id == snapshot.PrimaryTask.Id))
                           ?? ordered.FirstOrDefault(IsDesktopTaskActive)
                           ?? ordered.FirstOrDefault();
            ThreadListBox.SelectedItem = selected;
            if (selected is not null)
            {
                ApplySelectedTask(selected);
            }
            else
            {
                _selectedThreadId = null;
                SelectedTaskText.Text = "尚未发现 Codex 任务";
                SelectedTaskDetailText.Text = "请先在 Codex 中打开一个任务";
            }
        }

        TaskCountText.Text = snapshot.IsConnected
            ? snapshot.WaitingCount > 0
                ? $"{snapshot.Tasks.Count} 个任务 · {snapshot.WaitingCount} 个需要你"
                : snapshot.ActiveCount > 0
                    ? $"{snapshot.Tasks.Count} 个任务 · {snapshot.ActiveCount} 个进行中"
                    : $"已连接 · {snapshot.Tasks.Count} 个任务"
            : snapshot.Tasks.Count > 0
                ? $"连接暂断 · 保留 {snapshot.Tasks.Count} 个任务"
                : "正在连接 Codex 任务…";

        WorkStatusText.Text = snapshot.IsConnected
            ? snapshot.WaitingCount > 0
                ? $"工作状态 · {snapshot.WaitingCount} 项待处理"
                : snapshot.ActiveCount > 0
                    ? $"工作状态 · {snapshot.ActiveCount} 项进行中"
                    : "工作状态 · 暂无进行中"
            : snapshot.Tasks.Count > 0
                ? "工作状态 · 连接暂断"
                : "工作状态 · 正在连接";

        var connectionDetail = snapshot.IsConnected
            ? snapshot.ActiveCount > 0 ? $"Codex 已连接 · {snapshot.ActiveCount} 个任务进行中" : "Codex 桌面任务已连接"
            : "Codex 桌面任务未连接";
        ConnectionText.Text = "陪你聊天";
        ConnectionDot.Fill = snapshot.IsConnected
            ? new SolidColorBrush(Color.FromRgb(86, 190, 151))
            : snapshot.Error is null ? Brushes.Goldenrod : Brushes.IndianRed;
        ConnectionText.ToolTip = snapshot.Error is null
            ? connectionDetail
            : $"{connectionDetail}：{snapshot.Error}";

        if (!snapshot.IsConnected && !string.IsNullOrWhiteSpace(snapshot.Error) && IsVisible)
        {
            TaskStatusText.Text = $"任务连接异常：{snapshot.Error}";
        }
        else if (!_wasProgressActive && snapshot.IsConnected)
        {
            TaskStatusText.Text = snapshot.WaitingCount > 0
                ? $"{snapshot.WaitingCount} 个任务需要你处理"
                : snapshot.ActiveCount > 0
                    ? $"Codex 正在执行 {snapshot.ActiveCount} 个任务"
                    : "可以选择任务并继续对话";
        }

        UpdateCommandAvailability();
    }

    private static bool IsDesktopTaskActive(CodexDesktopTask task) =>
        task.IsActive ||
        string.Equals(task.CurrentTurnStatus, "inProgress", StringComparison.OrdinalIgnoreCase) ||
        task.ActiveFlags.Any(flag =>
            flag.Contains("waiting", StringComparison.OrdinalIgnoreCase) ||
            flag.Contains("active", StringComparison.OrdinalIgnoreCase));

    private static string TaskTitle(CodexDesktopTask task) =>
        string.IsNullOrWhiteSpace(task.Title) ? $"任务 {task.Id[..Math.Min(8, task.Id.Length)]}" : task.Title.Trim();

    private static string DesktopStatusText(CodexDesktopTask task)
    {
        if (task.ActiveFlags.Any(flag => flag.Contains("Approval", StringComparison.OrdinalIgnoreCase)))
        {
            return "等待确认";
        }

        if (task.ActiveFlags.Any(flag => flag.Contains("Input", StringComparison.OrdinalIgnoreCase)))
        {
            return "等待输入";
        }

        return task.Status.ToLowerInvariant() switch
        {
            "active" => "进行中",
            "idle" => "已就绪",
            "notloaded" => "未加载",
            _ => string.IsNullOrWhiteSpace(task.Status) ? "状态未知" : task.Status
        };
    }

    private void ApplySelectedTask(CodexDesktopTask selected)
    {
        _selectedThreadId = selected.Id;
        SelectedTaskText.Text = $"继续：{TaskTitle(selected)}";
        SelectedTaskDetailText.Text = string.IsNullOrWhiteSpace(selected.CurrentOperation)
            ? $"{DesktopStatusText(selected)} · 可直接发送指令"
            : $"{DesktopStatusText(selected)} · {selected.CurrentOperation}";
        OpenTaskButton.IsEnabled = _desktopBridge.IsConnected;

        if (!_progress.Snapshot.IsActive)
        {
            var isDesktopTaskActive = IsDesktopTaskActive(selected);
            ProgressStatusText.Text = DesktopStatusText(selected);
            ProgressPercentText.Text = isDesktopTaskActive ? "监看中" : "—";
            ProgressPercentText.FontSize = isDesktopTaskActive ? 11 : 24;
            OverallProgressBar.IsIndeterminate = isDesktopTaskActive;
            OverallProgressBar.Value = 0;
            CurrentStepText.Text = TaskTitle(selected);
            CurrentOperationText.Text = selected.CurrentOperation ?? selected.Summary ?? "等待新的指令";
            PlanItemsControl.ItemsSource = null;
            ActivityItemsControl.ItemsSource = null;
        }

        UpdateCommandAvailability();
    }

    private void Codex_ConnectionStateChanged(object? sender, CodexConnectionState state)
    {
        Dispatcher.Invoke(() =>
        {
            if (_newTaskMode)
            {
                TaskStatusText.Text = state switch
                {
                    CodexConnectionState.Connected => "新任务服务已就绪",
                    CodexConnectionState.Connecting => "正在准备新任务服务",
                    CodexConnectionState.Error => "新任务服务连接异常",
                    _ => "新任务服务已断开"
                };
            }

            UpdateCommandAvailability();
        });
    }

    private void Codex_AgentMessageDelta(object? sender, string delta)
    {
        Dispatcher.Invoke(() =>
        {
            _output.Append(delta);
            if (IsVisible)
            {
                OutputTextBlock.Text = _output.ToString();
                OutputScrollViewer.ScrollToEnd();
            }
        });
    }

    private void Codex_TurnCompleted(object? sender, CodexTurnCompleted completed)
    {
        Dispatcher.Invoke(() =>
        {
            StopButton.IsEnabled = false;
            TaskStatusText.Text = completed.Status switch
            {
                "completed" => "任务完成",
                "interrupted" => "任务已停止",
                _ => "任务失败"
            };
            AppendOutput($"\n[{TaskStatusText.Text}]\n");
        });
    }

    private void Codex_ProgressChanged(object? sender, CodexTaskProgressSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() =>
            {
                if (IsVisible)
                {
                    ApplyProgress(snapshot);
                }
            });
            return;
        }

        if (IsVisible)
        {
            ApplyProgress(snapshot);
        }
    }

    private void ApplyProgress(CodexTaskProgressSnapshot snapshot)
    {
        ProgressStatusText.Text = snapshot.StatusText;
        ProgressPercentText.Text = snapshot.ProgressLabel;
        ProgressPercentText.FontSize = 24;
        OverallProgressBar.IsIndeterminate = snapshot.IsActive && snapshot.Percent is null;
        OverallProgressBar.Value = snapshot.Percent ?? 0;
        CurrentStepText.Text = snapshot.CurrentStep;
        CurrentOperationText.Text = snapshot.CurrentOperation;
        PlanItemsControl.ItemsSource = snapshot.PlanSteps;
        ActivityItemsControl.ItemsSource = snapshot.RecentActivities;

        if (snapshot.IsVisible)
        {
            TaskStatusText.Text = snapshot.StatusText;
        }

        StopButton.IsEnabled = snapshot.IsActive &&
                               _codex.IsConnected &&
                               !string.IsNullOrWhiteSpace(_selectedThreadId) &&
                               string.Equals(_codex.CurrentThreadId, _selectedThreadId, StringComparison.Ordinal);
        ThreadListBox.IsEnabled = true;
        RefreshButton.IsEnabled = !_isBusy;
        NewTaskButton.IsEnabled = !_isBusy;
        ProjectPathTextBox.IsEnabled = !_isBusy && _newTaskMode;
        ChooseFolderButton.IsEnabled = !_isBusy && _newTaskMode;
        if (snapshot.IsActive && !_wasProgressActive)
        {
            TaskTabs.SelectedItem = ProgressTab;
        }

        _wasProgressActive = snapshot.IsActive;
        UpdateCommandAvailability();
    }

    private void AppendOutput(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendOutput(text));
            return;
        }

        if (_output.Length == 0 && OutputTextBlock.Text == "柯朵已经准备好连接 Codex。")
        {
            OutputTextBlock.Text = string.Empty;
        }

        _output.Append(text);
        OutputTextBlock.Text = _output.ToString();
        OutputScrollViewer.ScrollToEnd();
    }

    private void ShowNotice(string message, bool isError)
    {
        NoticeText.Text = message;
        NoticeBorder.Background = isError
            ? new SolidColorBrush(Color.FromRgb(255, 239, 239))
            : new SolidColorBrush(Color.FromRgb(246, 243, 255));
        NoticeText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(147, 57, 57))
            : new SolidColorBrush(Color.FromRgb(98, 86, 129));
        NoticeBorder.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _isBusy = busy;
        RefreshButton.IsEnabled = !busy;
        NewTaskButton.IsEnabled = !busy;
        ProjectPathTextBox.IsEnabled = !busy && _newTaskMode;
        ChooseFolderButton.IsEnabled = !busy && _newTaskMode;
        UpdateCommandAvailability();
        if (status is not null)
        {
            TaskStatusText.Text = status;
        }
    }

    private void UpdateCommandAvailability()
    {
        var hasDesktopSelection = !_newTaskMode && ThreadListBox.SelectedItem is CodexDesktopTask;
        var isPetAddressed = PetCommandRouter.LooksLikePetAddress(PromptTextBox.Text);
        SendButton.IsEnabled = !_isBusy && !_voiceConversationActive &&
                               (isPetAddressed ||
                                (_newTaskMode
                                    ? _codex.IsConnected
                                    : hasDesktopSelection && _desktopBridge.IsConnected));
        SendButton.Content = isPetAddressed ? "和柯朵说" : "发送给 Codex";
        VoiceButton.IsEnabled = !_companionChatSending;
        OpenTaskButton.IsEnabled = !_isBusy && hasDesktopSelection && _desktopBridge.IsConnected;
        UpdateCapabilityPresentation();
    }

    private void UpdateCapabilityPresentation()
    {
        var selectedTask = ThreadListBox.SelectedItem as CodexDesktopTask;
        var selectedTaskId = selectedTask?.Id ?? _selectedThreadId;
        var localThreadId = _codex.CurrentThreadId;
        var progressSnapshot = _progress.Snapshot;
        var selectedMatchesLocal = !string.IsNullOrWhiteSpace(localThreadId) &&
                                   !string.IsNullOrWhiteSpace(selectedTaskId) &&
                                   string.Equals(localThreadId, selectedTaskId, StringComparison.Ordinal);
        var progressMatchesLocal = !string.IsNullOrWhiteSpace(localThreadId) &&
                                   !string.IsNullOrWhiteSpace(progressSnapshot.ThreadId) &&
                                   string.Equals(localThreadId, progressSnapshot.ThreadId, StringComparison.Ordinal);

        static SolidColorBrush Brush(byte red, byte green, byte blue) =>
            new(Color.FromRgb(red, green, blue));

        void ApplyChatCapabilityTheme(
            SolidColorBrush accent,
            SolidColorBrush background,
            SolidColorBrush border)
        {
            ChatCapabilityText.Foreground = accent;
            ChatCapabilityBorder.Background = background;
            ChatCapabilityBorder.BorderBrush = border;
        }

        var localAccent = Brush(58, 151, 137);
        var localBackground = Brush(236, 249, 246);
        var localBorder = Brush(188, 229, 221);
        var desktopAccent = Brush(117, 101, 206);
        var desktopBackground = Brush(241, 237, 255);
        var desktopBorder = Brush(211, 202, 241);
        var neutralAccent = Brush(139, 134, 147);
        var neutralBackground = Brush(245, 243, 248);
        var neutralBorder = Brush(222, 218, 228);

        if (_newTaskMode)
        {
            ApplyChatCapabilityTheme(localAccent, localBackground, localBorder);
            SelectedCapabilityDot.Fill = localAccent;
            ChatTaskText.Text = "当前任务：准备新建本地任务";
            ChatCapabilityText.Text = "本地控制任务 · 可停止与审批";
            ChatScopeNoticeText.Text = "发送首条要求后，可在这里查看流式回复；同时支持完整进度、停止和审批。";
            SelectedCapabilityText.Text = "新建本地任务 · 流式回复 / 完整进度 / 停止 / 审批";
            ProgressScopeText.Text = "新任务开始后，这里会显示完整计划、完成比例和实时活动。";
            return;
        }

        if (selectedMatchesLocal)
        {
            ApplyChatCapabilityTheme(localAccent, localBackground, localBorder);
            SelectedCapabilityDot.Fill = localAccent;
            ChatTaskText.Text = selectedTask is null ? "当前任务：柯朵本地任务" : $"当前任务：{TaskTitle(selectedTask)}";
            ChatCapabilityText.Text = "本地控制中 · 可停止与审批";
            ChatScopeNoticeText.Text = "此处显示本地控制任务的流式回复；可查看完整进度，并在需要时停止或处理审批。";
            SelectedCapabilityText.Text = "本地当前任务 · 流式回复 / 完整进度 / 停止 / 审批";
            ProgressScopeText.Text = "本地控制任务：显示完整计划、完成比例和实时活动。";
            return;
        }

        if (selectedTask is not null)
        {
            SelectedCapabilityDot.Fill = desktopAccent;
            if (progressMatchesLocal && progressSnapshot.IsVisible)
            {
                ApplyChatCapabilityTheme(localAccent, localBackground, localBorder);
                ChatTaskText.Text = "当前任务：柯朵本地任务";
                ChatCapabilityText.Text = "本地控制中 · 可停止与审批";
                ChatScopeNoticeText.Text =
                    "此处显示本地控制任务的流式回复；所选桌面任务的完整回复请在 Codex 中查看。";
            }
            else
            {
                ApplyChatCapabilityTheme(desktopAccent, desktopBackground, desktopBorder);
                ChatTaskText.Text = $"当前任务：{TaskTitle(selectedTask)}";
                ChatCapabilityText.Text = "Codex 桌面任务 · 可发送与跳转";
                ChatScopeNoticeText.Text = "可在柯朵中监看状态、发送消息并跳转；完整回复请在 Codex 中查看。";
            }

            SelectedCapabilityText.Text = "Codex 桌面任务 · 监看 / 发送 / 跳转";
            ProgressScopeText.Text = progressMatchesLocal && progressSnapshot.IsVisible
                ? "当前显示本地控制任务的完整进度；所选桌面任务仅提供状态与当前操作。"
                : "Codex 桌面任务：这里只显示状态与当前操作；完整进度和回复请在 Codex 中查看。";
            return;
        }

        if (progressMatchesLocal)
        {
            ApplyChatCapabilityTheme(localAccent, localBackground, localBorder);
            SelectedCapabilityDot.Fill = localAccent;
            ChatTaskText.Text = "当前任务：柯朵本地任务";
            ChatCapabilityText.Text = "本地控制中 · 可停止与审批";
            ChatScopeNoticeText.Text = "此处显示本地控制任务的流式回复；可查看完整进度，并在需要时停止或处理审批。";
            SelectedCapabilityText.Text = "本地当前任务 · 流式回复 / 完整进度 / 停止 / 审批";
            ProgressScopeText.Text = "本地控制任务：显示完整计划、完成比例和实时活动。";
            return;
        }

        ApplyChatCapabilityTheme(neutralAccent, neutralBackground, neutralBorder);
        SelectedCapabilityDot.Fill = neutralAccent;
        ChatTaskText.Text = "尚未选择任务";
        ChatCapabilityText.Text = "等待选择";
        ChatScopeNoticeText.Text = "选择桌面任务可监看、发送与跳转；新建本地任务可获得流式回复、完整进度、停止和审批。";
        SelectedCapabilityText.Text = "请选择任务，或新建本地任务";
        ProgressScopeText.Text = "选择桌面任务可查看状态；新建本地任务可获得完整计划和实时活动。";
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        StopVoiceConversation();
        Hide();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshThreadsAsync();

    private void ThreadListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThreadListBox.SelectedItem is CodexDesktopTask selected)
        {
            _newTaskMode = false;
            NewTaskExpander.IsExpanded = false;
            ApplySelectedTask(selected);
        }
        else if (!_newTaskMode)
        {
            _selectedThreadId = null;
            SelectedTaskText.Text = "请选择一个 Codex 任务";
            SelectedTaskDetailText.Text = "选择后可直接发送指令";
            UpdateCommandAvailability();
        }
    }

    private void NewTaskButton_Click(object sender, RoutedEventArgs e)
    {
        _newTaskMode = true;
        NewTaskExpander.IsExpanded = true;
        ThreadListBox.SelectedItem = null;
        _selectedThreadId = null;
        SelectedTaskText.Text = "新建本地 Codex 任务";
        SelectedTaskDetailText.Text = "选择工作文件夹后发送第一条指令";
        TaskStatusText.Text = _codex.IsConnected ? "新任务服务已就绪" : "正在准备新任务服务";
        UpdateCommandAvailability();
        PromptTextBox.Focus();
    }

    private void NewTaskExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        _newTaskMode = true;
        ThreadListBox.SelectedItem = null;
        _selectedThreadId = null;
        SelectedTaskText.Text = "新建本地 Codex 任务";
        SelectedTaskDetailText.Text = "此处的文件夹只用于新任务";
        UpdateCommandAvailability();
    }

    private void NewTaskExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || !_newTaskMode)
        {
            return;
        }

        _newTaskMode = false;
        var fallback = _threads.FirstOrDefault(IsDesktopTaskActive) ?? _threads.FirstOrDefault();
        ThreadListBox.SelectedItem = fallback;
        if (fallback is not null)
        {
            ApplySelectedTask(fallback);
        }
        else
        {
            SelectedTaskText.Text = "请选择一个 Codex 任务";
            SelectedTaskDetailText.Text = "选择后可直接发送指令";
        }

        UpdateCommandAvailability();
    }

    private async void ThreadListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedTaskAsync();

    private async void OpenTaskButton_Click(object sender, RoutedEventArgs e) => await OpenSelectedTaskAsync();

    private async Task OpenSelectedTaskAsync()
    {
        if (ThreadListBox.SelectedItem is not CodexDesktopTask selected)
        {
            ShowNotice("请先选择要打开的任务。", false);
            return;
        }

        try
        {
            await _desktopBridge.NavigateToTaskAsync(selected.Id);
            TaskStatusText.Text = $"已在 Codex 打开：{TaskTitle(selected)}";
        }
        catch (Exception ex)
        {
            ShowNotice($"打开任务失败：{ex.Message}", true);
        }
    }

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Codex 工作文件夹",
            InitialDirectory = Directory.Exists(ProjectPathTextBox.Text)
                ? ProjectPathTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            ProjectPathTextBox.Text = dialog.FolderName;
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendPromptAsync();

    private async void VoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceConversationCts is not null)
        {
            StopVoiceConversation();
            return;
        }

        if (_companionChatSending)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _voiceConversationCts = cts;
        SetVoiceConversationUi(active: true);
        NoticeBorder.Visibility = Visibility.Collapsed;
        await EnsureCharacterHistoryLoadedAsync();
        WorkStatusExpander.IsExpanded = false;

        var progress = new Progress<VoiceConversationProgress>(update =>
        {
            TaskStatusText.Text = update.Message;
            CompanionChatStatusText.Text = update.Message;
            VoiceButton.ToolTip = update.Stage == VoiceConversationStage.Listening
                ? "正在听主人说话；自然停顿约一秒后会自动开始识别。再点一次结束语音模式。"
                : update.Message;
            CompanionVoiceButton.ToolTip = VoiceButton.ToolTip;
        });

        try
        {
            while (!cts.IsCancellationRequested)
            {
                VoiceConversationResult result;
                try
                {
                    result = await _petWindow.RunVoiceConversationTurnAsync(progress, cts.Token);
                }
                catch (NoSpeechDetectedException exception)
                {
                    TaskStatusText.Text = "还在听，主人可以直接说话…";
                    CompanionChatStatusText.Text = "还在听，直接说话就好…";
                    VoiceButton.ToolTip = exception.Message;
                    CompanionVoiceButton.ToolTip = exception.Message;
                    await Task.Delay(300, cts.Token);
                    continue;
                }

                AddCompanionMessage(
                    isUser: true,
                    result.Transcript,
                    DateTimeOffset.Now,
                    PetInputSource.Voice);
                AddCompanionMessage(
                    isUser: false,
                    result.Interaction.Reply,
                    DateTimeOffset.Now,
                    detail: result.Interaction.Detail);
                CompanionChatStatusText.Text = $"已听懂 · {result.RecognitionBackend}";
                WorkStatusExpander.IsExpanded = false;

                await Task.Delay(280, cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            TaskStatusText.Text = "语音模式已结束";
            CompanionChatStatusText.Text = "语音模式已结束";
        }
        catch (Exception exception)
        {
            TaskStatusText.Text = "语音模式暂时不可用";
            CompanionChatStatusText.Text = "语音暂时不可用";
            AddCompanionSystemMessage($"语音刚才没接上：{exception.GetBaseException().Message}");
            ShowNotice(exception.GetBaseException().Message, true);
            App.WriteErrorLog("Voice conversation failure", exception);
        }
        finally
        {
            if (ReferenceEquals(_voiceConversationCts, cts))
            {
                _voiceConversationCts = null;
            }

            cts.Dispose();
            SetVoiceConversationUi(active: false);
        }
    }

    private void StopVoiceConversation()
    {
        var cts = _voiceConversationCts;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _petWindow.CancelVoiceConversation();
        TaskStatusText.Text = "正在结束语音模式…";
        CompanionChatStatusText.Text = "正在结束语音…";
    }

    private void SetVoiceConversationUi(bool active)
    {
        _voiceConversationActive = active;
        VoiceButton.Content = active ? "结束语音" : "开始语音";
        CompanionVoiceButton.Opacity = active ? 0.7 : 1;
        VoiceButton.ToolTip = active
            ? "结束连续语音对话。"
            : "优先使用 Agent Plan 豆包语音识别，失败自动回退本机 Whisper；回答结束后继续监听。";
        CompanionVoiceButton.ToolTip = active
            ? "结束连续语音对话。"
            : "开始连续语音对话。";
        PromptTextBox.IsEnabled = !active;
        CompanionChatInput.IsEnabled = !active && !_companionChatSending;
        UpdateCommandAvailability();
        UpdateCompanionChatAvailability();
    }

    private void PromptTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized)
        {
            UpdateCommandAvailability();
        }
    }

    private async void CompanionSendButton_Click(object sender, RoutedEventArgs e) =>
        await SendCompanionMessageAsync();

    private void CompanionChatInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized)
        {
            UpdateCompanionChatAvailability();
        }
    }

    private async void CompanionChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        e.Handled = true;
        await SendCompanionMessageAsync();
    }

    private void TaskTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, TaskTabs))
        {
            return;
        }

        UpdateComposerVisibility();
        if (!IsVisible)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!WorkStatusExpander.IsExpanded)
            {
                CompanionChatInput.Focus();
            }
            else
            {
                PromptTextBox.Focus();
            }
        }));
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _codex.InterruptTurnAsync();
            StopButton.IsEnabled = false;
            TaskStatusText.Text = "正在停止任务…";
        }
        catch (Exception ex)
        {
            ShowNotice($"停止失败：{ex.Message}", true);
        }
    }

    private async void AcceptApprovalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_approval is null)
        {
            return;
        }

        try
        {
            await _codex.RespondToApprovalAsync(_approval, "accept");
            ApprovalBorder.Visibility = Visibility.Collapsed;
            _approval = null;
            TaskStatusText.Text = "已允许，Codex 继续工作";
        }
        catch (Exception ex)
        {
            ShowNotice($"提交确认失败：{ex.Message}", true);
        }
    }

    private async void DeclineApprovalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_approval is null)
        {
            return;
        }

        try
        {
            await _codex.RespondToApprovalAsync(_approval, "decline");
            ApprovalBorder.Visibility = Visibility.Collapsed;
            _approval = null;
            TaskStatusText.Text = "已拒绝该操作";
        }
        catch (Exception ex)
        {
            ShowNotice($"提交确认失败：{ex.Message}", true);
        }
    }

    private async void TtsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _syncingTts)
        {
            return;
        }

        await _petWindow.UpdateTtsEnabledAsync(TtsCheckBox.IsChecked == true, announce: true);
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            StopVoiceConversation();
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!WorkStatusExpander.IsExpanded)
            {
                await SendCompanionMessageAsync();
            }
            else
            {
                await SendPromptAsync();
            }

            e.Handled = true;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        StopVoiceConversation();
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _codex.ConnectionStateChanged -= Codex_ConnectionStateChanged;
        _codex.AgentMessageDelta -= Codex_AgentMessageDelta;
        _codex.TurnCompleted -= Codex_TurnCompleted;
        _progress.ProgressChanged -= Codex_ProgressChanged;
        _desktopMonitor.SnapshotChanged -= DesktopMonitor_SnapshotChanged;
        TaskTabs.SelectionChanged -= TaskTabs_SelectionChanged;
    }
}
