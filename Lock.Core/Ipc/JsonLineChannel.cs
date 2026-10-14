using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lock.Core.Ipc;

/// <summary>
/// 在一个双向流上按行收发 JSON 对象。写入串行化，读取由调用方单线程循环。
/// </summary>
public sealed class JsonLineChannel : IDisposable
{
    public const int MaxMessageChars = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly char[] _readBuffer = new char[4096];
    private int _readOffset;
    private int _readCount;
    private int _disposed;

    public JsonLineChannel(Stream stream, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _reader = new StreamReader(stream, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { AutoFlush = false, NewLine = "\n" };
    }

    /// <summary>读取下一条消息；连接关闭返回 null。</summary>
    public async Task<JsonObject?> ReadAsync(CancellationToken ct)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_readOffset == _readCount)
            {
                _readCount = await _reader.ReadAsync(_readBuffer.AsMemory(), ct).ConfigureAwait(false);
                _readOffset = 0;
                if (_readCount == 0)
                {
                    if (line.Length != 0) throw new InvalidDataException("消息不完整");
                    return null;
                }
            }
            while (_readOffset < _readCount)
            {
                var character = _readBuffer[_readOffset++];
                if (character == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r') line.Length--;
                    if (line.Length == 0) throw new InvalidDataException("消息不能为空");
                    try
                    {
                        return JsonNode.Parse(line.ToString(), documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject
                            ?? throw new InvalidDataException("消息必须是 JSON 对象");
                    }
                    catch (JsonException exception)
                    {
                        throw new InvalidDataException("消息格式无效", exception);
                    }
                }
                if (line.Length >= MaxMessageChars && !(line.Length == MaxMessageChars && character == '\r'))
                    throw new InvalidDataException("消息超过大小限制");
                line.Append(character);
            }
        }
    }

    public async Task SendAsync(JsonObject message, CancellationToken ct = default)
    {
        var line = message.ToJsonString(Options);
        if (line.Length > MaxMessageChars) throw new InvalidDataException("消息超过大小限制");
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!_leaveOpen)
        {
            _stream.Dispose();
            try { _writer.Dispose(); } catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
        _reader.Dispose();
    }
}
