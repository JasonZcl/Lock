using System.Windows;
using System.Windows.Interop;
using Lock.Core.Native;

namespace Lock.Views;

/// <summary>
/// 通用密码对话框：设置新密码（需二次确认）或校验已有密码（校验逻辑由调用方异步完成，例如发给服务）。
/// </summary>
public partial class PasswordDialog : Window
{
    private const int MinLength = 4;

    /// <summary>校验回调：返回 null 表示通过，否则返回错误信息。</summary>
    private readonly Func<string, Task<string?>>? _verify;
    private readonly Action? _forgot;
    private readonly bool _setMode;
    private bool _busy;

    /// <summary>设置模式下用户输入的新密码。</summary>
    public string Password { get; private set; } = "";

    private PasswordDialog(string title, string prompt, bool setMode, Func<string, Task<string?>>? verify, Action? forgot)
    {
        InitializeComponent();

        Title = title;
        PromptText.Text = prompt;
        _setMode = setMode;
        _verify = verify;
        _forgot = forgot;

        Label1.Text = setMode ? $"新密码（至少 {MinLength} 位）" : "密码";
        ConfirmPanel.Visibility = setMode ? Visibility.Visible : Visibility.Collapsed;
        ForgotLink.Visibility = forgot != null ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) =>
        {
            NativeMethods.ForceForeground(new WindowInteropHelper(this).Handle);
            Password1.Focus();
        };
    }

    /// <summary>弹出“设置密码”对话框，返回新密码；取消返回 null。</summary>
    public static string? AskNewPassword(Window? owner, string title, string prompt)
    {
        var dlg = new PasswordDialog(title, prompt, setMode: true, verify: null, forgot: null);
        if (owner is { IsLoaded: true }) dlg.Owner = owner;
        return dlg.ShowDialog() == true ? dlg.Password : null;
    }

    /// <summary>弹出“验证密码”对话框，verify 通过后返回 true。</summary>
    public static bool Verify(Window? owner, string title, string prompt, Func<string, Task<string?>> verify, Action? forgot = null)
    {
        var dlg = new PasswordDialog(title, prompt, setMode: false, verify, forgot);
        if (owner is { IsLoaded: true }) dlg.Owner = owner;
        return dlg.ShowDialog() == true;
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var p1 = Password1.Password;

        if (_setMode)
        {
            if (p1.Length < MinLength)
            {
                ShowError($"密码长度至少 {MinLength} 位");
                return;
            }
            if (p1 != Password2.Password)
            {
                ShowError("两次输入的密码不一致");
                Password2.Clear();
                Password2.Focus();
                return;
            }
            Password = p1;
            DialogResult = true;
            return;
        }

        _busy = true;
        OkButton.IsEnabled = false;
        string? error;
        try
        {
            error = await _verify!(p1);
        }
        finally
        {
            _busy = false;
            OkButton.IsEnabled = true;
        }

        if (error == null)
        {
            DialogResult = true;
            return;
        }

        ShowError(error);
        Password1.Clear();
        Password1.Focus();
    }

    private void Forgot_Click(object sender, RoutedEventArgs e)
    {
        _forgot?.Invoke();
        Password1.Clear();
        Password1.Focus();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
