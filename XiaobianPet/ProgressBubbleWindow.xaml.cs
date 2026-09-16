using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using XiaobianPet.Models;
using CodexProgressSnapshot = XiaobianPet.Models.CodexTaskProgressSnapshot;

namespace XiaobianPet;

/// <summary>
/// A non-activating, click-through status bubble that follows the desktop pet.
/// </summary>
public partial class ProgressBubbleWindow : Window
{
    private const double ReadyHeight = 104;
    private const double ProgressHeight = 104;
    private const double ScreenMargin = 6;
    private const double PetOverlap = 7;

    private const int GwlExStyle = -20;
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;

    private static readonly Brush ReadyBrush = CreateFrozenBrush(0x65, 0xC9, 0xB1);
    private static readonly Brush RunningBrush = CreateFrozenBrush(0x75, 0x65, 0xCE);
    private static readonly Brush WaitingBrush = CreateFrozenBrush(0xE5, 0x99, 0x3E);
    private static readonly Brush FailedBrush = CreateFrozenBrush(0xCD, 0x5C, 0x5C);
    private static readonly Brush StoppedBrush = CreateFrozenBrush(0x8B, 0x86, 0x93);

    private Window? _petWindow;
    private HwndSource? _source;

    public ProgressBubbleWindow()
    {
        InitializeComponent();

        // Give ShowReady() a safe first-show location even if the caller has not yet
        // supplied a pet window through PositionNear().
        Left = Math.Max(SystemParameters.WorkArea.Left + ScreenMargin,
            SystemParameters.WorkArea.Right - Width - 24);
        Top = Math.Max(SystemParameters.WorkArea.Top + ScreenMargin,
            SystemParameters.WorkArea.Bottom - Height - 24);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        extendedStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(extendedStyle));

        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessageHook);
    }

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
        base.OnClosed(e);
    }

    /// <summary>
    /// Shows the compact idle state without activating the window.
    /// </summary>
    public void ShowReady(string message = "柯朵已就绪")
    {
        RunOnUiThread(() =>
        {
            Height = ReadyHeight;
            StatusText.Text = "Codex 已就绪";
            StatusDot.Fill = ReadyBrush;
            PercentText.Visibility = Visibility.Collapsed;
            TaskProgressBar.Visibility = Visibility.Collapsed;
            KnotImage.Visibility = Visibility.Collapsed;
            TaskProgressBar.IsIndeterminate = false;
            TaskProgressBar.Value = 0;
            StepRow.Visibility = Visibility.Collapsed;
            OperationRow.Visibility = Visibility.Visible;
            OperationLabel.Visibility = Visibility.Collapsed;
            CurrentOperationText.Text = string.IsNullOrWhiteSpace(message)
                ? "柯朵已就绪"
                : message.Trim();
            CurrentOperationText.FontSize = 10.2;
            CurrentOperationText.Foreground = new SolidColorBrush(Color.FromRgb(98, 86, 129));

            RepositionByLastPet();
            EnsureShown();
        });
    }

    /// <summary>
    /// Shows one short character line without presenting it as a Codex status.
    /// </summary>
    public void ShowPetSpeech(string message)
    {
        RunOnUiThread(() =>
        {
            var content = string.IsNullOrWhiteSpace(message)
                ? "主人，我在这里。"
                : message.Trim();
            Height = ProgressHeight;
            StatusText.Text = "柯朵";
            StatusDot.Fill = ReadyBrush;
            PercentText.Visibility = Visibility.Collapsed;
            TaskProgressBar.Visibility = Visibility.Collapsed;
            KnotImage.Visibility = Visibility.Collapsed;
            TaskProgressBar.IsIndeterminate = false;
            TaskProgressBar.Value = 0;
            StepRow.Visibility = Visibility.Collapsed;
            OperationRow.Visibility = Visibility.Visible;
            OperationLabel.Visibility = Visibility.Collapsed;
            CurrentOperationText.Text = content;
            CurrentOperationText.FontSize = 10.2;
            CurrentOperationText.Foreground = new SolidColorBrush(Color.FromRgb(98, 86, 129));

            RepositionByLastPet();
            EnsureShown();
        });
    }

    /// <summary>
    /// Renders a Codex task snapshot and shows the bubble without taking focus.
    /// </summary>
    public void Render(CodexProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        RunOnUiThread(() =>
        {
            if (!snapshot.IsVisible)
            {
                HideBubble();
                return;
            }

            Height = ProgressHeight;
            StatusText.Text = string.IsNullOrWhiteSpace(snapshot.StatusText)
                ? StateLabel(snapshot.State)
                : snapshot.StatusText;
            StatusDot.Fill = StateBrush(snapshot.State);
            PercentText.Text = string.IsNullOrWhiteSpace(snapshot.ProgressLabel)
                ? snapshot.Percent is int percent ? $"{percent}%" : "—"
                : snapshot.ProgressLabel;
            PercentText.Visibility = Visibility.Visible;

            TaskProgressBar.Visibility = Visibility.Visible;
            KnotImage.Visibility = Visibility.Visible;
            TaskProgressBar.IsIndeterminate = snapshot.IsActive && snapshot.Percent is null;
            TaskProgressBar.Value = snapshot.Percent ?? 0;

            // Keep the desktop bubble as compact as the selected concept: status,
            // percentage, and the ornamental progress line. Detailed step and
            // operation text remains available in the main Progress tab.
            StepRow.Visibility = Visibility.Collapsed;
            OperationRow.Visibility = Visibility.Collapsed;
            CurrentStepText.Text = string.IsNullOrWhiteSpace(snapshot.CurrentStep)
                ? "等待 Codex 给出下一步"
                : snapshot.CurrentStep;

            OperationLabel.Visibility = Visibility.Visible;
            CurrentOperationText.FontSize = 10.5;
            CurrentOperationText.Foreground = new SolidColorBrush(Color.FromRgb(118, 109, 133));
            CurrentOperationText.Text = string.IsNullOrWhiteSpace(snapshot.CurrentOperation)
                ? CurrentStepText.Text
                : snapshot.CurrentOperation;

            RepositionByLastPet();
            EnsureShown();
        });
    }

    /// <summary>
    /// Positions the bubble near the pet and remembers it for future renders.
    /// </summary>
    public void PositionNear(Window pet)
    {
        ArgumentNullException.ThrowIfNull(pet);

        RunOnUiThread(() =>
        {
            _petWindow = pet;
            PositionNearCore(pet);
        });
    }

    /// <summary>
    /// Hides the bubble without closing it, so it can be shown again later.
    /// </summary>
    public void HideBubble() => RunOnUiThread(Hide);

    private void EnsureShown()
    {
        if (!IsVisible)
        {
            Show();
        }
    }

    private void RepositionByLastPet()
    {
        if (_petWindow is { IsLoaded: true } pet)
        {
            PositionNearCore(pet);
        }
    }

    private void PositionNearCore(Window pet)
    {
        var petBounds = pet is MainWindow mainWindow
            ? mainWindow.GetPetViewportScreenBounds()
            : new Rect(
                pet.Left,
                pet.Top,
                PositiveSize(pet.ActualWidth, pet.Width, 200),
                PositiveSize(pet.ActualHeight, pet.Height, 250));
        var targetX = petBounds.Left + (petBounds.Width / 2);

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        var minLeft = virtualLeft + ScreenMargin;
        var maxLeft = Math.Max(minLeft, virtualRight - Width - ScreenMargin);
        Left = Clamp(targetX - (Width / 2), minLeft, maxLeft);

        var aboveTop = petBounds.Top - Height + PetOverlap;
        var belowTop = petBounds.Bottom - PetOverlap;
        var canPlaceAbove = aboveTop >= virtualTop + ScreenMargin;
        var canPlaceBelow = belowTop + Height <= virtualBottom - ScreenMargin;

        if (canPlaceAbove || !canPlaceBelow)
        {
            Top = Clamp(
                aboveTop,
                virtualTop + ScreenMargin,
                Math.Max(virtualTop + ScreenMargin, virtualBottom - Height - ScreenMargin));
            ConfigureTail(pointsDown: true);
        }
        else
        {
            Top = Clamp(
                belowTop,
                virtualTop + ScreenMargin,
                Math.Max(virtualTop + ScreenMargin, virtualBottom - Height - ScreenMargin));
            ConfigureTail(pointsDown: false);
        }
    }

    private void ConfigureTail(bool pointsDown)
    {
        // Flip the one complete shell image when the bubble must sit below the pet.
        // The content remains upright and swaps its safe-area padding accordingly.
        BubbleShellTransform.ScaleY = pointsDown ? 1 : -1;
        BubbleContent.Margin = pointsDown
            ? new Thickness(24, 20, 24, 10)
            : new Thickness(24, 10, 24, 20);
    }

    private void RunOnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.Invoke(action);
        }
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmNcHitTest)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        if (message == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        return IntPtr.Zero;
    }

    private static Brush StateBrush(CodexTaskProgressState state) => state switch
    {
        CodexTaskProgressState.Running => RunningBrush,
        CodexTaskProgressState.Waiting => WaitingBrush,
        CodexTaskProgressState.Completed => ReadyBrush,
        CodexTaskProgressState.Failed => FailedBrush,
        CodexTaskProgressState.Interrupted => StoppedBrush,
        _ => ReadyBrush
    };

    private static string StateLabel(CodexTaskProgressState state) => state switch
    {
        CodexTaskProgressState.Running => "任务进行中",
        CodexTaskProgressState.Waiting => "等待你的确认",
        CodexTaskProgressState.Completed => "任务完成",
        CodexTaskProgressState.Failed => "任务失败",
        CodexTaskProgressState.Interrupted => "任务已停止",
        _ => "Codex 已就绪"
    };

    private static Brush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static double PositiveSize(double actual, double configured, double fallback)
    {
        if (double.IsFinite(actual) && actual > 0)
        {
            return actual;
        }

        return double.IsFinite(configured) && configured > 0 ? configured : fallback;
    }

    private static double Clamp(double value, double minimum, double maximum) =>
        Math.Max(minimum, Math.Min(value, maximum));

    private static IntPtr GetWindowLongPtr(IntPtr handle, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(handle, index)
        : new IntPtr(GetWindowLong32(handle, index));

    private static IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value) => IntPtr.Size == 8
        ? SetWindowLongPtr64(handle, index, value)
        : new IntPtr(SetWindowLong32(handle, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr handle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr handle, int index, IntPtr value);
}
