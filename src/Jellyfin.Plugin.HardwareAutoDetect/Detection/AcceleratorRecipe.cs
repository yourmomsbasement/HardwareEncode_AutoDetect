using System;
using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.HardwareAutoDetect.Detection;

/// <summary>
/// One tone-mapping test: the device arguments plus the filter chain to run on a synthetic HDR10 frame.
/// </summary>
/// <param name="Feature">The <see cref="TonemapFeature"/> key.</param>
/// <param name="DeviceArgs">Arguments placed before the input.</param>
/// <param name="Filter">The -vf chain (after the HDR setparams prefix).</param>
public sealed record TonemapTest(string Feature, IReadOnlyList<string> DeviceArgs, string Filter);

/// <summary>
/// The ffmpeg arguments needed to exercise one accelerator. These mirror the device initialisation
/// Jellyfin's own EncodingHelper uses, so a passing probe means Jellyfin's real transcodes will work.
/// </summary>
public sealed class AcceleratorRecipe
{
    private AcceleratorRecipe(AcceleratorCandidate candidate)
    {
        Candidate = candidate;
    }

    public AcceleratorCandidate Candidate { get; }

    /// <summary>Gets device init args shared by decode and encode tests.</summary>
    public IReadOnlyList<string> DeviceArgs { get; private init; } = [];

    /// <summary>Gets the args (placed after <see cref="DeviceArgs"/>, before -i) that route decoding through the hwaccel.</summary>
    public IReadOnlyList<string> DecodeArgs { get; private init; } = [];

    /// <summary>Gets extra args for encode tests (e.g. -filter_hw_device).</summary>
    public IReadOnlyList<string> EncodeDeviceArgs { get; private init; } = [];

    /// <summary>Gets the -vf chain that prepares frames for the encoder.</summary>
    public string EncodeFilter { get; private init; } = "format=yuv420p";

    /// <summary>Gets the ffmpeg encoder names keyed by <see cref="EncodeFeature"/>.</summary>
    public IReadOnlyDictionary<string, string> Encoders { get; private init; } = new Dictionary<string, string>();

    /// <summary>Gets a value indicating whether Intel low-power (VDEnc) encoding should be probed.</summary>
    public bool ProbeLowPower { get; private init; }

    public IReadOnlyList<TonemapTest> TonemapTests { get; private init; } = [];

