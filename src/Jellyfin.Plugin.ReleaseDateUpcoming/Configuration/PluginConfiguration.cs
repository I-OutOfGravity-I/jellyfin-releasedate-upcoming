using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ReleaseDateUpcoming.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the Sonarr base URL.
    /// </summary>
    public string SonarrBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Sonarr API key.
    /// </summary>
    public string SonarrApiKey { get; set; } = string.Empty;
}
