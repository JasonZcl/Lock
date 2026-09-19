using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Lock.Core.Ipc;
using Lock.Core.Native;
using Lock.Services;

namespace Lock.Views;

/// <summary>
/// 解锁窗口：把密码交给服务校验，服务决定恢复还是结束目标进程。
/// </summary>
public partial class UnlockWindow : Window
{
    private readonly ServiceClient _client;
    private readonly string _requestId;
    private bool _finished;
    private bool _busy;

    public string RequestId => _requestId;

    public UnlockWindow(ServiceClient client, UnlockRequestEvent request)
    {
        InitializeComponent();

        _client = client;
        _requestId = request.RequestId;

        AppNameText.Text = request.DisplayName;
        Title = $"应用锁 - {request.DisplayName}";

        var icon = IconHelper.GetIcon(request.FullPath);
        if (icon != null) AppIcon.Source = icon;
        else AppIcon.Source = LockGlyph.Create();

        Loaded += (_, _) =>
        {
            NativeMethods.ForceForeground(new WindowInteropHelper(this).Handle);
            PasswordInput.Focus();
        };
        Closing += OnClosing;
    }

    /// <summary>服务端已关闭该请求（超出次数 / 进程退出等），窗口直接关掉。</summary>
    public void CloseByService(string reason)
    {
        _finished = true;
        Close();
    }

    private void Unlock_Click(object sender, RoutedEventArgs e) => _ = TryUnlockAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Cancel();
    }

    private void Forgot_Click(object sender, RoutedEventArgs e)
    {
        ResetPasswordDialog.Show(this, _client);
        PasswordInput.Focus();
    }

    private async Task TryUnlockAsync()
    {
        if (_busy || _finished) return;

        var password = PasswordInput.Password;
        PasswordInput.Clear();

        _busy = true;
        UnlockButton.IsEnabled = false;
        try
        {
            var r = await _client.UnlockAttemptAsync(_requestId, password);
            if (!r.Ok || r.Data == null)
            {
                ShowError(r.Error ?? "与服务通信失败");
                return;
            }

            if (r.Data.Success)
            {
                _finished = true;
                Close();
                return;
            }

            if (r.Data.Remaining <= 0)
            {
                // 服务已结束进程
                _finished = true;
                Close();
                return;
            }

            ShowError($"密码错误，还可尝试 {r.Data.Remaining} 次");
        }
        finally
        {
            _busy = false;
            UnlockButton.IsEnabled = true;
            if (!_finished) PasswordInput.Focus();
        }
    }

    private void Cancel()
    {
        if (_finished) return;
        _finished = true;
        _ = _client.UnlockCancelAsync(_requestId);
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 用户点关闭按钮 / Alt+F4 等同于取消
        if (!_finished)
        {
            _finished = true;
            _ = _client.UnlockCancelAsync(_requestId);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
