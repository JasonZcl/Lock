using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Lock.Core.Ipc;
using Lock.Core.Models;
using Lock.Core.Services;
using Lock.Services;
using Microsoft.Win32;

namespace Lock.Views;

public partial class MainWindow : Window
{
    /// <summary>列表行：包装 LockedApp 并附带图标。</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        public LockedApp Model { get; }
        public ImageSource? Icon { get; }

        public string DisplayName => Model.DisplayName;
        public string ExeName => Model.ExeName;
        public string? FullPath => Model.FullPath;
        public bool HasPath => !string.IsNullOrEmpty(Model.FullPath);

        public bool Enabled
        {
            get => Model.Enabled;
            set { Model.Enabled = value; Notify(nameof(Enabled)); }
        }

        /// <summary>0 = 文件名，1 = 完整路径。</summary>
        public int MatchIndex
        {
            get => Model.MatchMode == MatchMode.FullPath ? 1 : 0;
            set { Model.MatchMode = value == 1 ? MatchMode.FullPath : MatchMode.ExeName; Notify(nameof(MatchIndex)); }
        }

        public Row(LockedApp model)
        {
            Model = model;
            Icon = IconHelper.GetIcon(model.FullPath);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly ServiceClient _client;
    private readonly ObservableCollection<Row> _rows = [];
    private LockSettings _settings = new();
    private bool _loading = true;
    private bool _dirty;

    private static App App => (App)Application.Current;

    public MainWindow(ServiceClient client)
    {
        InitializeComponent();
        _client = client;
        AppList.ItemsSource = _rows;

        _client.Connected += OnConnectionChanged;
        _client.Disconnected += OnConnectionChanged;
        _client.SettingsChanged += () => _ = LoadSettingsAsync();
        _client.FoldersChanged += () => _ = LoadFoldersAsync();

        Closing += OnClosing;
        Loaded += async (_, _) =>
        {
            RefreshServicePanel();
            await LoadSettingsAsync();
        };
    }

    // ---- 加载 / 保存 ----

    private bool LoggedIn => _client.IsConnected && _client.Token != null;

    private async Task LoadSettingsAsync()
    {
        var enabled = LoggedIn;
        AppsTab.IsEnabled = SettingsTab.IsEnabled = LogTab.IsEnabled = FoldersTab.IsEnabled = enabled;
        if (!enabled)
        {
            Tabs.SelectedItem = ServiceTab;
            return;
        }

        var r = await _client.GetSettingsAsync();
        if (!r.Ok && r.Code == "unauthorized")
        {
            // 服务端 token 已过期（30 分钟）：重新登录后再取一次
            if (!await App.RequireLoginAsync(this)) { await LoadSettingsAsync(); return; }
            r = await _client.GetSettingsAsync();
        }
        if (!r.Ok || r.Data == null)
        {
            ShowError("读取设置失败：" + r.Error);
            return;
        }

        _loading = true;
        _settings = r.Data;
        _rows.Clear();
        foreach (var app in _settings.LockedApps) _rows.Add(new Row(app));
        GraceBox.Text = _settings.UnlockGraceSeconds.ToString();
        AttemptsBox.Text = _settings.MaxAttempts.ToString();
        RelockBox.Text = _settings.FolderRelockMinutes.ToString();
        PauseCheck.IsChecked = _settings.Paused;
        _loading = false;
        SetDirty(false);
    }

    private async Task<bool> SaveAsync()
    {
        if (!LoggedIn) return false;

        if (!int.TryParse(GraceBox.Text, out var grace) || grace < 0)
        {
            ShowError("宽限期必须是大于等于 0 的整数");
            return false;
        }
        if (!int.TryParse(AttemptsBox.Text, out var attempts) || attempts < 1)
        {
            ShowError("尝试次数必须是大于等于 1 的整数");
            return false;
        }

        if (!int.TryParse(RelockBox.Text, out var relock) || relock < 0)
        {
            ShowError("文件夹自动锁定分钟数必须是大于等于 0 的整数");
            return false;
        }

        _settings.UnlockGraceSeconds = grace;
        _settings.MaxAttempts = attempts;
        _settings.FolderRelockMinutes = relock;
        _settings.Paused = PauseCheck.IsChecked == true;
        _settings.LockedApps = _rows.Select(r => r.Model).ToList();

        var r = await _client.SetSettingsAsync(_settings);
        if (!r.Ok)
        {
            ShowError("保存失败：" + r.Error);
            if (r.Code == "unauthorized") await App.RequireLoginAsync(this);
            return false;
        }

        SetDirty(false);
        _ = App.RefreshTrayAsync();
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => _ = SaveAsync();

    private void Dirty(object sender, RoutedEventArgs e)
    {
        if (!_loading) SetDirty(true);
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        DirtyHint.Visibility = dirty ? Visibility.Visible : Visibility.Hidden;
    }

    // ---- 添加 / 移除 ----

    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择要锁定的程序",
            Filter = "可执行文件 (*.exe)|*.exe",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        var aliases = new List<string>();
        foreach (var path in dlg.FileNames)
        {
            var exe = Path.GetFileName(path);
            // Win11 的 notepad.exe / calc.exe 等只是跳板（应用执行别名或 System32 下的启动器），
            // 真实进程在 WindowsApps 下，按完整路径永远匹配不上；系统目录下的程序统一默认按文件名匹配
            var isAlias = IsAppExecutionAlias(path) || IsUnderWindowsDir(path);
            if (isAlias) aliases.Add(exe);

            AddApp(new LockedApp
            {
                ExeName = exe.ToLowerInvariant(),
                DisplayName = ProcessPickerWindow.GetDescription(path) ?? Path.GetFileNameWithoutExtension(exe),
                FullPath = path,
                MatchMode = isAlias ? MatchMode.ExeName : MatchMode.FullPath,
            });
        }

        if (aliases.Count > 0)
        {
            MessageBox.Show(this,
                $"{string.Join("、", aliases)} 位于系统目录或是商店应用的别名，真实进程路径可能不同，已自动改为按文件名匹配。",
                "应用锁", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static bool IsAppExecutionAlias(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) && new FileInfo(path).Length == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnderWindowsDir(string path)
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !string.IsNullOrEmpty(win)
               && path.StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private void AddInstalled_Click(object sender, RoutedEventArgs e)
    {
        var picker = new InstalledAppPickerWindow { Owner = this };
        if (picker.ShowDialog() != true) return;
        foreach (var app in picker.Selected) AddApp(app);
    }

    private void AddRunning_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ProcessPickerWindow { Owner = this };
        if (picker.ShowDialog() != true) return;
        foreach (var app in picker.Selected) AddApp(app);
    }

    private void AddApp(LockedApp app)
    {
        var selfExe = Path.GetFileName(Environment.ProcessPath ?? "").ToLowerInvariant();
        if (app.ExeName == selfExe || app.ExeName == "applock.service.exe")
        {
            MessageBox.Show(this, "不能锁定应用锁自身。", "应用锁", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_rows.Any(r => r.Model.Key == app.Key)) return; // 已存在

        _rows.Add(new Row(app));
        SetDirty(true);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = AppList.SelectedItems.OfType<Row>().ToList();
        if (selected.Count == 0) return;
        foreach (var row in selected) _rows.Remove(row);
        SetDirty(true);
    }

    // ---- 文件夹锁 ----

    public sealed class FolderRow
    {
        public required FolderInfo Info { get; init; }
        public string DisplayName => Info.DisplayName;
        public string Path => Info.Path;
        public string StateText => Info.Locked ? "已锁定" : "已解锁";
        public Brush StateBg => (Brush)Application.Current.FindResource(Info.Locked ? "SuccessLight" : "WarningLight");
        public Brush StateFg => (Brush)Application.Current.FindResource(Info.Locked ? "Success" : "Warning");
        public string RelockText => Info.Locked ? "" : Info.RelockInSeconds is { } s ? $"{Math.Max(1, (s + 59) / 60)} 分钟后" : "注销 / 手动";
    }

    private async Task LoadFoldersAsync()
    {
        if (!LoggedIn) return;
        var r = await _client.FolderListAsync();
        if (!r.Ok || r.Data == null) return;
        FolderList.ItemsSource = r.Data.Folders.Select(f => new FolderRow { Info = f }).ToList();
    }

    private void RefreshFolders_Click(object sender, RoutedEventArgs e) => _ = LoadFoldersAsync();

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择要锁定的文件夹", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;

        var errors = new List<string>();
        foreach (var path in dlg.FolderNames)
        {
            var r = await _client.FolderAddAsync(path);
            if (!r.Ok) errors.Add(path + "\n    " + r.Error);
            Lock.Core.Native.NativeMethods.NotifyFolderChanged(path);
        }
        await LoadFoldersAsync();
        if (errors.Count > 0) ShowError("以下文件夹未能锁定：\n\n" + string.Join("\n", errors));
    }

    private async void LockFolder_Click(object sender, RoutedEventArgs e)
        => await ForSelectedFoldersAsync(p => _client.FolderLockAsync(p), "锁定");

    private async void UnlockFolder_Click(object sender, RoutedEventArgs e)
        => await ForSelectedFoldersAsync(p => _client.FolderUnlockAsync(p, null), "解锁");

    private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        var selected = FolderList.SelectedItems.OfType<FolderRow>().ToList();
        if (selected.Count == 0) return;
        var answer = MessageBox.Show(this, $"移除 {selected.Count} 个文件夹？移除后会恢复原有权限，不再受保护。",
            "应用锁", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        await ForSelectedFoldersAsync(p => _client.FolderRemoveAsync(p), "移除");
    }

    private async Task ForSelectedFoldersAsync(Func<string, Task<ApiResult<ServiceClient.Empty>>> op, string verb)
    {
        var selected = FolderList.SelectedItems.OfType<FolderRow>().Select(r => r.Path).ToList();
        if (selected.Count == 0) return;

        var errors = new List<string>();
        foreach (var path in selected)
        {
            var r = await op(path);
            if (!r.Ok) errors.Add(path + "\n    " + r.Error);
            Lock.Core.Native.NativeMethods.NotifyFolderChanged(path);
        }
        await LoadFoldersAsync();
        if (errors.Count > 0) ShowError($"以下文件夹{verb}失败：\n\n" + string.Join("\n", errors));
    }

    // ---- 日志（倒序分页） ----

    private int _logPage;      // 从 0 开始
    private int _logTotal;
    private bool _logLoading;

    private int LogPageSize =>
        PageSizeBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Content: string s } && int.TryParse(s, out var n) ? n : 50;

    private int LogPageCount => Math.Max(1, (_logTotal + LogPageSize - 1) / LogPageSize);

    private async Task LoadLogPageAsync(int page)
    {
        if (!LoggedIn || _logLoading) return;
        _logLoading = true;
        try
        {
            var size = LogPageSize;
            var r = await _client.GetLogAsync(Math.Max(0, page) * size, size);
            if (!r.Ok || r.Data == null)
            {
                ShowError("读取日志失败：" + r.Error);
                return;
            }

            _logTotal = r.Data.Total;
            // 总数变化后页码可能越界（例如日志滚动），夹回有效范围
            _logPage = Math.Clamp(page, 0, LogPageCount - 1);
            if (_logPage != page)
            {
                r = await _client.GetLogAsync(_logPage * size, size);
                if (!r.Ok || r.Data == null) return;
            }

            LogList.ItemsSource = r.Data.Entries;
            LogCountText.Text = $"共 {_logTotal} 条";
            PageText.Text = $"{_logPage + 1} / {LogPageCount}";
            FirstPageButton.IsEnabled = PrevPageButton.IsEnabled = _logPage > 0;
            NextPageButton.IsEnabled = LastPageButton.IsEnabled = _logPage < LogPageCount - 1;
        }
        finally
        {
            _logLoading = false;
        }
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e) => _ = LoadLogPageAsync(_logPage);
    private void FirstPage_Click(object sender, RoutedEventArgs e) => _ = LoadLogPageAsync(0);
    private void PrevPage_Click(object sender, RoutedEventArgs e) => _ = LoadLogPageAsync(_logPage - 1);
    private void NextPage_Click(object sender, RoutedEventArgs e) => _ = LoadLogPageAsync(_logPage + 1);
    private void LastPage_Click(object sender, RoutedEventArgs e) => _ = LoadLogPageAsync(LogPageCount - 1);

    private void PageSize_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded) _ = LoadLogPageAsync(0);
    }

