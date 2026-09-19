using System.Windows;

namespace Lock.Views;

public partial class RecoveryKeyWindow : Window
{
    public RecoveryKeyWindow(string key)
    {
        InitializeComponent();
        KeyText.Text = key;
    }

    public static void Show(Window? owner, string key)
    {
        var w = new RecoveryKeyWindow(key);
        if (owner is { IsLoaded: true }) w.Owner = owner;
        w.ShowDialog();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(KeyText.Text); } catch { /* 剪贴板被占用时忽略 */ }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
