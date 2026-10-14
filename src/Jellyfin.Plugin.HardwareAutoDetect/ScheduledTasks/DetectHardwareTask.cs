using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.HardwareAutoDetect.Detection;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.HardwareAutoDetect.ScheduledTasks;

/// <summary>
/// Scheduled task that re-detects hardware and applies the result. No default triggers; the
/// startup behaviour is controlled by the plugin settings, this is for manual / custom schedules.
/// </summary>
public class DetectHardwareTask : IScheduledTask
{
    private readonly HardwareDetectionService _detection;

    public DetectHardwareTask(HardwareDetectionService detection)
    {
        _detection = detection;
    }

    /// <inheritdoc />
    public string Name => "Detect and apply hardware acceleration";

    /// <inheritdoc />
    public string Key => "HardwareAutoDetectApply";

    /// <inheritdoc />
    public string Description => "Probes the GPU(s) with ffmpeg and writes the working hardware acceleration settings to the transcoding configuration.";

    /// <inheritdoc />
    public string Category => "Hardware Acceleration Auto-Detect";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        await _detection.RunAsync(apply: true, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
