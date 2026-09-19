using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lock.Core.Ipc;

/// <summary>
/// 在一个双向流上按行收发 JSON 对象。写入串行化，读取由调用方单线程循环。
/// </summary>
public sealed class JsonLineChannel : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonLineChannel(Stream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { AutoFlush = false, NewLine = "\n" };
    }

    /// <summary>读取下一条消息；连接关闭返回 null。</summary>
    public async Task<JsonObject?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) return null;
            if (line.Length == 0) continue;

            try
            {
                return JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                // 跳过无法解析的行
            }
        }
    }

    public async Task SendAsync(JsonObject message, CancellationToken ct = default)
    {
        var line = message.ToJsonString(Options);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    public static T? FromNode<T>(JsonNode? node) => node == null ? default : node.Deserialize<T>(Options);

    public void Dispose()
    {
        _writer.Dispose();
        _reader.Dispose();
        _stream.Dispose();
        _writeLock.Dispose();
    }
}
