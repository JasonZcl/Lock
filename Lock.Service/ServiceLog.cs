using Lock.Core.Services;

namespace Lock.Service;

/// <summary>
/// 服务自身的诊断日志（与解锁日志分开），写到 ProgramData\AppLock\service.log。
/// </summary>
public static class ServiceLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(ConfigStore.DataDirectory);
                var fi = new FileInfo(ConfigStore.ServiceLogPath);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(ConfigStore.ServiceLogPath, ConfigStore.ServiceLogPath + ".1", overwrite: true);

                File.AppendAllText(ConfigStore.ServiceLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
            catch
            {
                // 日志失败忽略
            }
        }
    }
}
