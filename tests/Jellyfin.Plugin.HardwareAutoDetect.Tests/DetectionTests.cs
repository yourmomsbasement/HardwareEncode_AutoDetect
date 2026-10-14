using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.HardwareAutoDetect.Configuration;
using Jellyfin.Plugin.HardwareAutoDetect.Detection;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.HardwareAutoDetect.Tests;

public sealed class GpuEnumeratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hwad-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private void AddRenderNode(string node, string? vendor, string driver)
    {
        var dev = Path.Combine(_root, "sys", "class", "drm", node, "device");
        Directory.CreateDirectory(dev);
        if (vendor is not null)
        {
            File.WriteAllText(Path.Combine(dev, "vendor"), vendor + "\n");
            File.WriteAllText(Path.Combine(dev, "device"), "0x1234\n");
        }

        File.WriteAllText(Path.Combine(dev, "uevent"), $"DRIVER={driver}\nPCI_CLASS=30000\n");
    }

    private GpuEnumerator Create()
    {
        Directory.CreateDirectory(Path.Combine(_root, "dev"));
        Directory.CreateDirectory(Path.Combine(_root, "proc"));
        return new GpuEnumerator(NullLogger.Instance, Path.Combine(_root, "sys"), Path.Combine(_root, "dev"), Path.Combine(_root, "proc"));
    }

    [Fact]
    public void Linux_ReadsRenderNodesFromSysfs()
    {
        AddRenderNode("renderD128", "0x8086", "i915");
        AddRenderNode("renderD129", "0x1002", "amdgpu");
        var devices = Create().EnumerateLinux();

        Assert.Equal(2, devices.Count);
        Assert.Equal(GpuVendor.Intel, devices[0].Vendor);
        Assert.Equal(Path.Combine(_root, "dev", "dri", "renderD128"), devices[0].DevicePath);
        Assert.Equal("i915", devices[0].KernelDriver);
        Assert.Equal(GpuVendor.Amd, devices[1].Vendor);
    }

    [Fact]
    public void Linux_DetectsNvidiaWithoutRenderNode()
    {
        var enumerator = Create();
        File.WriteAllText(Path.Combine(_root, "dev", "nvidiactl"), string.Empty);
        var devices = enumerator.EnumerateLinux();

        var nv = Assert.Single(devices);
        Assert.Equal(GpuVendor.Nvidia, nv.Vendor);
        Assert.Null(nv.DevicePath);
    }

    [Fact]
    public void Linux_DetectsRockchip()
    {
        AddRenderNode("renderD128", null, "rockchip-drm");
        var devices = Create().EnumerateLinux();
        Assert.Equal(GpuVendor.Rockchip, Assert.Single(devices).Vendor);
    }

    [Theory]
    [InlineData("0x8086", GpuVendor.Intel)]
    [InlineData("10DE", GpuVendor.Nvidia)]
    [InlineData("0x1002", GpuVendor.Amd)]
    [InlineData("0x5143", GpuVendor.Unknown)]
    [InlineData(null, GpuVendor.Unknown)]
    public void VendorFromPciId(string? id, GpuVendor expected) => Assert.Equal(expected, GpuEnumerator.VendorFromPciId(id));
}

public class CandidatePlannerTests
{
    private static readonly GpuDevice _intel = new(GpuVendor.Intel, "Intel", "/dev/dri/renderD128", "i915");
    private static readonly GpuDevice _amd = new(GpuVendor.Amd, "AMD", "/dev/dri/renderD129", "amdgpu");
    private static readonly GpuDevice _nvidia = new(GpuVendor.Nvidia, "NVIDIA", "/dev/dri/renderD130", "nvidia");

    [Fact]
    public void Linux_PlansEveryVendorInPreferenceOrder()
    {
        var plan = CandidatePlanner.Plan([_intel, _amd, _nvidia], CandidatePlanner.HostOs.Linux);
        Assert.Equal(
            [HardwareAccelerationType.nvenc, HardwareAccelerationType.qsv, HardwareAccelerationType.vaapi, HardwareAccelerationType.vaapi],
            plan.Select(c => c.Type));
        Assert.Equal(_amd, plan[3].Device);
    }

