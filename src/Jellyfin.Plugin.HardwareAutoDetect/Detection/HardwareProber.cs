using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HardwareAutoDetect.Detection;

/// <summary>
/// Runs real ffmpeg decode / encode / tone-mapping jobs against one accelerator.
/// </summary>
public sealed class HardwareProber
{
    // A synthetic HDR10 (PQ / BT.2020) source so the tone-mapping filters have something to map.
    private const string HdrPrefix = "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc:range=tv,";

    // ffmpeg prints these when it silently falls back to software decoding.
    private static readonly string[] _fallbackMarkers =
    [
        "Failed setup for format",
        "hwaccel initialisation returned error",
        "No support for codec",
        "not supported by the hardware",
    ];

    private readonly IProcessRunner _runner;
    private readonly ILogger _logger;
    private readonly string _ffmpegPath;
    private readonly TimeSpan _timeout;
    private readonly bool _isWindows;

    public HardwareProber(IProcessRunner runner, ILogger logger, string ffmpegPath, TimeSpan timeout, bool isWindows)
    {
        _runner = runner;
        _logger = logger;
        _ffmpegPath = ffmpegPath;
        _timeout = timeout;
        _isWindows = isWindows;
    }

    public async Task<AcceleratorProbeResult> ProbeAsync(
        AcceleratorCandidate candidate,
        SampleLibrary samples,
        bool inferVc1,
        CancellationToken cancellationToken)
    {
        var recipe = AcceleratorRecipe.For(candidate, _isWindows);
        var result = new AcceleratorProbeResult(candidate);
        _logger.LogInformation("Probing {Accelerator}", candidate.DisplayName);

        // Cheap gate first: if the device can't even be opened there's no point running 20 more jobs.
        var init = await RunAsync([.. recipe.DeviceArgs, "-f", "lavfi", "-i", "nullsrc=s=64x64", "-frames:v", "1", "-f", "null", "-"], cancellationToken).ConfigureAwait(false);
        if (!init.Success)
        {
            var reason = "Device init failed: " + SampleLibrary.FirstLine(init.Output);
            _logger.LogInformation("{Accelerator}: {Reason}", candidate.DisplayName, reason);
            result.Encode.Add(new ProbeResult { Feature = EncodeFeature.H264, Status = ProbeStatus.Failed, Detail = reason });
            return result;
        }

        await ProbeEncodersAsync(recipe, result, cancellationToken).ConfigureAwait(false);
        await ProbeDecodersAsync(recipe, samples, result, cancellationToken).ConfigureAwait(false);

        // VC-1 has no software encoder in ffmpeg, so we can't build a sample. Intel and NVIDIA have
        // decoded VC-1 in hardware on every generation that can decode MPEG-2, so infer it from that.
        var vendor = candidate.Device?.Vendor ?? (candidate.Type == HardwareAccelerationType.nvenc ? GpuVendor.Nvidia : GpuVendor.Unknown);
        if (inferVc1 && result.CanDecode(DecodeFeature.Mpeg2) && vendor is GpuVendor.Intel or GpuVendor.Nvidia)
        {
            result.Decode.Add(new ProbeResult { Feature = DecodeFeature.Vc1, Status = ProbeStatus.Inferred, Detail = "No VC-1 software encoder to build a sample; inferred from MPEG-2 support on " + vendor });
        }
        else
        {
            result.Decode.Add(new ProbeResult { Feature = DecodeFeature.Vc1, Status = ProbeStatus.Skipped, Detail = "VC-1 cannot be tested (no software encoder) and is not inferred for this vendor" });
        }

        if (result.IsUsable)
        {
            foreach (var test in recipe.TonemapTests)
            {
                var r = await RunAsync(
                    [.. test.DeviceArgs, "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=1", "-frames:v", "1", "-vf", HdrPrefix + test.Filter, "-f", "null", "-"],
                    cancellationToken).ConfigureAwait(false);
                result.Tonemap.Add(ToProbe(test.Feature, r));
            }
        }

        _logger.LogInformation(
            "{Accelerator}: encode [{Encode}] decode [{Decode}] tonemap [{Tonemap}]",
            candidate.DisplayName,
            Summary(result.Encode),
            Summary(result.Decode),
            Summary(result.Tonemap));
        return result;
    }

    private async Task ProbeEncodersAsync(AcceleratorRecipe recipe, AcceleratorProbeResult result, CancellationToken cancellationToken)
    {
        foreach (var (feature, encoder) in recipe.Encoders)
        {
            var r = await RunAsync(EncodeArgs(recipe, encoder, lowPower: false), cancellationToken).ConfigureAwait(false);
            result.Encode.Add(ToProbe(feature, r, encoder));
        }

        if (recipe.ProbeLowPower)
        {
            foreach (var (feature, lpFeature) in new[] { (EncodeFeature.H264, EncodeFeature.H264LowPower), (EncodeFeature.Hevc, EncodeFeature.HevcLowPower) })
            {
                if (!result.CanEncode(feature))
                {
                    continue;
                }

                var r = await RunAsync(EncodeArgs(recipe, recipe.Encoders[feature], lowPower: true), cancellationToken).ConfigureAwait(false);
                result.Encode.Add(ToProbe(lpFeature, r, recipe.Encoders[feature] + " -low_power 1"));
            }
        }
    }

