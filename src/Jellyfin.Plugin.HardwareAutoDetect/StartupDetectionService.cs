using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.HardwareAutoDetect.Configuration;
using Jellyfin.Plugin.HardwareAutoDetect.Detection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HardwareAutoDetect;

/// <summary>
/// Runs detection after server startup according to <see cref="PluginConfiguration.ApplyMode"/>.
/// </summary>
public sealed class StartupDetectionService : BackgroundService
{
    // Give Jellyfin time to finish locating and validating ffmpeg before we use it.
    private static readonly TimeSpan _startupDelay = TimeSpan.FromSeconds(20);

    private readonly HardwareDetectionService _detection;
    private readonly ILogger<StartupDetectionService> _logger;

    public StartupDetectionService(HardwareDetectionService detection, ILogger<StartupDetectionService> logger)
    {
        _detection = detection;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_startupDelay, stoppingToken).ConfigureAwait(false);

            var config = Plugin.Instance?.Configuration;
            if (config is null || config.ApplyMode == ApplyMode.Manual)
            {
                return;
            }

            if (config.ApplyMode == ApplyMode.OnHardwareChange
                && string.Equals(config.LastAppliedFingerprint, _detection.ComputeFingerprint(), StringComparison.Ordinal))
            {
                _logger.LogDebug("Hardware unchanged since last auto-detect; skipping");
                return;
            }

            _logger.LogInformation("Running hardware acceleration auto-detect ({Mode})", config.ApplyMode);
            await _detection.RunAsync(apply: true, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hardware acceleration auto-detect failed");
        }
    }
}