    [Fact]
    public void Linux_NoDevices_TriesDefaults()
    {
        var plan = CandidatePlanner.Plan([], CandidatePlanner.HostOs.Linux);
        Assert.Contains(plan, c => c.Type == HardwareAccelerationType.nvenc);
        Assert.Contains(plan, c => c.Type == HardwareAccelerationType.vaapi && c.Device!.DevicePath == "/dev/dri/renderD128");
    }

    [Fact]
    public void Windows_UsesVendorSpecificApis()
    {
        var plan = CandidatePlanner.Plan([new GpuDevice(GpuVendor.Amd, "Radeon", null, null)], CandidatePlanner.HostOs.Windows);
        Assert.Equal(HardwareAccelerationType.amf, Assert.Single(plan).Type);

        var blind = CandidatePlanner.Plan([], CandidatePlanner.HostOs.Windows);
        Assert.Equal([HardwareAccelerationType.nvenc, HardwareAccelerationType.qsv, HardwareAccelerationType.amf], blind.Select(c => c.Type));
    }

    [Fact]
    public void Choose_PrefersHighestScoreThenPlanOrder()
    {
        var qsv = Result(HardwareAccelerationType.qsv, encode: [EncodeFeature.H264, EncodeFeature.Hevc]);
        var vaapi = Result(HardwareAccelerationType.vaapi, encode: [EncodeFeature.H264, EncodeFeature.Hevc]);
        var broken = Result(HardwareAccelerationType.nvenc);

        Assert.Same(qsv, CandidatePlanner.Choose([broken, qsv, vaapi], null));

        var better = Result(HardwareAccelerationType.vaapi, encode: [EncodeFeature.H264, EncodeFeature.Hevc, EncodeFeature.Av1]);
        Assert.Same(better, CandidatePlanner.Choose([qsv, better], null));
        Assert.Null(CandidatePlanner.Choose([broken], null));
    }

    [Fact]
    public void Choose_HonoursPreferenceOnlyWhenUsable()
    {
        var qsv = Result(HardwareAccelerationType.qsv, encode: [EncodeFeature.H264, EncodeFeature.Hevc, EncodeFeature.Av1]);
        var vaapi = Result(HardwareAccelerationType.vaapi, encode: [EncodeFeature.H264]);
        var nvenc = Result(HardwareAccelerationType.nvenc);

        Assert.Same(vaapi, CandidatePlanner.Choose([qsv, vaapi], HardwareAccelerationType.vaapi));
        Assert.Same(qsv, CandidatePlanner.Choose([nvenc, qsv], HardwareAccelerationType.nvenc));
    }

    internal static AcceleratorProbeResult Result(HardwareAccelerationType type, string[]? encode = null, string[]? decode = null, string[]? tonemap = null, GpuDevice? device = null)
    {
        var r = new AcceleratorProbeResult(new AcceleratorCandidate(type, device));
        r.Encode.AddRange((encode ?? []).Select(f => new ProbeResult { Feature = f, Status = ProbeStatus.Passed }));
        r.Decode.AddRange((decode ?? []).Select(f => new ProbeResult { Feature = f, Status = ProbeStatus.Passed }));
        r.Tonemap.AddRange((tonemap ?? []).Select(f => new ProbeResult { Feature = f, Status = ProbeStatus.Passed }));
        return r;
    }
}

public class EncodingSettingsMapperTests
{
    private static readonly GpuDevice _intel = new(GpuVendor.Intel, "Intel", "/dev/dri/renderD129", "i915");