    /// <summary>
    /// Builds the recipe for a candidate on the given OS.
    /// </summary>
    /// <param name="candidate">The candidate.</param>
    /// <param name="isWindows">Whether the host is Windows (QSV and AMF use D3D11 there).</param>
    /// <returns>The recipe.</returns>
    public static AcceleratorRecipe For(AcceleratorCandidate candidate, bool isWindows)
    {
        var node = candidate.Device?.DevicePath ?? "/dev/dri/renderD128";
        var isIntel = candidate.Device?.Vendor == GpuVendor.Intel;

        switch (candidate.Type)
        {
            case HardwareAccelerationType.nvenc:
                string[] cuda = ["-init_hw_device", "cuda=cu:0"];
                return new AcceleratorRecipe(candidate)
                {
                    DeviceArgs = cuda,
                    DecodeArgs = ["-hwaccel", "cuda", "-hwaccel_device", "cu", "-hwaccel_output_format", "cuda"],
                    Encoders = Encs("h264_nvenc", "hevc_nvenc", "av1_nvenc"),
                    TonemapTests = [new TonemapTest(TonemapFeature.Generic, [.. cuda, "-filter_hw_device", "cu"], "format=p010le,hwupload,tonemap_cuda=format=nv12:tonemap=bt2390")],
                };

            case HardwareAccelerationType.qsv when isWindows:
                string[] qsvWin = ["-init_hw_device", "d3d11va=dx11:,vendor=0x8086", "-init_hw_device", "qsv=qs@dx11"];
                return new AcceleratorRecipe(candidate)
                {
                    DeviceArgs = qsvWin,
                    DecodeArgs = ["-hwaccel", "d3d11va", "-hwaccel_device", "dx11", "-hwaccel_output_format", "d3d11"],
                    EncodeDeviceArgs = ["-filter_hw_device", "qs"],
                    EncodeFilter = "format=nv12,hwupload=extra_hw_frames=64",
                    Encoders = Encs("h264_qsv", "hevc_qsv", "av1_qsv"),
                    ProbeLowPower = true,
                    TonemapTests =
                    [
                        new TonemapTest(TonemapFeature.Generic, ["-init_hw_device", "d3d11va=dx11:,vendor=0x8086", "-init_hw_device", "opencl=ocl@dx11", "-filter_hw_device", "ocl"], "format=p010le,hwupload,tonemap_opencl=format=nv12:tonemap=bt2390"),
                        new TonemapTest(TonemapFeature.IntelVpp, [.. qsvWin, "-filter_hw_device", "qs"], "format=p010le,hwupload=extra_hw_frames=64,vpp_qsv=tonemap=1:format=nv12"),
                    ],
                };

            case HardwareAccelerationType.qsv:
                // Jellyfin derives QSV from a VA-API device forced onto the iHD driver.
                string[] vaIhd = ["-init_hw_device", $"vaapi=va:{node},driver=iHD"];
                return new AcceleratorRecipe(candidate)
                {
                    DeviceArgs = [.. vaIhd, "-init_hw_device", "qsv=qs@va"],
                    DecodeArgs = ["-hwaccel", "vaapi", "-hwaccel_device", "va", "-hwaccel_output_format", "vaapi"],
                    EncodeDeviceArgs = ["-filter_hw_device", "qs"],
                    EncodeFilter = "format=nv12,hwupload=extra_hw_frames=64",
                    Encoders = Encs("h264_qsv", "hevc_qsv", "av1_qsv"),
                    ProbeLowPower = true,
                    TonemapTests =
                    [
                        new TonemapTest(TonemapFeature.Generic, [.. vaIhd, "-init_hw_device", "opencl=ocl@va", "-filter_hw_device", "ocl"], "format=p010le,hwupload,tonemap_opencl=format=nv12:tonemap=bt2390"),
                        new TonemapTest(TonemapFeature.IntelVpp, [.. vaIhd, "-filter_hw_device", "va"], "format=p010le,hwupload,tonemap_vaapi=format=nv12:t=bt709:m=bt709:p=bt709"),
                    ],
                };

            case HardwareAccelerationType.vaapi:
                string[] va = ["-init_hw_device", $"vaapi=va:{node}"];
                var vaTonemaps = new List<TonemapTest>
                {
                    new(TonemapFeature.Generic, [.. va, "-init_hw_device", "opencl=ocl@va", "-filter_hw_device", "ocl"], "format=p010le,hwupload,tonemap_opencl=format=nv12:tonemap=bt2390"),
                };
                if (isIntel)
                {
                    vaTonemaps.Add(new(TonemapFeature.IntelVpp, [.. va, "-filter_hw_device", "va"], "format=p010le,hwupload,tonemap_vaapi=format=nv12:t=bt709:m=bt709:p=bt709"));
                }

                return new AcceleratorRecipe(candidate)
                {
                    DeviceArgs = va,
                    DecodeArgs = ["-hwaccel", "vaapi", "-hwaccel_device", "va", "-hwaccel_output_format", "vaapi"],
                    EncodeDeviceArgs = ["-filter_hw_device", "va"],
                    EncodeFilter = "format=nv12,hwupload",
                    Encoders = Encs("h264_vaapi", "hevc_vaapi", "av1_vaapi"),
                    ProbeLowPower = isIntel,
                    TonemapTests = vaTonemaps,
                };

            case HardwareAccelerationType.amf:
                string[] dxAmd = ["-init_hw_device", "d3d11va=dx11:,vendor=0x1002"];
                return new AcceleratorRecipe(candidate)
                {
                    DeviceArgs = dxAmd,
                    DecodeArgs = ["-hwaccel", "d3d11va", "-hwaccel_device", "dx11", "-hwaccel_output_format", "d3d11"],
                    Encoders = Encs("h264_amf", "hevc_amf", "av1_amf"),
                    TonemapTests = [new TonemapTest(TonemapFeature.Generic, [.. dxAmd, "-init_hw_device", "opencl=ocl@dx11", "-filter_hw_device", "ocl"], "format=p010le,hwupload,tonemap_opencl=format=nv12:tonemap=bt2390")],
                };

            case HardwareAccelerationType.videotoolbox:
                string[] vt = ["-init_hw_device", "videotoolbox=vt"];
                return new AcceleratorRecipe(candidate)
                {
                    DeviceArgs = vt,
                    DecodeArgs = ["-hwaccel", "videotoolbox", "-hwaccel_output_format", "videotoolbox_vld"],
                    Encoders = new Dictionary<string, string>
                    {
                        [EncodeFeature.H264] = "h264_videotoolbox",
                        [EncodeFeature.Hevc] = "hevc_videotoolbox",
                    },
                    TonemapTests = [new TonemapTest(TonemapFeature.VideoToolbox, [.. vt, "-filter_hw_device", "vt"], "format=p010le,hwupload,tonemap_videotoolbox=format=nv12")],
                };

            case HardwareAccelerationType.rkmpp:
                return new AcceleratorRecipe(candidate)
                {
                    DeviceArgs = ["-init_hw_device", "rkmpp=rk"],
                    DecodeArgs = ["-hwaccel", "rkmpp", "-hwaccel_device", "rk", "-hwaccel_output_format", "drm_prime"],
                    Encoders = Encs("h264_rkmpp", "hevc_rkmpp", null),
                    TonemapTests = [new TonemapTest(TonemapFeature.Generic, ["-init_hw_device", "opencl=ocl", "-filter_hw_device", "ocl"], "format=p010le,hwupload,tonemap_opencl=format=nv12:tonemap=bt2390")],
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(candidate), candidate.Type, "No probe recipe for this accelerator");
        }
    }

    private static Dictionary<string, string> Encs(string h264, string hevc, string? av1)
    {
        var d = new Dictionary<string, string>
        {
            [EncodeFeature.H264] = h264,
            [EncodeFeature.Hevc] = hevc,
        };
        if (av1 is not null)
        {
            d[EncodeFeature.Av1] = av1;
        }

        return d;
    }
}
