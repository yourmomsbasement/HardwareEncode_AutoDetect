using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.HardwareAutoDetect.Detection;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.HardwareAutoDetect.Tests;

/// <summary>
/// Runs the real command lines against the ffmpeg on PATH (if any). On a machine without a GPU this
/// verifies the sample clips build and that a missing device is reported as unusable rather than passing.
/// </summary>
public sealed class RealFfmpegTests : IDisposable
{
    private static readonly string? _ffmpeg = new[] { Environment.GetEnvironmentVariable("HWAD_FFMPEG"), "/usr/lib/jellyfin-ffmpeg/ffmpeg", "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg" }
        .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hwad-real-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public async Task SamplesBuild_AndMissingDeviceIsNotUsable()
    {
        if (_ffmpeg is null)
        {
            return; // No ffmpeg on this machine.
        }

        var runner = new ProcessRunner();
        var timeout = TimeSpan.FromSeconds(60);
        var samples = new SampleLibrary(runner, NullLogger.Instance, _ffmpeg, timeout);
        await samples.GenerateAsync(_dir, CancellationToken.None);

        // Any stock ffmpeg can build these.
        Assert.Contains(DecodeFeature.H264, samples.Samples.Keys);
        Assert.Contains(DecodeFeature.Mpeg2, samples.Samples.Keys);

        // A render node that doesn't exist must fail at device init, not slip through.
        var prober = new HardwareProber(runner, NullLogger.Instance, _ffmpeg, timeout, isWindows: false);
        var result = await prober.ProbeAsync(
            new AcceleratorCandidate(HardwareAccelerationType.vaapi, new GpuDevice(GpuVendor.Amd, "none", "/dev/dri/renderD999", null)),
            samples,
            inferVc1: true,
            CancellationToken.None);
        Assert.False(result.IsUsable);
    }
}
