using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.HardwareAutoDetect.Configuration;

/// <summary>
/// When detection runs on its own.
/// </summary>
public enum ApplyMode
{
    /// <summary>Only when the admin clicks "Detect" (or runs the scheduled task).</summary>
    Manual,

    /// <summary>At startup, but only if the GPUs or ffmpeg changed since the last apply.</summary>
    OnHardwareChange,

    /// <summary>At every server startup.</summary>
    EveryStartup,
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public ApplyMode ApplyMode { get; set; } = ApplyMode.OnHardwareChange;

    /// <summary>Gets or sets the preferred accelerator ("auto" or a HardwareAccelerationType name). Used when it works; otherwise auto.</summary>
    public string PreferredAccelerator { get; set; } = "auto";

    /// <summary>Gets or sets a value indicating whether HEVC output is allowed when the GPU can encode it.</summary>
    public bool AllowHevcEncoding { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether AV1 output is allowed when the GPU can encode it.</summary>
    public bool AllowAv1Encoding { get; set; }

    /// <summary>Gets or sets a value indicating whether hardware tone mapping is turned on when it works.</summary>
    public bool EnableTonemapping { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether VC-1 decode is inferred from MPEG-2 support on Intel/NVIDIA.</summary>
    public bool InferVc1 { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether hardware acceleration is switched off when nothing works.</summary>
    public bool DisableWhenNoneFound { get; set; }

    public int ProbeTimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets the hardware fingerprint of the last successful apply.</summary>
    public string LastAppliedFingerprint { get; set; } = string.Empty;
}