    private async Task ProbeDecodersAsync(AcceleratorRecipe recipe, SampleLibrary samples, AcceleratorProbeResult result, CancellationToken cancellationToken)
    {
        string[] features =
        [
            DecodeFeature.H264, DecodeFeature.Hevc, DecodeFeature.Hevc10, DecodeFeature.HevcRext10, DecodeFeature.HevcRext12,
            DecodeFeature.Mpeg2, DecodeFeature.Vp8, DecodeFeature.Vp9, DecodeFeature.Vp9_10, DecodeFeature.Av1,
        ];

        foreach (var feature in features)
        {
            // Higher bit depth / RExt variants are only meaningful if the base codec decodes.
            var parent = feature switch
            {
                DecodeFeature.Hevc10 or DecodeFeature.HevcRext10 or DecodeFeature.HevcRext12 => DecodeFeature.Hevc,
                DecodeFeature.Vp9_10 => DecodeFeature.Vp9,
                _ => null,
            };
            if (parent is not null && !result.CanDecode(parent))
            {
                result.Decode.Add(new ProbeResult { Feature = feature, Status = ProbeStatus.Skipped, Detail = $"{parent} decode not supported" });
                continue;
            }

            if (!samples.Samples.TryGetValue(feature, out var files))
            {
                samples.Failures.TryGetValue(feature, out var why);
                result.Decode.Add(new ProbeResult { Feature = feature, Status = ProbeStatus.Skipped, Detail = "No sample: " + why });
                continue;
            }

            ProbeResult? outcome = null;
            foreach (var file in files)
            {
                List<string> args = [.. recipe.DeviceArgs, .. recipe.DecodeArgs];

                // Force the native AV1 decoder so ffmpeg doesn't pick libdav1d, which ignores -hwaccel.
                if (feature == DecodeFeature.Av1)
                {
                    args.AddRange(["-c:v", "av1"]);
                }

                // hwdownload only accepts hardware frames, so a silent software fallback fails.
                // Its output must match the surface's software format. Without an explicit
                // format, the null sink can negotiate monow and reject a working decoder.
                foreach (var format in DownloadFormats(file))
                {
                    var r = await RunAsync(
                        [.. args, "-i", file, "-vf", $"hwdownload,format={format}", "-f", "null", "-"],
                        cancellationToken).ConfigureAwait(false);
                    outcome = ToProbe(feature, r, System.IO.Path.GetFileNameWithoutExtension(file));
                    if (outcome.IsSupported || r.TimedOut || !IsDownloadFormatMismatch(r.Output))
                    {
                        break;
                    }
                }
                if (!outcome!.IsSupported)
                {
                    break;
                }
            }

            result.Decode.Add(outcome!);
        }
    }

    private static IReadOnlyList<string> DownloadFormats(string file)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(file);
        var pixelFormat = name[(name.LastIndexOf('_') + 1)..];
        // VA-API/D3D11 use packed or semi-planar surfaces; CUDA can expose planar
        // surfaces instead. Retry only format negotiation, never a failed decoder.
        return pixelFormat switch
        {
            "yuv420p" => ["nv12", "yuv420p"],
            "yuv420p10le" => ["p010le", "yuv420p10le"],
            "yuv422p10le" => ["y210le", "p210le", "yuv422p10le"],
            "yuv444p10le" => ["yuv444p16le", "xv30le", "yuv444p10le"],
            "yuv420p12le" => ["p012le", "p016le", "yuv420p12le"],
            "yuv422p12le" => ["y212le", "p212le", "yuv422p12le"],
            "yuv444p12le" => ["yuv444p16le", "xv36le", "yuv444p12le"],
            _ => [pixelFormat],
        };
    }

    private static bool IsDownloadFormatMismatch(string output)
        => output.Contains("Invalid output format", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Invalid pixel format", StringComparison.OrdinalIgnoreCase);

    private static List<string> EncodeArgs(AcceleratorRecipe recipe, string encoder, bool lowPower)
    {
        List<string> args =
        [
            .. recipe.DeviceArgs, .. recipe.EncodeDeviceArgs,
            "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30",
            "-frames:v", "15",
            "-vf", recipe.EncodeFilter,
            "-c:v", encoder,
        ];
        if (lowPower)
        {
            args.AddRange(["-low_power", "1"]);
        }

        args.AddRange(["-f", "null", "-"]);
        return args;
    }

    private async Task<ProcessResult> RunAsync(List<string> args, CancellationToken cancellationToken)
    {
        args.InsertRange(0, ["-hide_banner", "-nostdin", "-v", "error"]);
        var r = await _runner.RunAsync(_ffmpegPath, args, _timeout, cancellationToken).ConfigureAwait(false);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("ffmpeg {Args} -> {Code}{Timeout} {Output}", string.Join(' ', args), r.ExitCode, r.TimedOut ? " (timeout)" : string.Empty, r.Output.Trim());
        }

        return r;
    }

    private static ProbeResult ToProbe(string feature, ProcessResult r, string? what = null)
    {
        var fellBack = _fallbackMarkers.Any(m => r.Output.Contains(m, StringComparison.OrdinalIgnoreCase));
        var ok = r.Success && !fellBack;
        string? detail = ok
            ? what
            : r.TimedOut
                ? $"{what}: timed out"
                : $"{what}: {SampleLibrary.FirstLine(r.Output)}".TrimStart(':', ' ');
        return new ProbeResult { Feature = feature, Status = ok ? ProbeStatus.Passed : ProbeStatus.Failed, Detail = detail };
    }

    private static string Summary(IEnumerable<ProbeResult> list)
        => string.Join(", ", list.Where(p => p.IsSupported).Select(p => p.Feature));
}
