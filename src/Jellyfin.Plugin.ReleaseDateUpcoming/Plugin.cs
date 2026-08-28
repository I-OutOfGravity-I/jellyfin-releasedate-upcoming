using System.Globalization;
using System.Text;
using Jellyfin.Plugin.ReleaseDateUpcoming.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReleaseDateUpcoming;

/// <summary>
/// Main plugin entry point.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private const string InjectionMarker = "Jellyfin.Plugin.ReleaseDateUpcoming";
    private const string ScriptPath = "../ReleaseDateUpcoming/script.js";
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    /// <param name="logger">Logger.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _applicationPaths = applicationPaths;
        _logger = logger;

        TryPatchIndexHtml();
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Release Date Upcoming";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("ab9f378f-32e9-4f10-a02e-f9ea5d3441b4");

    /// <inheritdoc />
    public override string Description => "Shows episode premiere dates and season episode progress on season pages.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "ReleaseDateUpcoming",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };
    }

    private void TryPatchIndexHtml()
    {
        var webPath = _applicationPaths.WebPath;
        if (string.IsNullOrWhiteSpace(webPath))
        {
            _logger.LogWarning("Jellyfin Web path is not available; release date UI script was not injected.");
            return;
        }

        var indexPath = Path.Combine(webPath, "index.html");
        if (!File.Exists(indexPath))
        {
            _logger.LogWarning("Jellyfin Web index.html was not found at {IndexPath}; release date UI script was not injected.", indexPath);
            return;
        }

        try
        {
            var html = File.ReadAllText(indexPath, Encoding.UTF8);
            if (html.Contains(InjectionMarker, StringComparison.Ordinal))
            {
                _logger.LogInformation("Release Date Upcoming script is already injected into Jellyfin Web.");
                return;
            }

            var scriptTag = string.Format(
                CultureInfo.InvariantCulture,
                "<!-- {0} --><script defer src=\"{1}\"></script>",
                InjectionMarker,
                ScriptPath);

            var bodyIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            var patchedHtml = bodyIndex >= 0
                ? html.Insert(bodyIndex, scriptTag)
                : html + scriptTag;

            File.WriteAllText(indexPath, patchedHtml, Encoding.UTF8);
            _logger.LogInformation("Injected Release Date Upcoming script into {IndexPath}.", indexPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to inject Release Date Upcoming script into Jellyfin Web index.html.");
        }
    }
}
