using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.HardwareAutoDetect.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.HardwareAutoDetect;

/// <summary>
/// Hardware Acceleration Auto-Detect plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Hardware Acceleration Auto-Detect";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("5b8c1f3e-7a42-4d6b-9c1e-2f8a6d4b3e91");

    /// <inheritdoc />
    public override string Description => "Detects the GPU / media engine on this server with real ffmpeg test jobs and configures hardware transcoding, decoding codecs, HEVC/AV1 encoding and tone mapping to match.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
            },
        ];
    }
}
