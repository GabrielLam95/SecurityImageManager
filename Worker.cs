using Microsoft.Extensions.Options;

namespace FtpMirrorService;

public sealed class Worker(
    FtpMirror mirror,
    IOptions<FtpMirrorOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.Interval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await mirror.SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "FTP mirror run failed.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
