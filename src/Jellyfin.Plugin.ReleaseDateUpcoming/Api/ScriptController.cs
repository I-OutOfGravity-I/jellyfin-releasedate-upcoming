using System.Reflection;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
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
    private static readonly HttpClient SonarrClient = new();

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

    /// <summary>
    /// Gets the plugin configuration for the configuration page.
    /// </summary>
    /// <returns>The sanitized plugin configuration.</returns>
    [HttpGet("config")]
    public ActionResult<ReleaseDateUpcomingConfigDto> GetConfig()
    {
        var config = Plugin.Instance?.Configuration;
        return new ReleaseDateUpcomingConfigDto
        {
            SonarrBaseUrl = config?.SonarrBaseUrl ?? string.Empty,
            HasSonarrApiKey = !string.IsNullOrWhiteSpace(config?.SonarrApiKey)
        };
    }

    /// <summary>
    /// Updates the plugin configuration.
    /// </summary>
    /// <param name="request">The requested configuration.</param>
    /// <returns>An empty result.</returns>
    [HttpPost("config")]
    public IActionResult UpdateConfig([FromBody] UpdateReleaseDateUpcomingConfigRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        var config = plugin.Configuration;
        config.SonarrBaseUrl = request.SonarrBaseUrl?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(request.SonarrApiKey))
        {
            config.SonarrApiKey = request.SonarrApiKey.Trim();
        }

        plugin.UpdateConfiguration(config);
        return NoContent();
    }

    /// <summary>
    /// Gets Sonarr episode progress for a Jellyfin season.
    /// </summary>
    /// <param name="seriesName">The series name.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <param name="tvdbId">The optional TVDB series ID.</param>
    /// <param name="tmdbId">The optional TMDB series ID.</param>
    /// <param name="imdbId">The optional IMDb series ID.</param>
    /// <param name="year">The optional series production year.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The Sonarr episode progress, when configured and matched.</returns>
    [HttpGet("sonarr-progress")]
    public async Task<ActionResult<SonarrProgressDto>> GetSonarrProgress(
        [FromQuery] string? seriesName,
        [FromQuery] int seasonNumber,
        [FromQuery] int? tvdbId,
        [FromQuery] int? tmdbId,
        [FromQuery] string? imdbId,
        [FromQuery] int? year,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null
            || string.IsNullOrWhiteSpace(config.SonarrBaseUrl)
            || string.IsNullOrWhiteSpace(config.SonarrApiKey)
            || seasonNumber < 0)
        {
            return NoContent();
        }

        if (!TryGetSonarrBaseUri(config.SonarrBaseUrl, out var baseUri))
        {
            return NoContent();
        }

        var series = await GetSonarrJsonAsync<List<SonarrSeriesDto>>(baseUri, "api/v3/series", config.SonarrApiKey, cancellationToken).ConfigureAwait(false);
        var matchedSeries = MatchSeries(series, seriesName, tvdbId, tmdbId, imdbId, year);
        if (matchedSeries is null)
        {
            return NoContent();
        }

        var episodesPath = $"api/v3/episode?seriesId={matchedSeries.Id}";
        var episodes = await GetSonarrJsonAsync<List<SonarrEpisodeDto>>(baseUri, episodesPath, config.SonarrApiKey, cancellationToken).ConfigureAwait(false);
        var seasonEpisodes = episodes
            .Where(episode => episode.SeasonNumber == seasonNumber && episode.EpisodeNumber > 0)
            .ToList();

        if (seasonEpisodes.Count == 0)
        {
            return NoContent();
        }

        return new SonarrProgressDto
        {
            AvailableEpisodeNumber = seasonEpisodes
                .Where(episode => episode.HasFile || episode.EpisodeFile is not null)
                .Select(episode => episode.EpisodeNumber)
                .DefaultIfEmpty(0)
                .Max(),
            TotalEpisodeNumber = seasonEpisodes
                .Select(episode => episode.EpisodeNumber)
                .Max()
        };
    }

    private static bool TryGetSonarrBaseUri(string value, out Uri baseUri)
    {
        if (Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out baseUri!)
            && (baseUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || baseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        baseUri = null!;
        return false;
    }

    private static async Task<T> GetSonarrJsonAsync<T>(Uri baseUri, string path, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
        request.Headers.Add("X-Api-Key", apiKey);

        using var response = await SonarrClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Sonarr returned an empty response.");
    }

    private static SonarrSeriesDto? MatchSeries(IEnumerable<SonarrSeriesDto> series, string? seriesName, int? tvdbId, int? tmdbId, string? imdbId, int? year)
    {
        var seriesList = series.ToList();
        if (tvdbId is > 0)
        {
            var tvdbMatch = seriesList.FirstOrDefault(item => item.TvdbId == tvdbId.Value);
            if (tvdbMatch is not null)
            {
                return tvdbMatch;
            }
        }

        if (tmdbId is > 0)
        {
            var tmdbMatch = seriesList.FirstOrDefault(item => item.TmdbId == tmdbId.Value);
            if (tmdbMatch is not null)
            {
                return tmdbMatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            var imdbMatch = seriesList.FirstOrDefault(item => string.Equals(item.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase));
            if (imdbMatch is not null)
            {
                return imdbMatch;
            }
        }

        var normalizedName = Normalize(seriesName);
        if (string.IsNullOrEmpty(normalizedName))
        {
            return null;
        }

        var titleMatches = seriesList
            .Where(item => Normalize(item.Title) == normalizedName)
            .ToList();

        if (year is > 0)
        {
            var yearMatches = titleMatches
                .Where(item => item.Year == year.Value)
                .ToList();

            if (yearMatches.Count == 1)
            {
                return yearMatches[0];
            }
        }

        return titleMatches.Count == 1 ? titleMatches[0] : null;
    }

    private static string Normalize(string? value)
    {
        return new string((value ?? string.Empty)
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }
}

/// <summary>
/// Sanitized plugin configuration.
/// </summary>
public sealed class ReleaseDateUpcomingConfigDto
{
    /// <summary>
    /// Gets or sets the Sonarr base URL.
    /// </summary>
    public string SonarrBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a Sonarr API key is configured.
    /// </summary>
    public bool HasSonarrApiKey { get; set; }
}

/// <summary>
/// Plugin configuration update request.
/// </summary>
public sealed class UpdateReleaseDateUpcomingConfigRequest
{
    /// <summary>
    /// Gets or sets the Sonarr base URL.
    /// </summary>
    public string? SonarrBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the Sonarr API key.
    /// </summary>
    public string? SonarrApiKey { get; set; }
}

/// <summary>
/// Sonarr season progress response.
/// </summary>
public sealed class SonarrProgressDto
{
    /// <summary>
    /// Gets or sets the highest episode number that Sonarr has a file for.
    /// </summary>
    public int AvailableEpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets the highest episode number Sonarr knows for the season.
    /// </summary>
    public int TotalEpisodeNumber { get; set; }
}

internal sealed class SonarrSeriesDto
{
    public int Id { get; set; }

    public string? Title { get; set; }

    public int TvdbId { get; set; }

    public int? TmdbId { get; set; }

    public string? ImdbId { get; set; }

    public int? Year { get; set; }
}

internal sealed class SonarrEpisodeDto
{
    public int SeasonNumber { get; set; }

    public int EpisodeNumber { get; set; }

    public bool HasFile { get; set; }

    [JsonPropertyName("episodeFile")]
    public object? EpisodeFile { get; set; }
}
