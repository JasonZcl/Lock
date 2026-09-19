using System.Text.Json;
using Lock.Core.Models;

namespace Lock.Core.Services;

/// <summary>
/// 解锁日志：每行一条 JSON（JSONL），超过大小后滚动为 .1 备份。
/// </summary>
public static class LogStore
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions LineOptions = new() { WriteIndented = false };

    public static void Append(LogEntry entry)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(ConfigStore.DataDirectory);

                var fi = new FileInfo(ConfigStore.LogPath);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(ConfigStore.LogPath, ConfigStore.LogPath + ".1", overwrite: true);

                File.AppendAllText(ConfigStore.LogPath, JsonSerializer.Serialize(entry, LineOptions) + Environment.NewLine);
            }
            catch
            {
                // 日志失败不能影响主流程
            }
        }
    }

    /// <summary>按时间倒序分页读取：跳过最新的 offset 条，取 count 条。返回条目与总数。</summary>
    public static (List<LogEntry> Entries, int Total) ReadPage(int offset, int count)
    {
        lock (Sync)
        {
            var lines = new List<string>();
            try
            {
                if (File.Exists(ConfigStore.LogPath + ".1"))
                    lines.AddRange(File.ReadAllLines(ConfigStore.LogPath + ".1"));
                if (File.Exists(ConfigStore.LogPath))
                    lines.AddRange(File.ReadAllLines(ConfigStore.LogPath));
            }
            catch
            {
                return ([], 0);
            }

            lines.RemoveAll(string.IsNullOrWhiteSpace);

            var result = new List<LogEntry>(Math.Min(count, lines.Count));
            for (var i = lines.Count - 1 - offset; i >= 0 && result.Count < count; i--)
            {
                try
                {
                    var e = JsonSerializer.Deserialize<LogEntry>(lines[i], LineOptions);
                    if (e != null) result.Add(e);
                }
                catch
                {
                    // 跳过损坏行
                }
            }
            return (result, lines.Count);
        }
    }

    /// <summary>读取最近 count 条记录，最新的在前。</summary>
    public static List<LogEntry> ReadLast(int count) => ReadPage(0, count).Entries;
}