    [Fact]
    public void Qsv_MapsEveryCapability()
    {
        var best = CandidatePlannerTests.Result(
            HardwareAccelerationType.qsv,
            encode: [EncodeFeature.H264, EncodeFeature.Hevc, EncodeFeature.Av1, EncodeFeature.H264LowPower, EncodeFeature.HevcLowPower],
            decode: [DecodeFeature.H264, DecodeFeature.Hevc, DecodeFeature.Hevc10, DecodeFeature.HevcRext10, DecodeFeature.Vp9, DecodeFeature.Vp9_10, DecodeFeature.Av1, DecodeFeature.Mpeg2, DecodeFeature.Vc1],
            tonemap: [TonemapFeature.Generic, TonemapFeature.IntelVpp],
            device: _intel);
        var config = new PluginConfiguration { AllowAv1Encoding = true };

        var s = EncodingSettingsMapper.Map(new ManagedEncodingSettings(), best, config, isLinux: true)!;

        Assert.Equal(HardwareAccelerationType.qsv, s.HardwareAccelerationType);
        Assert.Equal("/dev/dri/renderD129", s.QsvDevice);
        Assert.Equal(["h264", "hevc", "mpeg2video", "vc1", "vp9", "av1"], s.HardwareDecodingCodecs);
        Assert.True(s.EnableDecodingColorDepth10Hevc);
        Assert.True(s.EnableDecodingColorDepth10Vp9);
        Assert.True(s.EnableDecodingColorDepth10HevcRext);
        Assert.False(s.EnableDecodingColorDepth12HevcRext);
        Assert.True(s.EnableHardwareEncoding);
        Assert.True(s.AllowHevcEncoding);
        Assert.True(s.AllowAv1Encoding);
        Assert.True(s.EnableIntelLowPowerH264HwEncoder);
        Assert.True(s.EnableIntelLowPowerHevcHwEncoder);
        Assert.True(s.EnableTonemapping);
        Assert.True(s.EnableVppTonemapping);
        Assert.False(s.EnableVideoToolboxTonemapping);
    }

    [Fact]
    public void Nvenc_DisablesUnsupportedAndIntelOnlyOptions()
    {
        var current = new ManagedEncodingSettings
        {
            VaapiDevice = "/dev/dri/renderD128",
            AllowHevcEncoding = true,
            AllowAv1Encoding = true,
            EnableIntelLowPowerH264HwEncoder = true,
            EnableVppTonemapping = true,
        };
        var best = CandidatePlannerTests.Result(HardwareAccelerationType.nvenc, encode: [EncodeFeature.H264], decode: [DecodeFeature.H264]);

        var s = EncodingSettingsMapper.Map(current, best, new PluginConfiguration { AllowAv1Encoding = true }, isLinux: true)!;

        Assert.Equal("/dev/dri/renderD128", s.VaapiDevice);
        Assert.False(s.AllowHevcEncoding);
        Assert.False(s.AllowAv1Encoding);
        Assert.False(s.EnableIntelLowPowerH264HwEncoder);
        Assert.False(s.EnableVppTonemapping);
        Assert.False(s.EnableTonemapping);
        Assert.Equal(["h264"], s.HardwareDecodingCodecs);
    }

    [Fact]
    public void Preferences_CanOptOutOfHevcAndTonemapping()
    {
        var best = CandidatePlannerTests.Result(HardwareAccelerationType.nvenc, encode: [EncodeFeature.H264, EncodeFeature.Hevc], tonemap: [TonemapFeature.Generic]);
        var s = EncodingSettingsMapper.Map(new ManagedEncodingSettings(), best, new PluginConfiguration { AllowHevcEncoding = false, EnableTonemapping = false }, true)!;
        Assert.False(s.AllowHevcEncoding);
        Assert.False(s.EnableTonemapping);
    }

    [Fact]
    public void NothingFound_LeavesSettingsAloneByDefault()
    {
        var current = new ManagedEncodingSettings { HardwareAccelerationType = HardwareAccelerationType.vaapi };
        Assert.Null(EncodingSettingsMapper.Map(current, null, new PluginConfiguration(), true));

        var off = EncodingSettingsMapper.Map(current, null, new PluginConfiguration { DisableWhenNoneFound = true }, true)!;
        Assert.Equal(HardwareAccelerationType.none, off.HardwareAccelerationType);
    }

