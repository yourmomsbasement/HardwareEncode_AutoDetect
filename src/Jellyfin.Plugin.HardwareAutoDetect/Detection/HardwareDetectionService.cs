using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.HardwareAutoDetect.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HardwareAutoDetect.Detection;

/// <summary>
/// Orchestrates enumeration, probing, selection and applying the result to Jellyfin's encoding settings.
/// </summary>
public sealed class HardwareDetectionService : IDisposable
{
    private const string EncodingKey = "encoding";
    private const string ReportFile = "last-report.json";
    private const string BackupFile = "encoding-backup.json";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IConfigurationManager _configurationManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<HardwareDetectionService> _logger;
    private readonly IProcessRunner _runner;
    private readonly GpuEnumerator _enumerator;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public HardwareDetectionService(
        IConfigurationManager configurationManager,
        IMediaEncoder mediaEncoder,
        ILogger<HardwareDetectionService> logger)
        : this(configurationManager, mediaEncoder, logger, new ProcessRunner(), new GpuEnumerator(logger))
    {
    }

    internal HardwareDetectionService(
        IConfigurationManager configurationManager,
        IMediaEncoder mediaEncoder,
        ILogger<HardwareDetectionService> logger,
        IProcessRunner runner,
        GpuEnumerator enumerator)
    {
        _configurationManager = configurationManager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        _runner = runner;
        _enumerator = enumerator;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static string DataFolder
    {
        get
        {
            var dir = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "jellyfin-hwautodetect");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static CandidatePlanner.HostOs CurrentOs =>
        OperatingSystem.IsLinux() ? CandidatePlanner.HostOs.Linux
        : OperatingSystem.IsWindows() ? CandidatePlanner.HostOs.Windows
        : OperatingSystem.IsMacOS() ? CandidatePlanner.HostOs.MacOS
        : CandidatePlanner.HostOs.Other;

    /// <summary>
    /// Computes a cheap fingerprint of the GPUs and ffmpeg build, without running any probes.
    /// </summary>
    public string ComputeFingerprint() => Fingerprint(_enumerator.Enumerate(), _mediaEncoder.EncoderPath, _mediaEncoder.EncoderVersion);

    internal static string Fingerprint(IEnumerable<GpuDevice> devices, string? ffmpegPath, Version? ffmpegVersion)
    {
        var text = string.Join('|', devices.Select(d => $"{d.Vendor}:{d.Name}:{d.DevicePath}:{d.KernelDriver}"))
            + $"#{ffmpegPath}#{ffmpegVersion}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    /// <summary>
    /// Runs detection.
    /// </summary>
    /// <param name="apply">Whether to write the result into Jellyfin's encoding settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report.</returns>
    public async Task<DetectionReport> RunAsync(bool apply, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var report = await RunCoreAsync(apply, cancellationToken).ConfigureAwait(false);
            SaveReport(report);
            return report;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<DetectionReport> RunCoreAsync(bool apply, CancellationToken cancellationToken)
    {
        var config = Config;
        var report = new DetectionReport
        {
            StartedUtc = DateTime.UtcNow,
            FfmpegPath = _mediaEncoder.EncoderPath,
            Platform = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})",
        };

        if (string.IsNullOrEmpty(report.FfmpegPath) || !File.Exists(report.FfmpegPath))
        {
            report.Messages.Add("ffmpeg was not found. Check Dashboard → Playback → Transcoding → FFmpeg path.");
            report.FinishedUtc = DateTime.UtcNow;
            return report;
        }

        report.Devices = _enumerator.Enumerate().ToList();
        report.HardwareFingerprint = Fingerprint(report.Devices, report.FfmpegPath, _mediaEncoder.EncoderVersion);
        _logger.LogInformation("Hardware auto-detect: found {Count} device(s): {Devices}", report.Devices.Count, string.Join("; ", report.Devices.Select(d => d.Name)));

        var candidates = CandidatePlanner.Plan(report.Devices, CurrentOs);
        if (candidates.Count == 0)
        {
            report.Messages.Add("No hardware accelerators are supported on this platform.");
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(config.ProbeTimeoutSeconds, 5, 300));
        var workDir = Path.Combine(Path.GetTempPath(), "jellyfin-hwautodetect-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            if (candidates.Count > 0)
            {
                var samples = new SampleLibrary(_runner, _logger, report.FfmpegPath, timeout);
                await samples.GenerateAsync(workDir, cancellationToken).ConfigureAwait(false);
                foreach (var (feature, why) in samples.Failures)
                {
                    report.Messages.Add($"Could not create a {feature} test clip, so it was not tested: {why}");
                }

                var prober = new HardwareProber(_runner, _logger, report.FfmpegPath, timeout, OperatingSystem.IsWindows());
                foreach (var candidate in candidates)
                {
                    report.Accelerators.Add(await prober.ProbeAsync(candidate, samples, config.InferVc1, cancellationToken).ConfigureAwait(false));
                }
            }
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }

        HardwareAccelerationType? preferred = Enum.TryParse<HardwareAccelerationType>(config.PreferredAccelerator, true, out var p) && p != HardwareAccelerationType.none ? p : null;
        var best = CandidatePlanner.Choose(report.Accelerators, preferred);
        report.Selected = best?.Name;
        if (best is null)
        {
            report.Messages.Add("No working hardware acceleration was found." + (config.DisableWhenNoneFound ? " Hardware acceleration will be turned off." : " Existing settings are left unchanged."));
        }
        else if (preferred is not null && best.Candidate.Type != preferred)
        {
            report.Messages.Add($"Preferred accelerator {preferred} did not work; using {best.Name} instead.");
        }

        var encoding = _configurationManager.GetEncodingOptions();
        var before = ManagedEncodingSettings.From(encoding);
        var desired = EncodingSettingsMapper.Map(before, best, config, OperatingSystem.IsLinux());
        if (desired is not null)
        {
            report.Changes = ManagedEncodingSettings.Diff(before, desired);
        }

        if (apply && desired is not null)
        {
            if (report.Changes.Count > 0)
            {
                BackupOnce(before);
                desired.ApplyTo(encoding);
                _configurationManager.SaveConfiguration(EncodingKey, encoding);
                _logger.LogInformation("Hardware auto-detect applied {Accelerator}: {Changes}", best?.Name ?? "none", string.Join("; ", report.Changes));
            }

            report.Applied = true;
        }

        if (apply && Plugin.Instance is { } plugin)
        {
            plugin.Configuration.LastAppliedFingerprint = report.HardwareFingerprint;
            plugin.SaveConfiguration();
        }

        report.FinishedUtc = DateTime.UtcNow;
        return report;
    }

    public DetectionReport? LoadLastReport()
    {
        var path = Path.Combine(DataFolder, ReportFile);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DetectionReport>(File.ReadAllText(path), _json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Could not read the last hardware detection report");
            return null;
        }
    }

    public bool HasBackup => File.Exists(Path.Combine(DataFolder, BackupFile));

    /// <summary>
    /// Restores the encoding settings that were in place before this plugin first changed them.
    /// </summary>
    /// <returns>The list of changes made, or null if there was no backup.</returns>
    public List<string>? RestoreBackup()
    {
        var path = Path.Combine(DataFolder, BackupFile);
        if (!File.Exists(path))
        {
            return null;
        }

        var backup = JsonSerializer.Deserialize<ManagedEncodingSettings>(File.ReadAllText(path), _json)
            ?? throw new InvalidDataException("Backup file is empty");
        var encoding = _configurationManager.GetEncodingOptions();
        var changes = ManagedEncodingSettings.Diff(ManagedEncodingSettings.From(encoding), backup);
        backup.ApplyTo(encoding);
        _configurationManager.SaveConfiguration(EncodingKey, encoding);
        File.Delete(path);

        // Don't immediately re-apply on the next startup.
        if (Plugin.Instance is { } plugin && plugin.Configuration.ApplyMode == ApplyMode.EveryStartup)
        {
            plugin.Configuration.ApplyMode = ApplyMode.Manual;
            plugin.SaveConfiguration();
        }

        _logger.LogInformation("Hardware auto-detect restored previous encoding settings: {Changes}", string.Join("; ", changes));
        return changes;
    }

    private void BackupOnce(ManagedEncodingSettings before)
    {
        var path = Path.Combine(DataFolder, BackupFile);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(before, _json));
        }
    }

    private void SaveReport(DetectionReport report)
    {
        try
        {
            File.WriteAllText(Path.Combine(DataFolder, ReportFile), JsonSerializer.Serialize(report, _json));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not save the hardware detection report");
        }
    }

    private void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Dir}", dir);
        }
    }

    public void Dispose() => _lock.Dispose();
}
