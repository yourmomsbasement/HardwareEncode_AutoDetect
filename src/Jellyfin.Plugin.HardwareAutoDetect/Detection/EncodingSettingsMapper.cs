using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.HardwareAutoDetect.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.HardwareAutoDetect.Detection;

/// <summary>
/// The subset of <see cref="EncodingOptions"/> this plugin manages. Used for diffing and for the
/// backup that "Restore" puts back.
/// </summary>
public sealed class ManagedEncodingSettings
{
    public HardwareAccelerationType HardwareAccelerationType { get; set; }

    public string? VaapiDevice { get; set; }

    public string? QsvDevice { get; set; }

    public string[] HardwareDecodingCodecs { get; set; } = [];

    public bool EnableDecodingColorDepth10Hevc { get; set; }

    public bool EnableDecodingColorDepth10Vp9 { get; set; }

    public bool EnableDecodingColorDepth10HevcRext { get; set; }

    public bool EnableDecodingColorDepth12HevcRext { get; set; }

    public bool EnableHardwareEncoding { get; set; }

    public bool AllowHevcEncoding { get; set; }

    public bool AllowAv1Encoding { get; set; }

    public bool EnableIntelLowPowerH264HwEncoder { get; set; }

    public bool EnableIntelLowPowerHevcHwEncoder { get; set; }

    public bool EnableTonemapping { get; set; }

    public bool EnableVppTonemapping { get; set; }

    public bool EnableVideoToolboxTonemapping { get; set; }

    public static ManagedEncodingSettings From(EncodingOptions o) => new()
    {
        HardwareAccelerationType = o.HardwareAccelerationType,
        VaapiDevice = o.VaapiDevice,
        QsvDevice = o.QsvDevice,
        HardwareDecodingCodecs = o.HardwareDecodingCodecs ?? [],
        EnableDecodingColorDepth10Hevc = o.EnableDecodingColorDepth10Hevc,
        EnableDecodingColorDepth10Vp9 = o.EnableDecodingColorDepth10Vp9,
        EnableDecodingColorDepth10HevcRext = o.EnableDecodingColorDepth10HevcRext,
        EnableDecodingColorDepth12HevcRext = o.EnableDecodingColorDepth12HevcRext,
        EnableHardwareEncoding = o.EnableHardwareEncoding,
        AllowHevcEncoding = o.AllowHevcEncoding,
        AllowAv1Encoding = o.AllowAv1Encoding,
        EnableIntelLowPowerH264HwEncoder = o.EnableIntelLowPowerH264HwEncoder,
        EnableIntelLowPowerHevcHwEncoder = o.EnableIntelLowPowerHevcHwEncoder,
        EnableTonemapping = o.EnableTonemapping,
        EnableVppTonemapping = o.EnableVppTonemapping,
        EnableVideoToolboxTonemapping = o.EnableVideoToolboxTonemapping,
    };

    public void ApplyTo(EncodingOptions o)
    {
        o.HardwareAccelerationType = HardwareAccelerationType;
        o.VaapiDevice = VaapiDevice ?? string.Empty;
        o.QsvDevice = QsvDevice ?? string.Empty;
        o.HardwareDecodingCodecs = HardwareDecodingCodecs;
        o.EnableDecodingColorDepth10Hevc = EnableDecodingColorDepth10Hevc;
        o.EnableDecodingColorDepth10Vp9 = EnableDecodingColorDepth10Vp9;
        o.EnableDecodingColorDepth10HevcRext = EnableDecodingColorDepth10HevcRext;
        o.EnableDecodingColorDepth12HevcRext = EnableDecodingColorDepth12HevcRext;
        o.EnableHardwareEncoding = EnableHardwareEncoding;
        o.AllowHevcEncoding = AllowHevcEncoding;
        o.AllowAv1Encoding = AllowAv1Encoding;
        o.EnableIntelLowPowerH264HwEncoder = EnableIntelLowPowerH264HwEncoder;
        o.EnableIntelLowPowerHevcHwEncoder = EnableIntelLowPowerHevcHwEncoder;
        o.EnableTonemapping = EnableTonemapping;
        o.EnableVppTonemapping = EnableVppTonemapping;
        o.EnableVideoToolboxTonemapping = EnableVideoToolboxTonemapping;
    }