    [Fact]
    public void Diff_ListsOnlyChangedFields()
    {
        var a = new ManagedEncodingSettings { HardwareDecodingCodecs = ["h264"] };
        var b = new ManagedEncodingSettings { HardwareAccelerationType = HardwareAccelerationType.qsv, HardwareDecodingCodecs = ["h264", "hevc"] };
        var diff = ManagedEncodingSettings.Diff(a, b);
        Assert.Equal(2, diff.Count);
        Assert.Contains("HardwareAccelerationType: none → qsv", diff);
        Assert.Contains("HardwareDecodingCodecs: h264 → h264, hevc", diff);
    }
}

/// <summary>
/// Simulates an ffmpeg + GPU that supports a fixed set of encoders / sample files.
/// </summary>
internal sealed class FakeFfmpeg : IProcessRunner
{
    public HashSet<string> WorkingEncoders { get; } = [];

    /// <summary>Gets sample file names (without extension) that decode in hardware.</summary>
    public HashSet<string> WorkingSamples { get; } = [];

    public HashSet<string> WorkingFilters { get; } = [];

    public bool DeviceWorks { get; set; } = true;

    public bool LowPowerWorks { get; set; }

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add(arguments);
        var args = arguments.ToList();
        var encoder = Value(args, "-c:v");
        var input = Value(args, "-i")!;

        // Sample generation: the last arg is the output file.
        if (args[^1].EndsWith(".mkv", StringComparison.Ordinal))
        {
            File.WriteAllText(args[^1], "x");
            return Done(true);
        }

        if (args.Contains("-init_hw_device") && !DeviceWorks)
        {
            return Done(false, "Device creation failed: -22.");
        }

        if (input.StartsWith("nullsrc", StringComparison.Ordinal))
        {
            return Done(true);
        }

        if (input.EndsWith(".mkv", StringComparison.Ordinal))
        {
            var ok = WorkingSamples.Contains(Path.GetFileNameWithoutExtension(input));
            return Done(ok, ok ? string.Empty : "Failed setup for format vaapi: hwaccel initialisation returned error.");
        }

        var vf = Value(args, "-vf") ?? string.Empty;
        if (vf.Contains("tonemap", StringComparison.Ordinal))
        {
            return Done(WorkingFilters.Any(vf.Contains), "No such filter");
        }

        var lowPower = args.Contains("-low_power");
        return Done(encoder is not null && WorkingEncoders.Contains(encoder) && (!lowPower || LowPowerWorks), "Error while opening encoder");
    }

    private static string? Value(List<string> args, string key)
    {
        var i = args.LastIndexOf(key);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    private static Task<ProcessResult> Done(bool ok, string output = "") => Task.FromResult(new ProcessResult(ok ? 0 : 1, output, false));
}

