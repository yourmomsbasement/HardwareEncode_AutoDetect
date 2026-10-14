using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.HardwareAutoDetect.Detection;

/// <summary>
/// GPU vendors the detector knows how to drive.
/// </summary>
public enum GpuVendor
{
    Unknown,
    Intel,
    Amd,
    Nvidia,
    Apple,
    Rockchip,
}

/// <summary>
/// A GPU / media engine found on the host before any ffmpeg probing.
/// </summary>
/// <param name="Vendor">The vendor.</param>
/// <param name="Name">Human readable name (best effort).</param>
/// <param name="DevicePath">Render node on Linux (e.g. /dev/dri/renderD128); null elsewhere.</param>
/// <param name="KernelDriver">Linux kernel driver (i915, xe, amdgpu, nvidia...), if known.</param>
public sealed record GpuDevice(GpuVendor Vendor, string Name, string? DevicePath, string? KernelDriver);

/// <summary>
/// One accelerator configuration we will try with ffmpeg (e.g. "QSV on /dev/dri/renderD128").
/// </summary>
/// <param name="Type">The Jellyfin hardware acceleration type this maps to.</param>
/// <param name="Device">The device it runs on, if a specific one.</param>
public sealed record AcceleratorCandidate(HardwareAccelerationType Type, GpuDevice? Device)
{
    /// <summary>Gets a short display name for logs and the report.</summary>
    public string DisplayName => Device?.DevicePath is { } path
        ? $"{Type} ({Device.Vendor} @ {path})"
        : Device is not null ? $"{Type} ({Device.Vendor})" : Type.ToString();
}

/// <summary>
/// Codec feature keys that can be tested for hardware decode.
/// Keys that appear in <see cref="MediaBrowser.Model.Configuration.EncodingOptions.HardwareDecodingCodecs"/>
/// use Jellyfin's spelling (h264, hevc, mpeg2video, vc1, vp8, vp9, av1).
/// </summary>
public static class DecodeFeature
{
    public const string H264 = "h264";
    public const string Hevc = "hevc";
    public const string Hevc10 = "hevc10";
    public const string HevcRext10 = "hevcrext10";
    public const string HevcRext12 = "hevcrext12";
    public const string Mpeg2 = "mpeg2video";
    public const string Vc1 = "vc1";
    public const string Vp8 = "vp8";
    public const string Vp9 = "vp9";
    public const string Vp9_10 = "vp9_10";
    public const string Av1 = "av1";

    /// <summary>Gets the keys that map 1:1 onto Jellyfin's HardwareDecodingCodecs list.</summary>
    public static IReadOnlyList<string> JellyfinCodecKeys { get; } = [H264, Hevc, Mpeg2, Vc1, Vp8, Vp9, Av1];
}

/// <summary>
/// Codec feature keys for hardware encode.
/// </summary>
public static class EncodeFeature
{
    public const string H264 = "h264";
    public const string Hevc = "hevc";
    public const string Av1 = "av1";
    public const string H264LowPower = "h264_lowpower";
    public const string HevcLowPower = "hevc_lowpower";
}

/// <summary>
/// Tone-mapping paths Jellyfin exposes as separate toggles.
/// </summary>
public static class TonemapFeature
{
    /// <summary>OpenCL / CUDA tone mapping ("Enable Tone mapping").</summary>
    public const string Generic = "tonemap";

    /// <summary>Intel VPP tone mapping (tonemap_vaapi / vpp_qsv).</summary>
    public const string IntelVpp = "tonemap_vpp";

    /// <summary>VideoToolbox native tone mapping.</summary>
    public const string VideoToolbox = "tonemap_videotoolbox";
}

/// <summary>
/// Outcome of a single ffmpeg probe.
/// </summary>
public enum ProbeStatus
{
    Passed,
    Failed,

    /// <summary>Could not be tested (e.g. no software encoder to build a sample); treated as unsupported.</summary>
    Skipped,

    /// <summary>Not tested directly, enabled based on a known-good vendor capability.</summary>
    Inferred,
}

/// <summary>
/// Result of one probe for the report.
/// </summary>
public sealed class ProbeResult
{
    public string Feature { get; set; } = string.Empty;

    public ProbeStatus Status { get; set; }

    public string? Detail { get; set; }

    public bool IsSupported => Status is ProbeStatus.Passed or ProbeStatus.Inferred;
}

/// <summary>
/// Everything learned about one candidate accelerator.
/// </summary>
public sealed class AcceleratorProbeResult
{
    public AcceleratorProbeResult(AcceleratorCandidate candidate)
    {
        Candidate = candidate;
    }

    public AcceleratorCandidate Candidate { get; }

    public string Name => Candidate.DisplayName;

    public List<ProbeResult> Decode { get; set; } = [];

    public List<ProbeResult> Encode { get; set; } = [];

    public List<ProbeResult> Tonemap { get; set; } = [];

    public bool Supports(IEnumerable<ProbeResult> list, string feature)
        => list.Any(p => p.IsSupported && string.Equals(p.Feature, feature, StringComparison.Ordinal));

    public bool CanDecode(string feature) => Supports(Decode, feature);

    public bool CanEncode(string feature) => Supports(Encode, feature);

    public bool CanTonemap(string feature) => Supports(Tonemap, feature);

    /// <summary>Gets a value indicating whether this accelerator is usable at all.</summary>
    public bool IsUsable => CanEncode(EncodeFeature.H264) || Decode.Any(d => d.IsSupported);

    /// <summary>
    /// Gets a score used to pick the best accelerator. Encoding is weighted far above decoding because
    /// a hardware encoder is what actually relieves the CPU during transcodes.
    /// </summary>
    public int Score =>
        (CanEncode(EncodeFeature.H264) ? 100 : 0)
        + (CanEncode(EncodeFeature.Hevc) ? 30 : 0)
        + (CanEncode(EncodeFeature.Av1) ? 20 : 0)
        + (Decode.Count(d => d.IsSupported) * 5)
        + (Tonemap.Any(t => t.IsSupported) ? 10 : 0);
}

/// <summary>
/// Full output of a detection run.
/// </summary>
public sealed class DetectionReport
{
    public DateTime StartedUtc { get; set; }

    public DateTime FinishedUtc { get; set; }

    public string? FfmpegPath { get; set; }

    public string Platform { get; set; } = string.Empty;

    public List<GpuDevice> Devices { get; set; } = [];

    public List<AcceleratorProbeResult> Accelerators { get; set; } = [];

    /// <summary>Gets or sets the display name of the accelerator chosen (null = none usable).</summary>
    public string? Selected { get; set; }

    /// <summary>Gets or sets a value indicating whether the settings were written to encoding.xml.</summary>
    public bool Applied { get; set; }

    /// <summary>Gets or sets a human readable list of the settings that changed (or would change).</summary>
    public List<string> Changes { get; set; } = [];

    public List<string> Messages { get; set; } = [];

    /// <summary>Gets or sets a fingerprint of the detected hardware, used to only re-apply when it changes.</summary>
    public string HardwareFingerprint { get; set; } = string.Empty;
}
