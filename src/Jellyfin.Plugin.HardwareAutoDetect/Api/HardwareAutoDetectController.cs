using System.Collections.Generic;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.HardwareAutoDetect.Detection;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.HardwareAutoDetect.Api;

/// <summary>
/// Admin API used by the plugin's configuration page.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("HardwareAutoDetect")]
[Produces(MediaTypeNames.Application.Json)]
public class HardwareAutoDetectController : ControllerBase
{
    private readonly HardwareDetectionService _detection;

    public HardwareAutoDetectController(HardwareDetectionService detection)
    {
        _detection = detection;
    }

    /// <summary>
    /// Runs hardware detection.
    /// </summary>
    /// <param name="apply">Write the detected settings to the transcoding configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The detection report.</returns>
    [HttpPost("Run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DetectionReport>> Run([FromQuery] bool apply, CancellationToken cancellationToken)
        => await _detection.RunAsync(apply, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Gets the last detection report.
    /// </summary>
    /// <returns>The report, or 204 if detection never ran.</returns>
    [HttpGet("Report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult<DetectionReport> Report()
    {
        var report = _detection.LoadLastReport();
        return report is null ? NoContent() : report;
    }

    /// <summary>
    /// Gets whether a backup of the original settings exists.
    /// </summary>
    /// <returns>Status.</returns>
    [HttpGet("Status")]
    public ActionResult<object> Status() => new { HasBackup = _detection.HasBackup };

    /// <summary>
    /// Restores the transcoding settings from before this plugin first changed them.
    /// </summary>
    /// <returns>The list of settings that changed.</returns>
    [HttpPost("Restore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<List<string>> Restore()
    {
        var changes = _detection.RestoreBackup();
        return changes is null ? NotFound("No backup available") : changes;
    }
}
