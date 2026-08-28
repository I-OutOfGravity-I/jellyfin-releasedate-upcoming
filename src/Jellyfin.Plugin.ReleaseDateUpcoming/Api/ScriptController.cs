using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ReleaseDateUpcoming.Api;

/// <summary>
/// Serves the Jellyfin Web enhancement script.
/// </summary>
[ApiController]
[Route("ReleaseDateUpcoming")]
public class ScriptController : ControllerBase
{
    private const string ResourceName = "Jellyfin.Plugin.ReleaseDateUpcoming.Web.release-date-upcoming.js";

    /// <summary>
    /// Gets the browser script.
    /// </summary>
    /// <returns>The JavaScript resource.</returns>
    [HttpGet("script.js")]
    [Produces("application/javascript")]
    public IActionResult GetScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "application/javascript");
    }
}
