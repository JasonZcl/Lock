using System.IO;
using System.Windows;
using Lock.Core.Native;
using Lock.Views;

namespace Lock.Services;

/// <summary>
/// 资源管理器右键菜单入口：AppLock.exe --folder "路径"。
/// 根据文件夹当前状态决定动作：未纳管 → 登录后加入并锁定；已锁定 → 输密码解锁；已解锁 → 立即锁定。
/// 独立于托盘实例运行，完成后进程退出。
/// </summary>
public static class FolderCommand
{
    private const string Title = "应用锁";

    public static async Task RunAsync(ServiceClient client, string path)
    {
        if (!await client.WaitConnectedAsync(TimeSpan.FromSeconds(4)))
        {
            MessageBox.Show("无法连接应用锁服务，请确认服务已安装并正在运行。", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (client.Hello?.HasPassword != true)
        {
            MessageBox.Show("应用锁尚未设置密码，请先从托盘图标打开应用锁完成初始化。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var status = await client.FolderStatusAsync(path);
        if (!status.Ok || status.Data == null)
        {
            MessageBox.Show("查询状态失败：" + status.Error, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var name = Path.GetFileName(path) is { Length: > 0 } n ? n : path;
        var s = status.Data;

        if (!s.Managed)
        {
            if (s.ForbiddenReason != null)
            {
                MessageBox.Show($"无法锁定“{name}”：\n{s.ForbiddenReason}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var ok = PasswordDialog.Verify(null, Title,
                $"将“{name}”加入应用锁并立即锁定？\n锁定后任何人打开都会提示“拒绝访问”，需要输入密码解锁。\n\n请输入应用锁密码确认：",
                async p =>
                {
                    var r = await client.LoginAsync(p);
                    return r.Ok ? null : r.Error;
                });
            if (!ok) return;

            var add = await client.FolderAddAsync(path);
            await client.LogoutAsync();
            NativeMethods.NotifyFolderChanged(path);
            if (add.Ok)
                MessageBox.Show($"“{name}”已锁定。\n再次右键该文件夹可解锁。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("锁定失败：" + add.Error, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (s.Folder!.Locked)
        {
            var unlocked = PasswordDialog.Verify(null, Title, $"“{name}”已被应用锁锁定，请输入密码解锁：",
                async p =>
                {
                    var r = await client.FolderUnlockAsync(path, p);
                    return r.Ok ? null : r.Error;
                },
                forgot: () => ResetPasswordDialog.Show(null, client));
            NativeMethods.NotifyFolderChanged(path);
            if (!unlocked) return;

            var hint = s.RelockMinutes > 0 ? $"{s.RelockMinutes} 分钟后会自动重新锁定，" : "";
            MessageBox.Show($"“{name}”已解锁。\n{hint}也可以再次右键立即锁定。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lockResult = await client.FolderLockAsync(path);
        NativeMethods.NotifyFolderChanged(path);
        if (lockResult.Ok)
            MessageBox.Show($"“{name}”已锁定。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show("锁定失败：" + lockResult.Error, Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