    /// <summary>
    /// Lists human readable differences between two settings snapshots.
    /// </summary>
    public static List<string> Diff(ManagedEncodingSettings before, ManagedEncodingSettings after)
    {
        var changes = new List<string>();
        foreach (var prop in typeof(ManagedEncodingSettings).GetProperties())
        {
            var a = Format(prop.GetValue(before));
            var b = Format(prop.GetValue(after));
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                changes.Add($"{prop.Name}: {a} → {b}");
            }
        }

        return changes;
    }

    private static string Format(object? value) => value switch
    {
        null => "(empty)",
        string s when s.Length == 0 => "(empty)",
        string[] arr => arr.Length == 0 ? "(none)" : string.Join(", ", arr),
        bool b => b ? "on" : "off",
        _ => value.ToString() ?? string.Empty,
    };
}

/// <summary>
/// Maps a probe result onto Jellyfin's encoding settings.
/// </summary>
public static class EncodingSettingsMapper
{
    /// <summary>
    /// Computes the settings to write. Settings this plugin doesn't manage are left untouched.
    /// </summary>
    /// <param name="current">The current settings.</param>
    /// <param name="best">The chosen accelerator, or null if none works.</param>
    /// <param name="config">Plugin preferences.</param>
    /// <param name="isLinux">Whether the host is Linux (QSV device path only applies there).</param>
    /// <returns>The desired settings, or null when nothing should be changed.</returns>
    public static ManagedEncodingSettings? Map(ManagedEncodingSettings current, AcceleratorProbeResult? best, PluginConfiguration config, bool isLinux)
    {
        if (best is null)
        {
            if (!config.DisableWhenNoneFound)
            {
                return null;
            }

            return new ManagedEncodingSettings
            {
                HardwareAccelerationType = HardwareAccelerationType.none,
                VaapiDevice = current.VaapiDevice,
                QsvDevice = current.QsvDevice,
                HardwareDecodingCodecs = [],
            };
        }

        var type = best.Candidate.Type;
        var devicePath = best.Candidate.Device?.DevicePath;
        var isIntelPath = type == HardwareAccelerationType.qsv
            || (type == HardwareAccelerationType.vaapi && best.Candidate.Device?.Vendor == GpuVendor.Intel);

        return new ManagedEncodingSettings
        {
            HardwareAccelerationType = type,
            VaapiDevice = type == HardwareAccelerationType.vaapi && devicePath is not null ? devicePath : current.VaapiDevice,
            QsvDevice = type == HardwareAccelerationType.qsv && isLinux && devicePath is not null ? devicePath : current.QsvDevice,
            HardwareDecodingCodecs = DecodeFeature.JellyfinCodecKeys.Where(best.CanDecode).ToArray(),
            EnableDecodingColorDepth10Hevc = best.CanDecode(DecodeFeature.Hevc10),
            EnableDecodingColorDepth10Vp9 = best.CanDecode(DecodeFeature.Vp9_10),
            EnableDecodingColorDepth10HevcRext = best.CanDecode(DecodeFeature.HevcRext10),
            EnableDecodingColorDepth12HevcRext = best.CanDecode(DecodeFeature.HevcRext12),
            EnableHardwareEncoding = best.CanEncode(EncodeFeature.H264),

            // These only allow HEVC/AV1 *output*; without a hardware encoder they'd mean slow software encodes.
            AllowHevcEncoding = config.AllowHevcEncoding && best.CanEncode(EncodeFeature.Hevc),
            AllowAv1Encoding = config.AllowAv1Encoding && best.CanEncode(EncodeFeature.Av1),
            EnableIntelLowPowerH264HwEncoder = isIntelPath && best.CanEncode(EncodeFeature.H264LowPower),
            EnableIntelLowPowerHevcHwEncoder = isIntelPath && best.CanEncode(EncodeFeature.HevcLowPower),
            EnableTonemapping = config.EnableTonemapping && best.CanTonemap(TonemapFeature.Generic),
            EnableVppTonemapping = config.EnableTonemapping && isIntelPath && best.CanTonemap(TonemapFeature.IntelVpp),
            EnableVideoToolboxTonemapping = config.EnableTonemapping && best.CanTonemap(TonemapFeature.VideoToolbox),
        };
    }
}
