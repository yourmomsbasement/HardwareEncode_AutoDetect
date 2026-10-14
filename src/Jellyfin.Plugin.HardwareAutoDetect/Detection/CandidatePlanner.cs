using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.HardwareAutoDetect.Detection;

/// <summary>
/// Turns the enumerated devices into the ordered list of accelerators to probe.
/// Order doubles as the tie-break preference when two accelerators score the same.
/// </summary>
public static class CandidatePlanner
{
    public enum HostOs
    {
        Linux,
        Windows,
        MacOS,
        Other,
    }

    public static IReadOnlyList<AcceleratorCandidate> Plan(IReadOnlyList<GpuDevice> devices, HostOs os)
    {
        var list = new List<AcceleratorCandidate>();
        switch (os)
        {
            case HostOs.Linux:
                // CUDA addresses the NVIDIA GPU by index, so one candidate covers it.
                var nvidia = devices.FirstOrDefault(d => d.Vendor == GpuVendor.Nvidia);
                if (nvidia is not null)
                {
                    list.Add(new AcceleratorCandidate(HardwareAccelerationType.nvenc, nvidia));
                }

                foreach (var intel in devices.Where(d => d.Vendor == GpuVendor.Intel && d.DevicePath is not null))
                {
                    // QSV is Jellyfin's recommended path for Intel; plain VA-API is the fallback
                    // for GPUs too old for the iHD driver (pre-Broadwell).
                    list.Add(new AcceleratorCandidate(HardwareAccelerationType.qsv, intel));
                    list.Add(new AcceleratorCandidate(HardwareAccelerationType.vaapi, intel));
                }

                foreach (var other in devices.Where(d => d.Vendor is GpuVendor.Amd or GpuVendor.Unknown && d.DevicePath is not null))
                {
                    list.Add(new AcceleratorCandidate(HardwareAccelerationType.vaapi, other));
                }

                var rockchip = devices.FirstOrDefault(d => d.Vendor == GpuVendor.Rockchip);
                if (rockchip is not null)
                {
                    list.Add(new AcceleratorCandidate(HardwareAccelerationType.rkmpp, rockchip));
                }

                if (list.Count == 0)
                {
                    // Nothing visible (e.g. restricted /sys in a container): try the common defaults blind.
                    list.Add(new AcceleratorCandidate(HardwareAccelerationType.nvenc, null));
                    list.Add(new AcceleratorCandidate(HardwareAccelerationType.qsv, new GpuDevice(GpuVendor.Intel, "Default render node", "/dev/dri/renderD128", null)));
                    list.Add(new AcceleratorCandidate(HardwareAccelerationType.vaapi, new GpuDevice(GpuVendor.Unknown, "Default render node", "/dev/dri/renderD128", null)));
                }

                break;

            case HostOs.Windows:
                var known = devices.Select(d => d.Vendor).ToHashSet();
                var all = known.Count == 0;
                if (all || known.Contains(GpuVendor.Nvidia))
                {
                    list.Add(new AcceleratorCandidate(HardwareAccelerationType.nvenc, devices.FirstOrDefault(d => d.Vendor == GpuVendor.Nvidia)));
                }

                if (all || known.Contains(GpuVendor.Intel))
                {
                    list.Add(new AcceleratorCandidate(HardwareAccelerationType.qsv, devices.FirstOrDefault(d => d.Vendor == GpuVendor.Intel)));
                }

                if (all || known.Contains(GpuVendor.Amd))
                {
                    list.Add(new AcceleratorCandidate(HardwareAccelerationType.amf, devices.FirstOrDefault(d => d.Vendor == GpuVendor.Amd)));
                }

                break;

            case HostOs.MacOS:
                list.Add(new AcceleratorCandidate(HardwareAccelerationType.videotoolbox, devices.FirstOrDefault()));
                break;
        }

        return list;
    }

    /// <summary>
    /// Picks the best usable accelerator: highest score, earliest in plan order on ties.
    /// </summary>
    /// <param name="results">Probe results, in plan order.</param>
    /// <param name="preferred">An accelerator type the admin wants used if it works, or null for automatic.</param>
    /// <returns>The chosen result, or null if nothing is usable.</returns>
    public static AcceleratorProbeResult? Choose(IReadOnlyList<AcceleratorProbeResult> results, HardwareAccelerationType? preferred)
    {
        var usable = results.Where(r => r.IsUsable).ToList();
        if (preferred is { } p)
        {
            var match = usable.Where(r => r.Candidate.Type == p).OrderByDescending(r => r.Score).FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        // OrderByDescending is stable, so plan order breaks ties.
        return usable.OrderByDescending(r => r.Score).FirstOrDefault();
    }
}