public sealed class HardwareProberTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hwad-probe-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private async Task<AcceleratorProbeResult> Probe(FakeFfmpeg ffmpeg, AcceleratorCandidate candidate, bool inferVc1 = true)
    {
        var samples = new SampleLibrary(ffmpeg, NullLogger.Instance, "ffmpeg", TimeSpan.FromSeconds(5));
        await samples.GenerateAsync(_dir, CancellationToken.None);
        var prober = new HardwareProber(ffmpeg, NullLogger.Instance, "ffmpeg", TimeSpan.FromSeconds(5), isWindows: false);
        return await prober.ProbeAsync(candidate, samples, inferVc1, CancellationToken.None);
    }

    [Fact]
    public async Task IntelQsv_ReportsExactlyWhatWorks()
    {
        var ffmpeg = new FakeFfmpeg { LowPowerWorks = true };
        ffmpeg.WorkingEncoders.UnionWith(["h264_qsv", "hevc_qsv"]);
        ffmpeg.WorkingSamples.UnionWith(["h264_yuv420p", "hevc_yuv420p", "hevc_yuv420p10le", "hevc_yuv422p10le", "mpeg2video_yuv420p", "vp9_yuv420p"]);
        ffmpeg.WorkingFilters.Add("tonemap_vaapi");

        var r = await Probe(ffmpeg, new AcceleratorCandidate(HardwareAccelerationType.qsv, new GpuDevice(GpuVendor.Intel, "Intel", "/dev/dri/renderD128", "i915")));

        Assert.True(r.CanEncode(EncodeFeature.H264));
        Assert.True(r.CanEncode(EncodeFeature.Hevc));
        Assert.False(r.CanEncode(EncodeFeature.Av1));
        Assert.True(r.CanEncode(EncodeFeature.H264LowPower));
        Assert.True(r.CanDecode(DecodeFeature.Hevc10));

        // RExt 10-bit needs both 4:2:2 and 4:4:4; only 4:2:2 works here.
        Assert.False(r.CanDecode(DecodeFeature.HevcRext10));
        Assert.False(r.CanDecode(DecodeFeature.Vp9_10));
        Assert.False(r.CanDecode(DecodeFeature.Av1));
        Assert.Equal(ProbeStatus.Inferred, r.Decode.Single(d => d.Feature == DecodeFeature.Vc1).Status);
        Assert.True(r.CanTonemap(TonemapFeature.IntelVpp));
        Assert.False(r.CanTonemap(TonemapFeature.Generic));

        // QSV on Linux must use Jellyfin's iHD-derived device.
        Assert.Contains(ffmpeg.Calls, c => c.Contains("vaapi=va:/dev/dri/renderD128,driver=iHD") && c.Contains("qsv=qs@va"));
    }

    [Fact]
    public async Task DeviceInitFailure_StopsEarly()
    {
        var ffmpeg = new FakeFfmpeg { DeviceWorks = false };
        var r = await Probe(ffmpeg, new AcceleratorCandidate(HardwareAccelerationType.nvenc, null));

        Assert.False(r.IsUsable);
        Assert.Empty(r.Decode);
        Assert.Contains("Device creation failed", r.Encode.Single().Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hevc10_SkippedWhenHevcFails()
    {
        var ffmpeg = new FakeFfmpeg();
        ffmpeg.WorkingEncoders.Add("h264_nvenc");
        ffmpeg.WorkingSamples.UnionWith(["h264_yuv420p", "hevc_yuv420p10le"]);
        var r = await Probe(ffmpeg, new AcceleratorCandidate(HardwareAccelerationType.nvenc, null), inferVc1: false);

        Assert.Equal(ProbeStatus.Skipped, r.Decode.Single(d => d.Feature == DecodeFeature.Hevc10).Status);
        Assert.False(r.CanDecode(DecodeFeature.Vc1));
    }

    [Fact]
    public async Task Av1Decode_ForcesNativeDecoder()
    {
        var ffmpeg = new FakeFfmpeg();
        ffmpeg.WorkingEncoders.Add("h264_vaapi");
        var r = await Probe(ffmpeg, new AcceleratorCandidate(HardwareAccelerationType.vaapi, new GpuDevice(GpuVendor.Amd, "AMD", "/dev/dri/renderD128", "amdgpu")));

        var av1Call = ffmpeg.Calls.Single(c => c.Any(a => a.EndsWith("av1_yuv420p.mkv", StringComparison.Ordinal)) && c.Contains("-hwaccel"));
        var list = av1Call.ToList();
        Assert.Equal("av1", list[list.IndexOf("-c:v") + 1]);
        Assert.True(list.IndexOf("-c:v") < list.IndexOf("-i"));

        // AMD: no low-power or VPP probes.
        Assert.DoesNotContain(ffmpeg.Calls, c => c.Contains("-low_power"));
        Assert.DoesNotContain(r.Tonemap, t => t.Feature == TonemapFeature.IntelVpp);
    }
}
