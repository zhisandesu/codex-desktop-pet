using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XiaobianPet.Models;

namespace XiaobianPet;

public partial class SettingsWindow : Window
{
    private const double MinimumOpacity = 0.60;
    private const double MaximumOpacity = 1.00;
    private const double ScreenMargin = 12;

    private readonly double _originalOpacity;
    private readonly double _originalPetSizeScale;
    private readonly Action<double> _previewOpacity;
    private readonly Action<double> _previewPetSize;
    private readonly Action _previewBubble;
    private readonly Func<double, double, Task> _saveAppearanceAsync;
    private readonly Func<PetMood, CancellationToken, Task> _previewAnimationAsync;
    private CancellationTokenSource? _animationPreviewCts;
    private Button? _activeAnimationPreviewButton;
    private long _animationPreviewGeneration;
    private bool _saved;
    private bool _reverted;

    public SettingsWindow(
        double currentOpacity,
        Action<double> previewOpacity,
        Func<double, Task> saveOpacityAsync)
        : this(
            currentOpacity,
            PetSettings.DefaultPetSizeScale,
            previewOpacity,
            static _ => { },
            () => { },
            AdaptOpacitySaver(saveOpacityAsync),
            [],
            static (_, _) => Task.CompletedTask)
    {
    }

    public SettingsWindow(
        double currentOpacity,
        Action<double> previewOpacity,
        Action previewBubble,
        Func<double, Task> saveOpacityAsync)
        : this(
            currentOpacity,
            PetSettings.DefaultPetSizeScale,
            previewOpacity,
            static _ => { },
            previewBubble,
            AdaptOpacitySaver(saveOpacityAsync),
            [],
            static (_, _) => Task.CompletedTask)
    {
    }

    public SettingsWindow(
        double currentOpacity,
        Action<double> previewOpacity,
        Action previewBubble,
        Func<double, Task> saveOpacityAsync,
        IReadOnlyList<PetAnimationPreviewOption> animationPreviews,
        Func<PetMood, CancellationToken, Task> previewAnimationAsync)
        : this(
            currentOpacity,
            PetSettings.DefaultPetSizeScale,
            previewOpacity,
            static _ => { },
            previewBubble,
            AdaptOpacitySaver(saveOpacityAsync),
            animationPreviews,
            previewAnimationAsync)
    {
    }

    public SettingsWindow(
        double currentOpacity,
        double currentPetSizeScale,
        Action<double> previewOpacity,
        Action<double> previewPetSize,
        Func<double, double, Task> saveAppearanceAsync)
        : this(
            currentOpacity,
            currentPetSizeScale,
            previewOpacity,
            previewPetSize,
            () => { },
            saveAppearanceAsync,
            [],
            static (_, _) => Task.CompletedTask)
    {
    }

