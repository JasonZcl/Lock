namespace Lock.Service;

/// <summary>
/// 托管服务入口：启动进程监控与管道服务端。
/// </summary>
public sealed class LockWorker : BackgroundService
{
    private ConfigManager? _config;
    private AgentHub? _agents;
    private LockEngine? _engine;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ServiceLog.Info("服务启动");

        _config = new ConfigManager();
        _agents = new AgentHub();
        _engine = new LockEngine(_config, _agents);
        _engine.Start();

        var server = new PipeServer(_config, _agents, _engine);
        try
        {
            await server.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            ServiceLog.Error("管道服务异常退出", ex);
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        _engine?.Dispose();
        ServiceLog.Info("服务停止");
    }
}
