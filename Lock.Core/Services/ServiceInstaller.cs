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
    // 显示名不叫“应用锁”，避免在 services.msc 里一眼被认出并停掉。想改成别的低调名字改这里即可。
    public const string ServiceDisplayName = "系统防护服务";
    public const string ServiceDescription = "协调本机安全策略与资源保护。若停止，部分保护功能将失效。";
    public const string TaskName = "AppLock Agent";

    // 加固后的服务权限（SDDL）：
    //   SYSTEM 完全控制；Administrators 拥有除“停止/暂停”外的一切（含改权限/删除，供本工具卸载用）；
    //   普通已认证用户仅可查询状态。
    // 效果：services.msc / 任务管理器里的“停止”对管理员也是灰的/被拒；只有 SYSTEM 能停。
    // 注意：管理员仍可用命令行 sc sdset 把权限改回去——这是用户态方案的上限，只挡不懂命令行的人。
    private const string HardenedSddl =
        "D:(A;;CCDCLCSWRPWPDTLOCRRCWDWO;;;SY)(A;;CCDCLCSWRPLOCRRCWDWOSD;;;BA)(A;;CCLCSWLOCRRC;;;AU)";
    // 卸载前先设回可停止（授予 Administrators 停止权），否则本工具自己也停不掉服务。
    private const string PermissiveSddl =
        "D:(A;;CCDCLCSWRPWPDTLOCRRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRRCWDWOSD;;;BA)(A;;CCLCSWLOCRRC;;;AU)";

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
            // 若上一版加固过权限，先设回可停止，否则下面 stop 会被拒。
            Run("sc.exe", $"sdset {ServiceName} {PermissiveSddl}");
            Run("sc.exe", $"stop {ServiceName}");
            for (var i = 0; i < 40 && QueryState() == ServiceState.Running; i++)
                Thread.Sleep(250);
            Check(Run("sc.exe", $"config {ServiceName} binPath= {quotedPath} start= auto DisplayName= \"{ServiceDisplayName}\""));
        }
        else
        {
            Check(Run("sc.exe", $"create {ServiceName} binPath= {quotedPath} start= auto DisplayName= \"{ServiceDisplayName}\""));
        }
        Run("sc.exe", $"description {ServiceName} \"{ServiceDescription}\"");
        // 崩溃 / 被强杀后 5 秒自动重启（重要：deliberate 的 sc stop 不算失败不会触发，
        // 但用任务管理器“结束”SYSTEM 进程算崩溃，会被这条拉起来）
        Run("sc.exe", $"failure {ServiceName} reset= 0 actions= restart/5000/restart/5000/restart/5000");
        Check(Run("sc.exe", $"start {ServiceName}"));

        // 启动成功后再加固权限：非 SYSTEM 无法停止 / 暂停
        Run("sc.exe", $"sdset {ServiceName} {HardenedSddl}");

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
            // 加固后管理员也没有“停止”权，先把权限设回可停止（管理员保留了 WRITE_DAC 才能做到）
            Run("sc.exe", $"sdset {ServiceName} {PermissiveSddl}");
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
