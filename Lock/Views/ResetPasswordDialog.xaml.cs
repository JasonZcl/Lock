using System.Windows;
using System.Windows.Interop;
using Lock.Core.Native;
using Lock.Services;

namespace Lock.Views;

public partial class ResetPasswordDialog : Window
{
    private readonly ServiceClient _client;
    private bool _busy;

    private ResetPasswordDialog(ServiceClient client)
    {
        InitializeComponent();
        _client = client;
        Loaded += (_, _) =>
        {
            NativeMethods.ForceForeground(new WindowInteropHelper(this).Handle);
            KeyBox.Focus();
        };
    }

    /// <summary>弹出重置对话框；重置成功后会展示新的恢复密钥并返回 true。</summary>
    public static bool Show(Window? owner, ServiceClient client)
    {
        var dlg = new ResetPasswordDialog(client);
        if (owner is { IsLoaded: true }) dlg.Owner = owner;
        return dlg.ShowDialog() == true;
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var key = KeyBox.Text.Trim();
        var p1 = Password1.Password;
        if (key.Length == 0) { ShowError("请输入恢复密钥"); return; }
        if (p1.Length < 4) { ShowError("密码长度至少 4 位"); return; }
        if (p1 != Password2.Password) { ShowError("两次输入的密码不一致"); return; }

        _busy = true;
        OkButton.IsEnabled = false;
        try
        {
            var r = await _client.ResetPasswordAsync(key, p1);
            if (!r.Ok || r.Data == null)
            {
                ShowError(r.Error ?? "重置失败");
                return;
            }

            MessageBox.Show(this, "密码已重置。接下来会显示新的恢复密钥，旧密钥已失效。", "应用锁",
                MessageBoxButton.OK, MessageBoxImage.Information);
            RecoveryKeyWindow.Show(this, r.Data.RecoveryKey);
            DialogResult = true;
        }
        finally
        {
            _busy = false;
            OkButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
