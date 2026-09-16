using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace XiaobianPet;

public partial class App : Application
{
    internal const string UiPreviewEnvironmentVariable = "XIAOBIAN_UI_PREVIEW";
    private const string SingleInstanceMutexName = @"Local\XiaobianPet.SingleInstance";
    private const string ActivationEventName = @"Local\XiaobianPet.Activate";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;
    private Task? _activationListener;
    private bool _ownsSingleInstanceMutex;
    private bool _activationPending;

    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(UiPreviewEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            base.OnStartup(e);
            return;
        }

        _activationEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: ActivationEventName);

        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: SingleInstanceMutexName,
            createdNew: out _ownsSingleInstanceMutex);

        if (!_ownsSingleInstanceMutex)
        {
            _activationEvent.Set();
            Shutdown();
            return;
        }

        _activationCancellation = new CancellationTokenSource();
        _activationListener = Task.Run(() => ListenForActivationRequests(_activationCancellation.Token));

        try
        {
            base.OnStartup(e);
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            WriteErrorLog("Startup failure", ex);
            try
            {
                MessageBox.Show(
                    $"柯朵启动失败。错误记录已保存到：\n{ErrorLogPath}\n\n{ex.Message}",
                    "柯朵启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
            }

            Shutdown(-1);
        }
    }

    internal void NotifyMainWindowReady(MainWindow window)
    {
        if (!_activationPending)
        {
            return;
        }

        _activationPending = false;
        window.ActivateFromLauncher();
    }

    private void HandleActivationRequest()
    {
        if (MainWindow is MainWindow window)
        {
            window.ActivateFromLauncher();
            return;
        }

        _activationPending = true;
    }

    private void ListenForActivationRequests(CancellationToken cancellationToken)
    {
        var activationEvent = _activationEvent;
        if (activationEvent is null)
        {
            return;
        }

        var handles = new WaitHandle[] { activationEvent, cancellationToken.WaitHandle };
        while (!cancellationToken.IsCancellationRequested)
        {
            var signaled = WaitHandle.WaitAny(handles);
            if (signaled != 0)
            {
                return;
            }

            Dispatcher.BeginInvoke(HandleActivationRequest);
        }
    }

    internal static void WriteErrorLog(string context, Exception exception)
    {
        try
        {
            var directory = Path.GetDirectoryName(ErrorLogPath)!;
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                ErrorLogPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
        }
    }

    private static string ErrorLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XiaobianPet",
        "startup-error.log");

    private static void App_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e) =>
        WriteErrorLog("Unhandled UI exception", e.Exception);

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteErrorLog("Unhandled process exception", exception);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationCancellation?.Cancel();
        try
        {
            _activationListener?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
        }

        _activationListener = null;
        _activationCancellation?.Dispose();
        _activationCancellation = null;
        _activationEvent?.Dispose();
        _activationEvent = null;

        if (_ownsSingleInstanceMutex)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
