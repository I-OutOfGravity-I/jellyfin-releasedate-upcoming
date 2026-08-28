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
    /// <param name="path">The optional Jellyfin series path.</param>
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
        [FromQuery] string? path,
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

        tvdbId ??= GetTvdbIdFromPath(path);
        var series = await GetSonarrJsonAsync<List<SonarrSeriesDto>>(baseUri, "api/v3/series", config.SonarrApiKey, cancellationToken).ConfigureAwait(false);
        var matchedSeries = MatchSeries(series, seriesName, tvdbId, tmdbId, imdbId, year);
        matchedSeries ??= await LookupSonarrSeriesAsync(baseUri, config.SonarrApiKey, series, seriesName, tvdbId, tmdbId, imdbId, year, cancellationToken).ConfigureAwait(false);
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
                .Max(),
            EpisodeAirDates = seasonEpisodes
                .Where(episode => !string.IsNullOrWhiteSpace(episode.AirDate))
                .GroupBy(episode => episode.EpisodeNumber)
                .ToDictionary(group => group.Key, group => group.First().AirDate!)
        };
    }

    /// <summary>
    /// Gets Sonarr matching diagnostics for a Jellyfin season.
    /// </summary>
    /// <param name="seriesName">The series name.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <param name="tvdbId">The optional TVDB series ID.</param>
    /// <param name="tmdbId">The optional TMDB series ID.</param>
    /// <param name="imdbId">The optional IMDb series ID.</param>
    /// <param name="year">The optional series production year.</param>
    /// <param name="path">The optional Jellyfin series path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Sonarr matching diagnostics.</returns>
    [HttpGet("sonarr-debug")]
    public async Task<ActionResult<SonarrDebugDto>> GetSonarrDebug(
        [FromQuery] string? seriesName,
        [FromQuery] int seasonNumber,
        [FromQuery] int? tvdbId,
        [FromQuery] int? tmdbId,
        [FromQuery] string? imdbId,
        [FromQuery] int? year,
        [FromQuery] string? path,
        CancellationToken cancellationToken)
    {
        var result = new SonarrDebugDto
        {
            RequestedSeriesName = seriesName,
            RequestedSeasonNumber = seasonNumber,
            RequestedTvdbId = tvdbId,
            RequestedTmdbId = tmdbId,
            RequestedImdbId = imdbId,
            RequestedYear = year,
            RequestedPathTvdbId = GetTvdbIdFromPath(path)
        };

        var config = Plugin.Instance?.Configuration;
        result.IsConfigured = config is not null
            && !string.IsNullOrWhiteSpace(config.SonarrBaseUrl)
            && !string.IsNullOrWhiteSpace(config.SonarrApiKey);
        if (!result.IsConfigured || config is null || !TryGetSonarrBaseUri(config.SonarrBaseUrl, out var baseUri))
        {
            return result;
        }

        tvdbId ??= result.RequestedPathTvdbId;
        var series = await GetSonarrJsonAsync<List<SonarrSeriesDto>>(baseUri, "api/v3/series", config.SonarrApiKey, cancellationToken).ConfigureAwait(false);
        result.LocalTitleMatches = series
            .Where(item => MatchesTitle(item, Normalize(seriesName)))
            .Select(SonarrSeriesMatchDto.FromSeries)
            .ToList();

        var matchedSeries = MatchSeries(series, seriesName, tvdbId, tmdbId, imdbId, year);
        result.MatchSource = matchedSeries is null ? null : "local-series";
        if (matchedSeries is null)
        {
            matchedSeries = await LookupSonarrSeriesAsync(baseUri, config.SonarrApiKey, series, seriesName, tvdbId, tmdbId, imdbId, year, cancellationToken).ConfigureAwait(false);
            result.MatchSource = matchedSeries is null ? null : "sonarr-lookup";
        }

        if (matchedSeries is null)
        {
            return result;
        }

        result.MatchedSeries = SonarrSeriesMatchDto.FromSeries(matchedSeries);
        var episodesPath = $"api/v3/episode?seriesId={matchedSeries.Id}";
        var episodes = await GetSonarrJsonAsync<List<SonarrEpisodeDto>>(baseUri, episodesPath, config.SonarrApiKey, cancellationToken).ConfigureAwait(false);
        var seasonEpisodes = episodes
            .Where(episode => episode.SeasonNumber == seasonNumber && episode.EpisodeNumber > 0)
            .ToList();

        result.SeasonEpisodeCount = seasonEpisodes.Count;
        result.AvailableEpisodeNumber = seasonEpisodes
            .Where(episode => episode.HasFile || episode.EpisodeFile is not null)
            .Select(episode => episode.EpisodeNumber)
            .DefaultIfEmpty(0)
            .Max();
        result.TotalEpisodeNumber = seasonEpisodes
            .Select(episode => episode.EpisodeNumber)
            .DefaultIfEmpty(0)
            .Max();
        result.EpisodeAirDates = seasonEpisodes
            .Where(episode => !string.IsNullOrWhiteSpace(episode.AirDate))
            .GroupBy(episode => episode.EpisodeNumber)
            .ToDictionary(group => group.Key, group => group.First().AirDate!);

        return result;
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

    private static async Task<SonarrSeriesDto?> LookupSonarrSeriesAsync(
        Uri baseUri,
        string apiKey,
        IEnumerable<SonarrSeriesDto> existingSeries,
        string? seriesName,
        int? tvdbId,
        int? tmdbId,
        string? imdbId,
        int? year,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        var lookupPath = $"api/v3/series/lookup?term={Uri.EscapeDataString(seriesName)}";
        var lookupResults = await GetSonarrJsonAsync<List<SonarrSeriesDto>>(baseUri, lookupPath, apiKey, cancellationToken).ConfigureAwait(false);
        var lookupMatch = MatchSeries(lookupResults, seriesName, tvdbId, tmdbId, imdbId, year);
        if (lookupMatch?.TvdbId is not > 0)
        {
            return null;
        }

        return existingSeries.FirstOrDefault(item => item.TvdbId == lookupMatch.TvdbId);
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
            .Where(item => MatchesTitle(item, normalizedName))
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

    private static bool MatchesTitle(SonarrSeriesDto item, string normalizedName)
    {
        return Normalize(item.Title) == normalizedName
            || item.AlternateTitles.Any(title => Normalize(title.Title) == normalizedName);
    }

    private static string Normalize(string? value)
    {
        return new string((value ?? string.Empty)
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static int? GetTvdbIdFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(path, @"\{tvdb-(\d+)\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var tvdbId) ? tvdbId : null;
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

    /// <summary>
    /// Gets or sets Sonarr air dates by episode number.
    /// </summary>
    public Dictionary<int, string> EpisodeAirDates { get; set; } = [];
}

/// <summary>
/// Sonarr matching diagnostics response.
/// </summary>
public sealed class SonarrDebugDto
{
    /// <summary>
    /// Gets or sets a value indicating whether Sonarr is configured.
    /// </summary>
    public bool IsConfigured { get; set; }

    /// <summary>
    /// Gets or sets the requested series name.
    /// </summary>
    public string? RequestedSeriesName { get; set; }

    /// <summary>
    /// Gets or sets the requested season number.
    /// </summary>
    public int RequestedSeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the requested TVDB ID.
    /// </summary>
    public int? RequestedTvdbId { get; set; }

    /// <summary>
    /// Gets or sets the requested TMDB ID.
    /// </summary>
    public int? RequestedTmdbId { get; set; }

    /// <summary>
    /// Gets or sets the requested IMDb ID.
    /// </summary>
    public string? RequestedImdbId { get; set; }

    /// <summary>
    /// Gets or sets the requested production year.
    /// </summary>
    public int? RequestedYear { get; set; }

    /// <summary>
    /// Gets or sets the TVDB ID parsed from the requested path.
    /// </summary>
    public int? RequestedPathTvdbId { get; set; }

    /// <summary>
    /// Gets or sets the match source.
    /// </summary>
    public string? MatchSource { get; set; }

    /// <summary>
    /// Gets or sets the matched Sonarr series.
    /// </summary>
    public SonarrSeriesMatchDto? MatchedSeries { get; set; }

    /// <summary>
    /// Gets or sets local Sonarr series whose title or alternate title matched the requested name.
    /// </summary>
    public List<SonarrSeriesMatchDto> LocalTitleMatches { get; set; } = [];

    /// <summary>
    /// Gets or sets the number of Sonarr episodes found for the requested season.
    /// </summary>
    public int SeasonEpisodeCount { get; set; }

    /// <summary>
    /// Gets or sets the highest available episode number.
    /// </summary>
    public int AvailableEpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets the highest total episode number.
    /// </summary>
    public int TotalEpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets Sonarr air dates by episode number.
    /// </summary>
    public Dictionary<int, string> EpisodeAirDates { get; set; } = [];
}

/// <summary>
/// Sonarr series match details.
/// </summary>
public sealed class SonarrSeriesMatchDto
{
    /// <summary>
    /// Gets or sets the Sonarr series ID.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the Sonarr title.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the TVDB ID.
    /// </summary>
    public int TvdbId { get; set; }

    /// <summary>
    /// Gets or sets the TMDB ID.
    /// </summary>
    public int? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the IMDb ID.
    /// </summary>
    public string? ImdbId { get; set; }

    /// <summary>
    /// Gets or sets the series year.
    /// </summary>
    public int? Year { get; set; }

    internal static SonarrSeriesMatchDto FromSeries(SonarrSeriesDto series)
    {
        return new SonarrSeriesMatchDto
        {
            Id = series.Id,
            Title = series.Title,
            TvdbId = series.TvdbId,
            TmdbId = series.TmdbId,
            ImdbId = series.ImdbId,
            Year = series.Year
        };
    }
}

internal sealed class SonarrSeriesDto
{
    public int Id { get; set; }

    public string? Title { get; set; }

    public int TvdbId { get; set; }

    public int? TmdbId { get; set; }

    public string? ImdbId { get; set; }

    public int? Year { get; set; }

    public List<SonarrAlternateTitleDto> AlternateTitles { get; set; } = [];
}

internal sealed class SonarrAlternateTitleDto
{
    public string? Title { get; set; }
}

internal sealed class SonarrEpisodeDto
{
    public int SeasonNumber { get; set; }

    public int EpisodeNumber { get; set; }

    public bool HasFile { get; set; }

    public string? AirDate { get; set; }

    [JsonPropertyName("episodeFile")]
    public object? EpisodeFile { get; set; }
}
