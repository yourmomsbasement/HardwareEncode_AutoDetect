using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HardwareAutoDetect.Detection;

/// <summary>
/// Generates tiny test clips (software-encoded) that the hardware decoders are then asked to decode.
/// </summary>
public sealed class SampleLibrary
{
    private const string Source = "testsrc2=size=640x360:rate=25";

    // feature -> list of (pix_fmt) variants that must ALL decode for the feature to count as supported.
    private static readonly (string Feature, string Codec, string[] PixFmts)[] _definitions =
    [
        (DecodeFeature.H264, "h264", ["yuv420p"]),
        (DecodeFeature.Hevc, "hevc", ["yuv420p"]),
        (DecodeFeature.Hevc10, "hevc", ["yuv420p10le"]),

        // Jellyfin's "10-bit HEVC RExt" toggle covers 4:2:2 and 4:4:4 up to 10 bit,
        // the 12-bit toggle covers every 12-bit chroma layout.
        (DecodeFeature.HevcRext10, "hevc", ["yuv422p10le", "yuv444p10le"]),
        (DecodeFeature.HevcRext12, "hevc", ["yuv420p12le", "yuv422p12le", "yuv444p12le"]),
        (DecodeFeature.Mpeg2, "mpeg2video", ["yuv420p"]),
        (DecodeFeature.Vp8, "vp8", ["yuv420p"]),
        (DecodeFeature.Vp9, "vp9", ["yuv420p"]),
        (DecodeFeature.Vp9_10, "vp9", ["yuv420p10le"]),
        (DecodeFeature.Av1, "av1", ["yuv420p"]),
    ];

    // Software encoders to try for each codec, in order, with fast settings.
    private static readonly Dictionary<string, (string Encoder, string[] Args)[]> _encoders = new()
    {
        ["h264"] = [("libx264", ["-preset", "ultrafast"])],
        ["hevc"] = [("libx265", ["-preset", "ultrafast", "-x265-params", "log-level=error"])],
        ["mpeg2video"] = [("mpeg2video", [])],
        ["vp8"] = [("libvpx", ["-deadline", "realtime", "-cpu-used", "8"])],
        ["vp9"] = [("libvpx-vp9", ["-deadline", "realtime", "-cpu-used", "8"])],
        ["av1"] =
        [
            ("libsvtav1", ["-preset", "12"]),
            ("libaom-av1", ["-cpu-used", "8", "-usage", "realtime"]),
            ("librav1e", ["-speed", "10"]),
        ],
    };

    private readonly IProcessRunner _runner;
    private readonly ILogger _logger;
    private readonly string _ffmpegPath;
    private readonly TimeSpan _timeout;

    public SampleLibrary(IProcessRunner runner, ILogger logger, string ffmpegPath, TimeSpan timeout)
    {
        _runner = runner;
        _logger = logger;
        _ffmpegPath = ffmpegPath;
        _timeout = timeout;
    }

    /// <summary>
    /// Gets the generated sample paths per decode feature. A feature that is missing here
    /// could not be generated (no suitable software encoder) and is reported as skipped.
    /// </summary>
    public Dictionary<string, IReadOnlyList<string>> Samples { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the reason a feature's sample could not be generated.</summary>
    public Dictionary<string, string> Failures { get; } = new(StringComparer.Ordinal);

    public async Task GenerateAsync(string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        foreach (var (feature, codec, pixFmts) in _definitions)
        {
            var paths = new List<string>();
            string? failure = null;
            foreach (var pixFmt in pixFmts)
            {
                var path = Path.Combine(directory, $"{codec}_{pixFmt}.mkv");
                if (!File.Exists(path))
                {
                    failure = await EncodeAsync(codec, pixFmt, path, cancellationToken).ConfigureAwait(false);
                    if (failure is not null)
                    {
                        break;
                    }
                }

                paths.Add(path);
            }

            if (failure is null)
            {
                Samples[feature] = paths;
            }
            else
            {
                Failures[feature] = failure;
                _logger.LogDebug("Could not generate {Feature} sample: {Reason}", feature, failure);
            }
        }
    }

    private async Task<string?> EncodeAsync(string codec, string pixFmt, string path, CancellationToken cancellationToken)
    {
        string? lastError = null;
        foreach (var (encoder, extra) in _encoders[codec])
        {
            List<string> args =
            [
                "-hide_banner", "-nostdin", "-v", "error", "-y",
                "-f", "lavfi", "-i", Source,
                "-frames:v", "10", "-g", "10",
                "-pix_fmt", pixFmt,
                "-c:v", encoder, .. extra,
                path,
            ];
            var result = await _runner.RunAsync(_ffmpegPath, args, _timeout, cancellationToken).ConfigureAwait(false);
            if (result.Success && File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return null;
            }

            TryDelete(path);
            lastError = $"{encoder} ({pixFmt}): {FirstLine(result.Output)}";
        }

        return lastError;
    }

    internal static string FirstLine(string text)
    {
        foreach (var entry in text.Split('\n'))
        {
            var line = entry.Trim();
            if (line.Length == 0 || line.StartsWith("libva info:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line.Length > 300 ? line[..300] : line;
        }

        return string.Empty;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
