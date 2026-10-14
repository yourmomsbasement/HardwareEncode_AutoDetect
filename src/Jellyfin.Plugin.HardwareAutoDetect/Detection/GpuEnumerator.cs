using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Jellyfin.Plugin.HardwareAutoDetect.Detection;

/// <summary>
/// Finds GPUs / media engines present on the host. This is only a hint for which
/// accelerators to try: ffmpeg probing has the final say.
/// </summary>
public class GpuEnumerator
{
    private const string DisplayAdapterClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private readonly ILogger _logger;
    private readonly string _sysRoot;
    private readonly string _devRoot;
    private readonly string _procRoot;

    public GpuEnumerator(ILogger logger)
        : this(logger, "/sys", "/dev", "/proc")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GpuEnumerator"/> class with custom filesystem roots (for tests).
    /// </summary>
    internal GpuEnumerator(ILogger logger, string sysRoot, string devRoot, string procRoot)
    {
        _logger = logger;
        _sysRoot = sysRoot;
        _devRoot = devRoot;
        _procRoot = procRoot;
    }

    public static GpuVendor VendorFromPciId(string? vendorId)
    {
        var id = vendorId?.Trim().ToLowerInvariant().Replace("0x", string.Empty, StringComparison.Ordinal);
        return id switch
        {
            "8086" => GpuVendor.Intel,
            "1002" or "1022" => GpuVendor.Amd,
            "10de" => GpuVendor.Nvidia,
            _ => GpuVendor.Unknown,
        };
    }

    public virtual IReadOnlyList<GpuDevice> Enumerate()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return EnumerateLinux();
            }

            if (OperatingSystem.IsWindows())
            {
                return EnumerateWindows();
            }

            if (OperatingSystem.IsMacOS())
            {
                return [new GpuDevice(GpuVendor.Apple, "Apple VideoToolbox", null, null)];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "GPU enumeration failed; falling back to probing every accelerator");
        }

        return [];
    }

    /// <summary>
    /// Linux: walk /sys/class/drm/renderD* (works inside Docker as long as /dev/dri is passed through)
    /// plus NVIDIA's and Rockchip's own device nodes.
    /// </summary>
    internal IReadOnlyList<GpuDevice> EnumerateLinux()
    {
        var devices = new List<GpuDevice>();
        var drmDir = Path.Combine(_sysRoot, "class", "drm");
        if (Directory.Exists(drmDir))
        {
            foreach (var nodeDir in Directory.EnumerateFileSystemEntries(drmDir, "renderD*").Order(StringComparer.Ordinal))
            {
                var node = Path.GetFileName(nodeDir);
                var devicePath = Path.Combine(_devRoot, "dri", node);
                var vendorId = ReadTrimmed(Path.Combine(nodeDir, "device", "vendor"));
                var deviceId = ReadTrimmed(Path.Combine(nodeDir, "device", "device"));
                var driver = ReadDriver(Path.Combine(nodeDir, "device"));
                var vendor = VendorFromPciId(vendorId);

                // Non-PCI SoC GPUs (e.g. Rockchip's Mali / rockchip-drm) have no PCI vendor file.
                if (vendor == GpuVendor.Unknown && driver is not null && driver.Contains("rockchip", StringComparison.OrdinalIgnoreCase))
                {
                    vendor = GpuVendor.Rockchip;
                }

                var name = vendorId is null
                    ? $"{vendor} GPU ({driver ?? "unknown driver"})"
                    : string.Create(CultureInfo.InvariantCulture, $"{vendor} GPU [{Strip0x(vendorId)}:{Strip0x(deviceId)}] ({driver ?? "unknown driver"})");
                devices.Add(new GpuDevice(vendor, name, devicePath, driver));
            }
        }

        // NVIDIA's proprietary driver doesn't always expose a render node in containers, but CUDA still works.
        var hasNvidiaNode = devices.Any(d => d.Vendor == GpuVendor.Nvidia);
        if (!hasNvidiaNode
            && (File.Exists(Path.Combine(_devRoot, "nvidiactl")) || File.Exists(Path.Combine(_procRoot, "driver", "nvidia", "version"))))
        {
            devices.Add(new GpuDevice(GpuVendor.Nvidia, "NVIDIA GPU (nvidia driver)", null, "nvidia"));
        }

        if (File.Exists(Path.Combine(_devRoot, "mpp_service")) && devices.All(d => d.Vendor != GpuVendor.Rockchip))
        {
            devices.Add(new GpuDevice(GpuVendor.Rockchip, "Rockchip MPP", null, "rkvenc"));
        }

        return devices;
    }

    private IReadOnlyList<GpuDevice> EnumerateWindows()
    {
        var devices = new List<GpuDevice>();
        if (!OperatingSystem.IsWindows())
        {
            return devices;
        }

        using var classKey = Registry.LocalMachine.OpenSubKey(DisplayAdapterClassKey);
        if (classKey is null)
        {
            return devices;
        }

        foreach (var sub in classKey.GetSubKeyNames().Where(n => n.All(char.IsAsciiDigit)))
        {
            using var key = classKey.OpenSubKey(sub);
            var desc = key?.GetValue("DriverDesc") as string;
            var match = key?.GetValue("MatchingDeviceId") as string;
            if (desc is null || match is null)
            {
                continue;
            }

            // MatchingDeviceId looks like "pci\ven_10de&dev_2684".
            var venIdx = match.IndexOf("ven_", StringComparison.OrdinalIgnoreCase);
            var vendor = venIdx >= 0 && match.Length >= venIdx + 8
                ? VendorFromPciId(match.Substring(venIdx + 4, 4))
                : GpuVendor.Unknown;
            if (vendor != GpuVendor.Unknown)
            {
                devices.Add(new GpuDevice(vendor, desc, null, null));
            }
        }

        return devices;
    }

    private static string? ReadTrimmed(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadDriver(string deviceDir)
    {
        // Prefer the uevent DRIVER= line: it works even when the driver symlink can't be resolved.
        var uevent = ReadTrimmed(Path.Combine(deviceDir, "uevent"));
        if (uevent is not null)
        {
            foreach (var line in uevent.Split('\n'))
            {
                if (line.StartsWith("DRIVER=", StringComparison.Ordinal))
                {
                    return line["DRIVER=".Length..].Trim();
                }
            }
        }

        try
        {
            var link = new FileInfo(Path.Combine(deviceDir, "driver")).LinkTarget;
            return link is null ? null : Path.GetFileName(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Strip0x(string? id) => (id ?? "????").Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
}
