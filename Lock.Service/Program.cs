using Lock.Core.Services;
using Lock.Service;

// 子命令：install / uninstall / run（前台调试）
// 无参数：作为 Windows 服务运行
if (args.Length > 0)
{
    try
    {
        switch (args[0].ToLowerInvariant())
        {
            case "install":
            {
                var servicePath = Environment.ProcessPath!;
                var agentPath = args.Length > 1
                    ? Path.GetFullPath(args[1])
                    : Path.Combine(Path.GetDirectoryName(servicePath)!, "AppLock.exe");
                ServiceInstaller.Install(servicePath, agentPath);
                ServiceLog.Info("服务已安装并启动");
                Console.WriteLine("服务已安装并启动。");
                return 0;
            }
            case "uninstall":
                ServiceInstaller.Uninstall(keepData: !args.Contains("--purge"));
                Console.WriteLine("服务已卸载。");
                return 0;
            case "run":
                break; // 前台运行，方便调试
            default:
                Console.WriteLine("用法：SysGuardSvc.exe [install [托盘程序路径] | uninstall [--purge] | run]");
                return 1;
        }
    }
    catch (Exception ex)
    {
        // 托盘程序是提权启动本进程的，只能看到退出码，详细原因要落到日志里
        ServiceLog.Error($"{args[0]} 失败", ex);
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ServiceInstaller.ServiceName);
builder.Services.AddHostedService<LockWorker>();

// 服务运行在 Session 0，控制台日志没人看，全部走文件日志
builder.Logging.ClearProviders();

try
{
    builder.Build().Run();
    return 0;
}
catch (Exception ex)
{
    ServiceLog.Error("服务崩溃", ex);
    return 2;
}
