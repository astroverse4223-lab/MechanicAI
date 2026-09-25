using System.Threading.Channels;
using MechanicAI.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Infrastructure.Platform;

public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<BackgroundWorkItem> _channel = Channel.CreateUnbounded<BackgroundWorkItem>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });

    private int _pending;

    public int PendingCount => Volatile.Read(ref _pending);

    public event EventHandler<BackgroundProgress>? ProgressChanged;

    public async ValueTask QueueAsync(BackgroundWorkItem item, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _pending);
        await _channel.Writer.WriteAsync(item, ct);
        ReportProgress(new BackgroundProgress(item.CorrelationId, item.Name, "Queued", null));
    }

    public async ValueTask<BackgroundWorkItem> DequeueAsync(CancellationToken ct)
    {
        var item = await _channel.Reader.ReadAsync(ct);
        Interlocked.Decrement(ref _pending);
        return item;
    }

    public void ReportProgress(BackgroundProgress progress) => ProgressChanged?.Invoke(this, progress);
}

/// <summary>Runs queued work items (document indexing, etc.) off the UI thread, two at a time.</summary>
public sealed class BackgroundTaskProcessor(
    IBackgroundTaskQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<BackgroundTaskProcessor> logger) : BackgroundService
{
    private const int Workers = 2;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => RunWorkerAsync(stoppingToken)));

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            BackgroundWorkItem item;
            try
            {
                item = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                queue.ReportProgress(new BackgroundProgress(item.CorrelationId, item.Name, "Started", 0));
                await using var scope = scopeFactory.CreateAsyncScope();
                await item.Work(scope.ServiceProvider, stoppingToken);
                queue.ReportProgress(new BackgroundProgress(item.CorrelationId, item.Name, "Completed", 1, Completed: true));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background work item {Name} failed", item.Name);
                queue.ReportProgress(new BackgroundProgress(item.CorrelationId, item.Name, ex.Message, null, Completed: true, Failed: true));
            }
        }
    }
}
