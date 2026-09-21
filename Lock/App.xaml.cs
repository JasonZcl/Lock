using System.IO;
using System.Windows;
using Lock.Core.Ipc;
using Lock.Core.Services;
using Lock.Services;
using Lock.Views;

namespace Lock;

public partial class App : Application
{
    private const string MutexName = @"Local\AppLock.Agent.SingleInstance";

    private Mutex? _mutex;
    private TrayIcon? _tray;
    private MainWindow? _mainWindow;
    private readonly Dictionary<string, UnlockWindow> _unlockWindows = new();
    private bool _setupInProgress;

    public ServiceClient Client { get; private set; } = null!;

    /// <summary>为 true 时 MainWindow 的 Closing 不再拦截为隐藏。</summary>
    public bool IsExiting { get; private set; }

    public static string ServiceExePath { get; } =
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "SysGuardSvc.exe");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("发生未处理的错误：\n" + args.Exception, "应用锁", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // 右键菜单：AppLock.exe --folder "路径"。独立于托盘实例，不占单实例互斥体，做完就退出
        var folderArg = Array.IndexOf(e.Args, "--folder");
        if (folderArg >= 0 && folderArg + 1 < e.Args.Length)
        {
            Client = new ServiceClient(Dispatcher);
            Client.Start();
            _ = FolderCommand.RunAsync(Client, e.Args[folderArg + 1]).ContinueWith(_ => Dispatcher.Invoke(Shutdown));
            return;
        }

        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("应用锁托盘程序已在运行（请查看系统托盘）。", "应用锁", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Client = new ServiceClient(Dispatcher);
        Client.Connected += OnConnected;
        Client.Disconnected += OnDisconnected;
        Client.UnlockRequested += OnUnlockRequested;
        Client.UnlockClosed += OnUnlockClosed;
        Client.Start();

        _tray = new TrayIcon();
        _tray.OpenRequested += () => _ = OpenMainWindowAsync();
        _tray.ExitRequested += RequestExit;
        _tray.SetStatus(false, 0);

        // 服务没装：直接打开管理界面的“服务”页引导安装
        if (ServiceInstaller.QueryState() == ServiceInstaller.ServiceState.NotInstalled)
            ShowMainWindow();
    }

    // ---- 连接状态 ----

    private async void OnConnected()
    {
        var hello = Client.Hello;
        if (hello == null) return;

        if (!hello.HasPassword)
        {
            await RunFirstTimeSetupAsync();
            return;
        }

        await RefreshTrayAsync();
    }

    private void OnDisconnected()
    {
        _tray?.SetStatus(false, 0);

        // 服务断了，等待中的解锁窗口已无意义
        foreach (var w in _unlockWindows.Values.ToList()) w.CloseByService("服务断开");
        _unlockWindows.Clear();
    }

    public async Task RefreshTrayAsync()
    {
        var s = await Client.GetStatusAsync();
        if (s.Ok && s.Data != null)
            _tray?.SetStatus(true, s.Data.Paused ? -1 : s.Data.EnabledLockCount);
    }

    private async Task RunFirstTimeSetupAsync()
    {
        if (_setupInProgress) return;
        _setupInProgress = true;
        try
        {
            var pwd = PasswordDialog.AskNewPassword(null, "欢迎使用应用锁",
                "首次运行，请先设置应用锁密码。之后打开被锁程序或管理界面都需要此密码。");
            if (pwd == null) return;

            var r = await Client.SetupAsync(pwd);
            if (!r.Ok || r.Data == null)
            {
                MessageBox.Show("设置密码失败：" + r.Error, "应用锁", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Hello 是连接时的快照，这里同步更新，否则再点托盘会再次进入“设置密码”流程
            if (Client.Hello != null) Client.Hello.HasPassword = true;

            RecoveryKeyWindow.Show(null, r.Data.RecoveryKey);

            // 刚设的密码直接登录，进入管理界面配置要锁的程序
            await Client.LoginAsync(pwd);
            ShowMainWindow();
            await RefreshTrayAsync();
        }
        finally
        {
            _setupInProgress = false;
        }
    }

    // ---- 解锁请求 ----

    private void OnUnlockRequested(UnlockRequestEvent request)
    {
        if (_unlockWindows.ContainsKey(request.RequestId)) return;

        var w = new UnlockWindow(Client, request);
        _unlockWindows[request.RequestId] = w;
        w.Closed += (_, _) => _unlockWindows.Remove(request.RequestId);
        w.Show();
        w.Activate();
    }

    private void OnUnlockClosed(UnlockClosedEvent evt)
    {
        if (_unlockWindows.Remove(evt.RequestId, out var w))
            w.CloseByService(evt.Reason);
    }

    // ---- 管理界面 ----

    /// <summary>弹出登录框并获取管理 token。已登录则直接返回 true。</summary>
    public Task<bool> RequireLoginAsync(Window? owner)
    {
        if (!Client.IsConnected) return Task.FromResult(false);
        if (Client.Token != null) return Task.FromResult(true);

        var ok = PasswordDialog.Verify(owner, "应用锁", "请输入密码以打开管理界面：",
            async p =>
            {
                var r = await Client.LoginAsync(p);
                return r.Ok ? null : r.Error;
            },
            forgot: () => ResetPasswordDialog.Show(owner, Client));
        return Task.FromResult(ok);
    }

    private async Task OpenMainWindowAsync()
    {
        if (_mainWindow is { IsVisible: true })
        {
            _mainWindow.Activate();
            return;
        }

        if (Client.IsConnected && Client.Hello?.HasPassword == false)
        {
            await RunFirstTimeSetupAsync();
            return;
        }

        // 服务未连接时也允许打开（只能用“服务”页）
        if (Client.IsConnected && !await RequireLoginAsync(null))
            return;

        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        _mainWindow ??= new MainWindow(Client);
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void RequestExit()
    {
        var r = MessageBox.Show(
            "退出托盘程序后，被锁程序启动时将无法弹出密码框，会被服务直接结束。\n确定退出吗？",
            "应用锁", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;

        IsExiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Client?.Dispose();
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