    public SettingsWindow(
        double currentOpacity,
        double currentPetSizeScale,
        Action<double> previewOpacity,
        Action<double> previewPetSize,
        Action previewBubble,
        Func<double, double, Task> saveAppearanceAsync,
        IReadOnlyList<PetAnimationPreviewOption> animationPreviews,
        Func<PetMood, CancellationToken, Task> previewAnimationAsync)
    {
        InitializeComponent();

        _previewOpacity = previewOpacity ?? throw new ArgumentNullException(nameof(previewOpacity));
        _previewPetSize = previewPetSize ?? throw new ArgumentNullException(nameof(previewPetSize));
        _previewBubble = previewBubble ?? throw new ArgumentNullException(nameof(previewBubble));
        _saveAppearanceAsync = saveAppearanceAsync ?? throw new ArgumentNullException(nameof(saveAppearanceAsync));
        _previewAnimationAsync = previewAnimationAsync ?? throw new ArgumentNullException(nameof(previewAnimationAsync));
        _originalOpacity = Normalize(currentOpacity);
        _originalPetSizeScale = NormalizePetSizeScale(currentPetSizeScale);

        var previews = animationPreviews?.ToArray()
            ?? throw new ArgumentNullException(nameof(animationPreviews));
        BindAnimationPreviewGroup(
            IdleAnimationPreviewItems,
            IdleAnimationPreviewGroup,
            previews,
            PetAnimationPreviewCategory.IdleAndFeedback);
        BindAnimationPreviewGroup(
            MovementAnimationPreviewItems,
            MovementAnimationPreviewGroup,
            previews,
            PetAnimationPreviewCategory.Movement);
        BindAnimationPreviewGroup(
            DragAnimationPreviewItems,
            DragAnimationPreviewGroup,
            previews,
            PetAnimationPreviewCategory.Drag);
        AnimationPreviewCard.Visibility = previews.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        OpacitySlider.Value = _originalOpacity;
        UpdateOpacityLabel(_originalOpacity);
        OpacitySlider.ValueChanged += OpacitySlider_ValueChanged;
        PetSizeSlider.Value = _originalPetSizeScale;
        UpdatePetSizeLabel(_originalPetSizeScale);
        PetSizeSlider.ValueChanged += PetSizeSlider_ValueChanged;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var ownerCenterX = Owner is null
            ? SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width / 2)
            : Owner.Left + (Owner.ActualWidth / 2);
        var ownerCenterY = Owner is null
            ? SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height / 2)
            : Owner.Top + (Owner.ActualHeight / 2);

        var virtualLeft = SystemParameters.VirtualScreenLeft + ScreenMargin;
        var virtualTop = SystemParameters.VirtualScreenTop + ScreenMargin;
        var virtualRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ScreenMargin;
        var virtualBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - ScreenMargin;

        Left = Math.Clamp(ownerCenterX - (ActualWidth / 2), virtualLeft, Math.Max(virtualLeft, virtualRight - ActualWidth));
        Top = Math.Clamp(ownerCenterY - (ActualHeight / 2), virtualTop, Math.Max(virtualTop, virtualBottom - ActualHeight));
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var value = Normalize(e.NewValue);
        UpdateOpacityLabel(value);
        ErrorText.Visibility = Visibility.Collapsed;
        _previewOpacity(value);
    }

    private void UpdateOpacityLabel(double value) =>
        OpacityValueText.Text = $"{Math.Round(value * 100):0}%";

    private void PetSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var value = NormalizePetSizeScale(e.NewValue);
        UpdatePetSizeLabel(value);
        ErrorText.Visibility = Visibility.Collapsed;
        _previewPetSize(value);
    }

    private void UpdatePetSizeLabel(double value) =>
        PetSizeValueText.Text = $"{Math.Round(value * 100):0}%";

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        OpacitySlider.Value = MaximumOpacity;
        PetSizeSlider.Value = PetSettings.DefaultPetSizeScale;
    }

    private void PreviewBubbleButton_Click(object sender, RoutedEventArgs e) =>
        _previewBubble();

    private async void AnimationPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button
            {
                DataContext: PetAnimationPreviewOption option
            } button)
        {
            return;
        }

        var generation = ++_animationPreviewGeneration;
        CancelAnimationPreviewCore();

        var cts = new CancellationTokenSource();
        _animationPreviewCts = cts;
        _activeAnimationPreviewButton = button;
        button.SetResourceReference(FrameworkElement.StyleProperty, "MoonPrimaryButtonStyle");
        StopAnimationPreviewButton.IsEnabled = true;
        AnimationPreviewStatusText.Text = $"正在查看：{option.DisplayName} · {option.Mood}";
        ErrorText.Visibility = Visibility.Collapsed;

        try
        {
            await _previewAnimationAsync(option.Mood, cts.Token);
            if (IsCurrentAnimationPreview(generation, cts))
            {
                AnimationPreviewStatusText.Text = $"查看完成：{option.DisplayName} · {option.Mood}";
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentAnimationPreview(generation, cts))
            {
                var message = exception.GetBaseException().Message;
                AnimationPreviewStatusText.Text = message;
                ErrorText.Text = $"动画预览失败：{message}";
                ErrorText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (IsCurrentAnimationPreview(generation, cts))
            {
                _animationPreviewCts = null;
                ResetActiveAnimationPreviewButton();
                StopAnimationPreviewButton.IsEnabled = false;
            }

            cts.Dispose();
        }
    }

    private void StopAnimationPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        ++_animationPreviewGeneration;
        CancelAnimationPreviewCore();
        AnimationPreviewStatusText.Text = "已请求停止；当前动画播完后恢复";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;

        try
        {
            await _saveAppearanceAsync(
                Normalize(OpacitySlider.Value),
                NormalizePetSizeScale(PetSizeSlider.Value));
            _saved = true;
            Close();
        }
        catch (Exception exception)
        {
            ErrorText.Text = $"设置保存失败：{exception.GetBaseException().Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        ++_animationPreviewGeneration;
        CancelAnimationPreviewCore();

        if (!_saved && !_reverted)
        {
            _reverted = true;
            _previewOpacity(_originalOpacity);
            _previewPetSize(_originalPetSizeScale);
        }
    }

    private static double Normalize(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinimumOpacity, MaximumOpacity) : MaximumOpacity;

    private static double NormalizePetSizeScale(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(
                value,
                PetSettings.MinimumPetSizeScale,
                PetSettings.MaximumPetSizeScale)
            : PetSettings.DefaultPetSizeScale;

    private static Func<double, double, Task> AdaptOpacitySaver(
        Func<double, Task> saveOpacityAsync)
    {
        ArgumentNullException.ThrowIfNull(saveOpacityAsync);
        return (opacity, _) => saveOpacityAsync(opacity);
    }

    private static void BindAnimationPreviewGroup(
        ItemsControl itemsControl,
        FrameworkElement group,
        IReadOnlyList<PetAnimationPreviewOption> previews,
        PetAnimationPreviewCategory category)
    {
        var matching = previews.Where(option => option.Category == category).ToArray();
        itemsControl.ItemsSource = matching;
        group.Visibility = matching.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool IsCurrentAnimationPreview(long generation, CancellationTokenSource cts) =>
        generation == _animationPreviewGeneration &&
        ReferenceEquals(_animationPreviewCts, cts);

    private void CancelAnimationPreviewCore()
    {
        var cts = _animationPreviewCts;
        _animationPreviewCts = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        ResetActiveAnimationPreviewButton();
        StopAnimationPreviewButton.IsEnabled = false;
    }

    private void ResetActiveAnimationPreviewButton()
    {
        if (_activeAnimationPreviewButton is not null)
        {
            _activeAnimationPreviewButton.SetResourceReference(
                FrameworkElement.StyleProperty,
                "MoonSecondaryButtonStyle");
            _activeAnimationPreviewButton = null;
        }
    }
}
