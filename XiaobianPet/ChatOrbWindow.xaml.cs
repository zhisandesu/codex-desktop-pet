using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using XiaobianPet.Models;

namespace XiaobianPet;

/// <summary>
/// A compact, non-activating chat launcher that follows the desktop pet.
/// </summary>
public partial class ChatOrbWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;

    private readonly Window _petWindow;
    private HwndSource? _source;

    public ChatOrbWindow(Window petWindow)
    {
        ArgumentNullException.ThrowIfNull(petWindow);

        InitializeComponent();
        _petWindow = petWindow;
        Owner = petWindow;

        _petWindow.LocationChanged += PetWindow_LocationOrSizeChanged;
        _petWindow.SizeChanged += PetWindow_LocationOrSizeChanged;

        PositionNearCore();
    }

    /// <summary>
    /// Raised when the user clicks the orb to request the chat surface.
    /// </summary>
    public event EventHandler? ChatRequested;

    /// <summary>
    /// Repositions and shows the launcher without activating it.
    /// </summary>
    public void ShowNearPet()
    {
        RunOnUiThread(() =>
        {
            PositionNearCore();
            if (!IsVisible)
            {
                Show();
            }
        });
    }

    /// <summary>
    /// Repositions the launcher without changing its visibility.
    /// </summary>
    public void PositionNearPet() => RunOnUiThread(PositionNearCore);

    /// <summary>
    /// Hides the launcher without closing it so it can be shown again later.
    /// </summary>
    public void HideOrb() => RunOnUiThread(Hide);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        extendedStyle |= WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(extendedStyle));

        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessageHook);
    }

    protected override void OnClosed(EventArgs e)
    {
        _petWindow.LocationChanged -= PetWindow_LocationOrSizeChanged;
        _petWindow.SizeChanged -= PetWindow_LocationOrSizeChanged;

        _source?.RemoveHook(WindowMessageHook);
        _source = null;
        base.OnClosed(e);
    }

    private void ChatButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ChatRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PetWindow_LocationOrSizeChanged(object? sender, EventArgs e) =>
        PositionNearCore();

    private void PositionNearCore()
    {
        var petBounds = _petWindow is MainWindow mainWindow
            ? mainWindow.GetPetViewportScreenBounds()
            : new Rect(
                _petWindow.Left,
                _petWindow.Top,
                PositiveSize(_petWindow.ActualWidth, _petWindow.Width, 242),
                PositiveSize(_petWindow.ActualHeight, _petWindow.Height, 276));
        var placement = ChatOrbPlacementPolicy.Calculate(
            new ChatOrbPlacementBounds(
                petBounds.Left,
                petBounds.Top,
                petBounds.Width,
                petBounds.Height),
            PositiveSize(ActualWidth, Width, 60),
            PositiveSize(ActualHeight, Height, 60),
            new ChatOrbPlacementBounds(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight));

        Left = placement.Left;
        Top = placement.Top;
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
        if (message == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        return IntPtr.Zero;
    }

    private static double PositiveSize(double actual, double configured, double fallback)
    {
        if (double.IsFinite(actual) && actual > 0)
        {
            return actual;
        }

        return double.IsFinite(configured) && configured > 0 ? configured : fallback;
    }

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