    private void Tabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // TabControl 内部控件的 SelectionChanged 也会冒泡上来，只处理 Tab 本身的切换
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;

        // 每次进入日志页都回到第一页，看到最新记录
        if (Tabs.SelectedItem == LogTab) _ = LoadLogPageAsync(0);
        if (Tabs.SelectedItem == FoldersTab) _ = LoadFoldersAsync();
    }

    // ---- 密码 ----

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var ok = PasswordDialog.Verify(this, "修改密码", "请输入当前密码：", async p =>
        {
            var r = await _client.LoginAsync(p);
            return r.Ok ? null : r.Error;
        });
        if (!ok) return;

        var newPassword = PasswordDialog.AskNewPassword(this, "修改密码", "请输入新密码：");
        if (newPassword == null) return;

        var res = await _client.ChangePasswordAsync(newPassword);
        if (!res.Ok || res.Data == null)
        {
            ShowError("修改失败：" + res.Error);
            return;
        }

        // 改密码后服务端会清空所有 token，重新登录一次
        await _client.LoginAsync(newPassword);
        MessageBox.Show(this, "密码已修改。接下来会显示新的恢复密钥。", "应用锁", MessageBoxButton.OK, MessageBoxImage.Information);
        RecoveryKeyWindow.Show(this, res.Data.RecoveryKey);
    }

    private async void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (ResetPasswordDialog.Show(this, _client))
            await App.RequireLoginAsync(this);
    }

    // ---- 服务 ----

    private void RefreshServicePanel()
    {
        var state = ServiceInstaller.QueryState();
        var running = state == ServiceInstaller.ServiceState.Running;
        var stateText = state switch
        {
            ServiceInstaller.ServiceState.NotInstalled => "未安装",
            ServiceInstaller.ServiceState.Running => "运行中",
            ServiceInstaller.ServiceState.Stopped => "已停止",
            _ => "未知",
        };

        ServiceStateText.Text = stateText;
        ServiceStateText.Foreground = running ? Res("Success") : Res("Danger");
        ConnStateText.Text = _client.IsConnected ? "已连接" : "未连接";
        ConnStateText.Foreground = _client.IsConnected ? Res("Success") : Res("Danger");

        // 顶部状态胶囊
        ServicePillText.Text = "服务 · " + stateText;
        ServiceDot.Fill = running ? Res("Success") : Res("Danger");
        ServicePill.Background = running ? Res("SuccessLight") : Res("DangerLight");
        ConnPillText.Text = _client.IsConnected ? "已连接" : "未连接";
        ConnDot.Fill = _client.IsConnected ? Res("Success") : Res("TextDisabled");
        ConnPill.Background = _client.IsConnected ? Res("SuccessLight") : Res("Bg");

        ServicePathText.Text = "服务程序：" + App.ServiceExePath;
        InstallButton.IsEnabled = File.Exists(App.ServiceExePath);
        UninstallButton.IsEnabled = state != ServiceInstaller.ServiceState.NotInstalled;
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private void RefreshService_Click(object sender, RoutedEventArgs e) => RefreshServicePanel();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = UninstallButton.IsEnabled = false;
        try
        {
            // 托盘本身不是管理员：提权运行服务程序的 install 子命令（弹一次 UAC）
            var agent = Environment.ProcessPath!;
            await Task.Run(() => ServiceInstaller.RunElevated(App.ServiceExePath, "install \"" + agent + "\""));
            MessageBox.Show(this, "服务已安装并启动。", "应用锁", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 上点了“否”
        }
        catch (Exception ex)
        {
            ShowError("安装失败：" + ex.Message);
        }
        RefreshServicePanel();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "卸载后应用锁将不再生效。是否同时删除密码和配置？\n\n是：连同配置一起删除\n否：保留配置\n取消：不卸载",
            "应用锁", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Cancel) return;

        InstallButton.IsEnabled = UninstallButton.IsEnabled = false;
        try
        {
            var keep = answer == MessageBoxResult.No;
            await Task.Run(() => ServiceInstaller.RunElevated(App.ServiceExePath, keep ? "uninstall" : "uninstall --purge"));
            MessageBox.Show(this, "服务已卸载。", "应用锁", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 上点了“否”
        }
        catch (Exception ex)
        {
            ShowError("卸载失败：" + ex.Message);
        }
        RefreshServicePanel();
    }

    private async void OnConnectionChanged()
    {
        RefreshServicePanel();
        // 服务重连后 token 已失效；窗口开着的话直接让用户重新登录，而不是让所有页签灰掉
        if (_client.IsConnected && _client.Token == null && IsVisible && _client.Hello?.HasPassword == true)
            await App.RequireLoginAsync(this);
        await LoadSettingsAsync();
    }

    // ---- 其它 ----

    private void ShowError(string message)
        => MessageBox.Show(this, message, "应用锁", MessageBoxButton.OK, MessageBoxImage.Error);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_dirty && LoggedIn)
        {
            var r = MessageBox.Show(this, "有未保存的更改，是否保存？", "应用锁", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes)
            {
                e.Cancel = true;
                _ = SaveAsync().ContinueWith(t =>
                {
                    if (t.Result) Dispatcher.Invoke(() => { _dirty = false; Close(); });
                });
                return;
            }
        }

        if (App.IsExiting) return;

        // 关闭 = 隐藏到托盘，并注销管理登录
        e.Cancel = true;
        _ = _client.LogoutAsync();
        Hide();
    }
}
