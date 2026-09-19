using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace Lock.Core.Services;

/// <summary>
/// 安装 / 卸载 Windows 服务与托盘程序的登录自启计划任务。全部操作需要管理员权限。
/// </summary>
public static class ServiceInstaller
{
    public const string ServiceName = "AppLockService";
    public const string ServiceDisplayName = "AppLock 应用锁服务";
    public const string TaskName = "AppLock Agent";

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public enum ServiceState { NotInstalled, Stopped, Running, Other }

    public static ServiceState QueryState()
    {
        var (code, output) = Run("sc.exe", $"query {ServiceName}");
        if (code == 1060) return ServiceState.NotInstalled;
        if (code != 0) return ServiceState.Other;
        if (output.Contains("RUNNING")) return ServiceState.Running;
        if (output.Contains("STOPPED")) return ServiceState.Stopped;
        return ServiceState.Other;
    }

    /// <summary>
    /// 安装服务并启动，同时注册托盘程序的登录自启任务。
    /// </summary>
    public static void Install(string servicePath, string agentPath)
    {
        if (!IsAdministrator()) throw new InvalidOperationException("需要管理员权限。");
        if (!File.Exists(servicePath)) throw new FileNotFoundException("找不到服务程序", servicePath);

        ConfigStore.EnsureSecuredDirectory();

        var quotedPath = $"\"\\\"{servicePath}\\\"\"";

        if (QueryState() != ServiceState.NotInstalled)
        {
            // 已存在：不要 delete 再 create——只要有句柄没释放（服务管理器开着、刚 sc query 过），
            // create 就会报 1072“已标记为删除”。用 config 原地更新路径即可。
            Run("sc.exe", $"stop {ServiceName}");
            for (var i = 0; i < 40 && QueryState() == ServiceState.Running; i++)
                Thread.Sleep(250);
            Check(Run("sc.exe", $"config {ServiceName} binPath= {quotedPath} start= auto DisplayName= \"{ServiceDisplayName}\""));
        }
        else
        {
            Check(Run("sc.exe", $"create {ServiceName} binPath= {quotedPath} start= auto DisplayName= \"{ServiceDisplayName}\""));
        }
        Run("sc.exe", $"description {ServiceName} \"拦截被锁定的程序并要求输入密码。\"");
        // 崩溃后 5 秒自动重启
        Run("sc.exe", $"failure {ServiceName} reset= 0 actions= restart/5000/restart/5000/restart/5000");
        Check(Run("sc.exe", $"start {ServiceName}"));

        if (File.Exists(agentPath))
        {
            InstallAgentTask(agentPath);
            InstallContextMenu(agentPath);
        }
    }

    public static void Uninstall(bool keepData = true)
    {
        if (!IsAdministrator()) throw new InvalidOperationException("需要管理员权限。");

        Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
        RemoveContextMenu();

        if (QueryState() != ServiceState.NotInstalled)
        {
            Run("sc.exe", $"stop {ServiceName}");
            // 等待服务真正停止，否则 delete 会标记为“已删除”但残留
            for (var i = 0; i < 20 && QueryState() == ServiceState.Running; i++)
                Thread.Sleep(250);
            Check(Run("sc.exe", $"delete {ServiceName}"));
        }

        // 服务已停，锁着的文件夹没人能解了——在这里把权限恢复回去，否则卸载后文件夹永久无法访问
        RestoreAllFolders();

        if (!keepData && Directory.Exists(ConfigStore.DataDirectory))
            Directory.Delete(ConfigStore.DataDirectory, recursive: true);
    }

    /// <summary>恢复配置中所有仍处于锁定状态的文件夹的权限。失败的记录下来抛给调用方。</summary>
    public static void RestoreAllFolders()
    {
        var config = ConfigStore.Load();
        var failures = new List<string>();
        var changed = false;

        foreach (var f in config.Folders)
        {
            if (!Directory.Exists(f.Path)) continue;
            try
            {
                if (FolderLocker.IsLockedOnDisk(f.Path))
                    FolderLocker.Unlock(f.Path, f.OriginalSddl);
                f.Locked = false;
                changed = true;
            }
            catch (Exception ex)
            {
                failures.Add($"{f.Path}：{ex.Message}");
            }
        }

        if (changed)
        {
            try { ConfigStore.Save(config); } catch { /* 数据目录可能即将被删除 */ }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException("以下文件夹权限恢复失败，请手动在“属性 → 安全”里取得所有权：\n" + string.Join("\n", failures));
    }

    // ---- 资源管理器右键菜单 ----

    private const string ContextMenuKey = @"SOFTWARE\Classes\Directory\shell\AppLock";

    private static void InstallContextMenu(string agentPath)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(ContextMenuKey, writable: true);
        key.SetValue("MUIVerb", "应用锁：锁定 / 解锁此文件夹");
        key.SetValue("Icon", $"\"{agentPath}\",0");
        using var cmd = key.CreateSubKey("command", writable: true);
        cmd.SetValue("", $"\"{agentPath}\" --folder \"%1\"");
    }

    private static void RemoveContextMenu()
    {
        try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(ContextMenuKey, throwOnMissingSubKey: false); }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 从非管理员进程发起安装/卸载：以管理员身份运行服务程序的 install/uninstall 子命令并等待结束。
    /// 用户在 UAC 上点“否”会抛 Win32Exception(1223)。
    /// </summary>
    public static void RunElevated(string servicePath, string arguments)
    {
        var psi = new ProcessStartInfo(servicePath, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(servicePath),
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动服务程序");
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"操作失败（退出码 {p.ExitCode}），详情见 service.log");
    }

    /// <summary>
    /// 注册“任意用户登录时启动托盘程序”的计划任务（普通权限；托盘不需要管理员，安装/卸载时才单独提权）。
    /// 用计划任务而不是 HKLM Run 键，是为了对所有用户生效且不依赖各自的注册表配置单元。
    /// </summary>
    private static void InstallAgentTask(string agentPath)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>登录时启动 AppLock 托盘程序</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <GroupId>S-1-5-32-545</GroupId>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{System.Security.SecurityElement.Escape(agentPath)}</Command>
                  <WorkingDirectory>{System.Security.SecurityElement.Escape(Path.GetDirectoryName(agentPath) ?? "")}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var xmlPath = Path.Combine(Path.GetTempPath(), "AppLockAgentTask.xml");
        File.WriteAllText(xmlPath, xml, Encoding.Unicode);
        try
        {
            Check(Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F"));
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* ignore */ }
        }
    }

    private static (int Code, string Output) Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private static void Check((int Code, string Output) result)
    {
        if (result.Code != 0)
            throw new InvalidOperationException($"命令执行失败（{result.Code}）：{result.Output.Trim()}");
    }
}
