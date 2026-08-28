(function () {
    'use strict';

    const pluginClass = 'release-date-upcoming';
    const seasonSummaryClass = 'release-date-upcoming-season-summary';
    const legacyUpcomingClass = 'release-date-upcoming-panel';
    const processedAttribute = 'data-release-date-upcoming';
    let lastSeasonId = null;
    let lastRun = 0;
    let isRunning = false;
    let rerunRequested = false;
    let scheduleTimer = null;
    let followUpTimer = null;

    function getApiClient() {
        return window.ApiClient || window.ConnectionManager?.currentApiClient?.();
    }

    function getUserId(apiClient) {
        return apiClient?._serverInfo?.UserId || apiClient?._currentUserId || window.Dashboard?.getCurrentUserId?.();
    }

    function getCurrentItemId() {
        const hash = window.location.hash || '';
        const query = hash.includes('?') ? hash.substring(hash.indexOf('?')) : window.location.search;
        const params = new URLSearchParams(query.startsWith('?') ? query.substring(1) : query);
        return params.get('id');
    }

    function isCurrentItem(itemId) {
        return getCurrentItemId() === itemId;
    }

    function getCurrentPageContainer() {
        const candidates = Array.from(document.querySelectorAll('.page, .view, main'))
            .filter((element) => element.offsetParent !== null || element.getClientRects().length > 0);

        return candidates[candidates.length - 1] || document.body;
    }

    function parseDate(value) {
        if (!value) {
            return null;
        }

        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? null : date;
    }

    function formatDate(date) {
        return new Intl.DateTimeFormat('de-DE', {
            day: '2-digit',
            month: '2-digit',
            year: 'numeric'
        }).format(date);
    }

    function isFuture(date) {
        const today = new Date();
        today.setHours(0, 0, 0, 0);

        const releaseDate = new Date(date);
        releaseDate.setHours(0, 0, 0, 0);
        return releaseDate > today;
    }

    function normalize(value) {
        return (value || '').toString().trim().toLowerCase();
    }

    function isPluginElement(element) {
        return element?.classList?.contains(pluginClass)
            || element?.classList?.contains(seasonSummaryClass)
            || element?.classList?.contains(legacyUpcomingClass);
    }

    function isPluginMutation(mutation) {
        const nodes = [...mutation.addedNodes, ...mutation.removedNodes]
            .filter((node) => node.nodeType === Node.ELEMENT_NODE);

        return nodes.length > 0 && nodes.every((node) => isPluginElement(node));
    }

    function removePluginElements(container) {
        container.querySelectorAll(`.${pluginClass}, .${seasonSummaryClass}, .${legacyUpcomingClass}`).forEach((node) => node.remove());
        container.querySelectorAll(`[${processedAttribute}]`).forEach((node) => node.removeAttribute(processedAttribute));
    }

    async function getJson(apiClient, path) {
        if (typeof apiClient.getJSON === 'function') {
            return apiClient.getJSON(path);
        }

        if (typeof apiClient.ajax === 'function') {
            const url = typeof apiClient.getUrl === 'function' ? apiClient.getUrl(path) : path;
            return apiClient.ajax({ type: 'GET', url, dataType: 'json' });
        }

        const response = await fetch(path, { credentials: 'include' });
        return response.json();
    }

    async function getSeason(apiClient, userId, itemId) {
        return getJson(apiClient, `/Users/${encodeURIComponent(userId)}/Items/${encodeURIComponent(itemId)}?fields=ProviderIds,IndexNumber,SeriesName,SeriesId,ParentId,Path`);
    }

    async function getSeries(apiClient, userId, season) {
        const seriesId = season.SeriesId || season.ParentId;
        if (!seriesId) {
            return null;
        }

        try {
            return await getJson(apiClient, `/Users/${encodeURIComponent(userId)}/Items/${encodeURIComponent(seriesId)}?fields=ProviderIds,ProductionYear,Path`);
        } catch {
            return null;
        }
    }

    function getTvdbIdFromPath(path) {
        const match = (path || '').match(/\{tvdb-(\d+)\}/i);
        return match ? Number(match[1]) : null;
    }

    async function getSonarrProgress(apiClient, userId, season) {
        const series = await getSeries(apiClient, userId, season);
        const providerIds = series?.ProviderIds || season.ProviderIds || {};
        const seriesName = series?.Name || season.SeriesName || season.Series || season.SeriesTitle;
        const seasonNumber = Number(season.IndexNumber);
        if (!Number.isFinite(seasonNumber) || seasonNumber < 0) {
            return null;
        }

        const tvdbId = Number(providerIds.Tvdb || providerIds.TVDB || providerIds.tvdb || getTvdbIdFromPath(series?.Path) || getTvdbIdFromPath(season.Path));
        const tmdbId = Number(providerIds.Tmdb || providerIds.TMDB || providerIds.tmdb);
        const imdbId = providerIds.Imdb || providerIds.IMDB || providerIds.imdb;
        const productionYear = Number(series?.ProductionYear || season.ProductionYear);
        const params = new URLSearchParams({
            seasonNumber: seasonNumber.toString()
        });

        if (seriesName) {
            params.set('seriesName', seriesName);
        }

        if (Number.isFinite(tvdbId) && tvdbId > 0) {
            params.set('tvdbId', tvdbId.toString());
        }

        if (Number.isFinite(tmdbId) && tmdbId > 0) {
            params.set('tmdbId', tmdbId.toString());
        }

        if (imdbId) {
            params.set('imdbId', imdbId.toString());
        }

        if (Number.isFinite(productionYear) && productionYear > 0) {
            params.set('year', productionYear.toString());
        }

        if (series?.Path || season.Path) {
            params.set('path', series?.Path || season.Path);
        }

        try {
            const progress = await getJson(apiClient, `/ReleaseDateUpcoming/sonarr-progress?${params}`);
            const availableEpisodeNumber = Number(progress?.availableEpisodeNumber ?? progress?.AvailableEpisodeNumber) || 0;
            const totalEpisodeNumber = Number(progress?.totalEpisodeNumber ?? progress?.TotalEpisodeNumber) || 0;
            const episodeAirDates = progress?.episodeAirDates ?? progress?.EpisodeAirDates ?? {};
            if (!progress || !Number.isFinite(totalEpisodeNumber) || totalEpisodeNumber <= 0) {
                return null;
            }

            return {
                availableEpisodeNumber,
                totalEpisodeNumber,
                episodeAirDates
            };
        } catch {
            return null;
        }
    }

    async function getEpisodePage(apiClient, userId, season, options) {
        const isMissing = options?.isMissing === true;
        const isVirtualUnaired = options?.isVirtualUnaired === true;
        const params = new URLSearchParams({
            userId,
            seasonId: season.Id,
            fields: 'PremiereDate,Overview,IndexNumber,ParentIndexNumber,SortName,LocationType'
        });

        if (isMissing) {
            params.set('IsMissing', 'true');
        }

        if (isVirtualUnaired) {
            params.set('IsVirtualUnaired', 'true');
        }

        const seriesId = season.SeriesId || season.ParentId;
        const path = seriesId
            ? `/Shows/${encodeURIComponent(seriesId)}/Episodes?${params}`
            : `/Users/${encodeURIComponent(userId)}/Items?parentId=${encodeURIComponent(season.Id)}&recursive=false&includeItemTypes=Episode&${params}`;

        const result = await getJson(apiClient, path);
        return (result.Items || []).map((episode) => ({
            ...episode,
            ReleaseDateUpcomingIsMissing: isMissing || isVirtualUnaired || episode.LocationType === 'Virtual'
        }));
    }

    async function getEpisodes(apiClient, userId, season) {
        const pages = await Promise.allSettled([
            getEpisodePage(apiClient, userId, season, {}),
            getEpisodePage(apiClient, userId, season, { isMissing: true }),
            getEpisodePage(apiClient, userId, season, { isVirtualUnaired: true })
        ]);

        const byEpisodeNumber = new Map();
        const byId = new Map();
        for (const page of pages) {
            if (page.status !== 'fulfilled') {
                continue;
            }

            for (const episode of page.value) {
                const key = getEpisodeMergeKey(episode);
                const existing = byEpisodeNumber.get(key) || byId.get(episode.Id);
                const merged = mergeEpisode(existing, episode);
                byEpisodeNumber.set(key, merged);

                if (episode.Id) {
                    byId.set(episode.Id, merged);
                }
            }
        }

        return Array.from(byEpisodeNumber.values());
    }

    function getEpisodeMergeKey(episode) {
        const episodeNumber = Number(episode.IndexNumber);
        if (Number.isFinite(episodeNumber) && episodeNumber > 0) {
            return `${episode.ParentIndexNumber || ''}-${episodeNumber}`;
        }

        return episode.Id || `${episode.ParentIndexNumber || ''}-${episode.Name || ''}`;
    }

    function mergeEpisode(existing, episode) {
        if (!existing) {
            return episode;
        }

        return {
            ...existing,
            ...episode,
            Id: existing.ReleaseDateUpcomingIsMissing && episode.Id ? episode.Id : existing.Id,
            Name: existing.Name || episode.Name,
            PremiereDate: existing.PremiereDate || episode.PremiereDate,
            Overview: existing.Overview || episode.Overview,
            ReleaseDateUpcomingIsMissing: existing.ReleaseDateUpcomingIsMissing && episode.ReleaseDateUpcomingIsMissing
        };
    }

    function findEpisodeRows(container) {
        return Array.from(container.querySelectorAll('.listItem, .card, [data-id]'))
            .filter((element) => element.offsetParent !== null && element.querySelector('.listItemBody, .cardText, .cardText-first, .itemName, bdi, h3, a'));
    }

    function rowText(row) {
        return normalize(row.innerText || row.textContent);
    }

    function rowTitleText(row) {
        const title = row.querySelector('.itemName, .cardText-first, bdi, h3, a');
        return normalize(title?.textContent);
    }

    function matchesEpisode(row, episode) {
        if (episode.Id && row.getAttribute('data-id') === episode.Id) {
            return true;
        }

        const title = rowTitleText(row);
        const name = normalize(episode.Name);
        if (name && title && (title === name || title.includes(name))) {
            return true;
        }

        if (!episode.IndexNumber) {
            return false;
        }

        const text = rowText(row);
        const escapedNumber = episode.IndexNumber.toString().replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        return new RegExp(`(^|\\n)\\s*${escapedNumber}\\.\\s+\\S`).test(text);
    }

    function episodeIdentity(episode) {
        return episode.Id || `${episode.ParentIndexNumber || ''}-${episode.IndexNumber || ''}-${episode.Name || ''}`;
    }

    function findOverviewTarget(row) {
        return row.querySelector([
            '.listItemBodyText',
            '.listItemBodyText-secondary',
            '.itemOverview',
            '.overview',
            '.cardText-secondary'
        ].join(', ')) || row.querySelector('.listItemBody, .cardText, .cardBox') || row;
    }

    function addDateToRow(row, episode, date) {
        const identity = episodeIdentity(episode);
        const label = `Premiered ${formatDate(date)}`;
        const existing = row.querySelector(`.${pluginClass}`);
        if (row.getAttribute(processedAttribute) === identity && existing?.textContent === label) {
            return;
        }

        row.querySelectorAll(`.${pluginClass}`).forEach((node) => node.remove());

        if (isFuture(date)) {
            row.removeAttribute(processedAttribute);
            return;
        }

        const badge = document.createElement('div');
        badge.className = pluginClass;
        badge.textContent = label;

        const target = findOverviewTarget(row);
        target.insertBefore(badge, target.firstChild);
        row.setAttribute(processedAttribute, identity);
    }

    function getHighestEpisodeNumber(episodes) {
        return episodes
            .map((episode) => Number(episode.IndexNumber))
            .filter((number) => Number.isFinite(number) && number > 0)
            .sort((a, b) => b - a)[0] || null;
    }

    function enrichEpisodesWithSonarrDates(episodes, sonarrProgress) {
        const episodeAirDates = sonarrProgress?.episodeAirDates || {};
        return episodes.map((episode) => {
            const episodeNumber = Number(episode.IndexNumber);
            const airDate = Number.isFinite(episodeNumber) ? episodeAirDates[episodeNumber] : null;
            if (!airDate) {
                return episode;
            }

            return {
                ...episode,
                PremiereDate: airDate
            };
        });
    }

    function findSeasonTitleElement(container, season) {
        const topContainer = container.querySelector('.detailPagePrimaryContainer, .detailPageContent, .itemDetailsGroup') || container;
        const titleCandidates = Array.from(topContainer.querySelectorAll('h1, .itemName, .detailPageName'))
            .filter((element) => element.offsetParent !== null);
        const seasonName = normalize(season.Name);

        return titleCandidates.find((element) => normalize(element.textContent) === seasonName)
            || titleCandidates.find((element) => normalize(element.textContent).startsWith('season '))
            || titleCandidates[0]
            || topContainer;
    }

    function getSeasonProgress(episodes, sonarrProgress) {
        const jellyfinAvailableEpisodeNumber = getHighestEpisodeNumber(episodes.filter((episode) => !episode.ReleaseDateUpcomingIsMissing)) || 0;
        const sonarrTotalEpisodeNumber = Number(sonarrProgress?.totalEpisodeNumber) || 0;

        return {
            availableEpisodeNumber: Math.max(jellyfinAvailableEpisodeNumber, sonarrProgress?.availableEpisodeNumber || 0),
            totalEpisodeNumber: sonarrTotalEpisodeNumber > 0 ? sonarrTotalEpisodeNumber.toString() : '?'
        };
    }

    function renderSeasonSummary(container, season, episodes, sonarrProgress) {
        container.querySelectorAll(`.${legacyUpcomingClass}`).forEach((node) => node.remove());

        const progress = getSeasonProgress(episodes, sonarrProgress);
        const label = `${progress.availableEpisodeNumber} / ${progress.totalEpisodeNumber}`;
        const existing = container.querySelector(`.${seasonSummaryClass}`);
        if (existing) {
            if (existing.getAttribute('data-season-id') !== season.Id) {
                existing.remove();
            } else {
                if (existing.textContent !== label) {
                    existing.textContent = label;
                }

                return;
            }
        }

        const summary = document.createElement('div');
        summary.className = seasonSummaryClass;
        summary.setAttribute('data-season-id', season.Id);
        summary.textContent = label;

        const title = findSeasonTitleElement(container, season);
        title.insertAdjacentElement('afterend', summary);
    }

    function updateSeasonSummary(container, season, episodes, sonarrProgress) {
        const progress = getSeasonProgress(episodes, sonarrProgress);
        const label = `${progress.availableEpisodeNumber} / ${progress.totalEpisodeNumber}`;
        const existing = container.querySelector(`.${seasonSummaryClass}`);
        if (existing && existing.getAttribute('data-season-id') === season.Id) {
            if (existing.textContent !== label) {
                existing.textContent = label;
            }

            return;
        }

        renderSeasonSummary(container, season, episodes, sonarrProgress);
    }

    function injectStyles() {
        if (document.getElementById('release-date-upcoming-style')) {
            return;
        }

        const style = document.createElement('style');
        style.id = 'release-date-upcoming-style';
        style.textContent = `
            .${pluginClass} {
                display: block;
                margin: 0 0 .35em;
                color: var(--theme-primary-color, #00a4dc);
                font-size: .92em;
                line-height: 1.35;
                font-weight: 500;
            }
            .${seasonSummaryClass} {
                display: block;
                margin: .2em 0 .8em;
                color: var(--theme-primary-color, #00a4dc);
                font-size: .98em;
                line-height: 1.35;
                font-weight: 500;
            }
        `;
        document.head.appendChild(style);
    }

    async function run() {
        if (isRunning) {
            rerunRequested = true;
            return;
        }

        const now = Date.now();
        if (now - lastRun < 500) {
            return;
        }

        isRunning = true;
        lastRun = now;
        try {
            const apiClient = getApiClient();
            const userId = getUserId(apiClient);
            const itemId = getCurrentItemId();
            if (!apiClient || !userId || !itemId) {
                removePluginElements(document);
                return;
            }

            const season = await getSeason(apiClient, userId, itemId);
            if (!season || season.Type !== 'Season') {
                removePluginElements(document);
                lastSeasonId = null;
                return;
            }

            if (!isCurrentItem(itemId)) {
                return;
            }

            const episodes = await getEpisodes(apiClient, userId, season);
            if (!isCurrentItem(itemId)) {
                return;
            }

            if (!episodes.length) {
                const page = getCurrentPageContainer();
                removePluginElements(page);
                return;
            }

            const sonarrProgress = await getSonarrProgress(apiClient, userId, season);
            if (!isCurrentItem(itemId)) {
                return;
            }

            const enrichedEpisodes = enrichEpisodesWithSonarrDates(episodes, sonarrProgress);
            const page = getCurrentPageContainer();
            injectStyles();

            const rows = findEpisodeRows(page);
            const matchedRows = new Set();
            for (const episode of enrichedEpisodes) {
                const date = parseDate(episode.PremiereDate);
                if (!date) {
                    continue;
                }

                const row = rows.find((candidate) => !matchedRows.has(candidate) && matchesEpisode(candidate, episode));
                if (row) {
                    addDateToRow(row, episode, date);
                    matchedRows.add(row);
                }
            }

            updateSeasonSummary(page, season, enrichedEpisodes, sonarrProgress);
            lastSeasonId = season.Id;
        } catch (error) {
            console.warn('Release Date Upcoming failed to update the season page.', error);
        } finally {
            isRunning = false;
            if (rerunRequested) {
                rerunRequested = false;
                scheduleRun();
            }
        }
    }

    function scheduleRun() {
        window.clearTimeout(scheduleTimer);
        window.clearTimeout(followUpTimer);

        scheduleTimer = window.setTimeout(run, 250);
        followUpTimer = window.setTimeout(run, 1250);
    }

    window.addEventListener('hashchange', scheduleRun);
    window.addEventListener('popstate', scheduleRun);
    document.addEventListener('viewshow', scheduleRun);
    document.addEventListener('pageshow', scheduleRun);

    const observer = new MutationObserver((mutations) => {
        if (mutations.length > 0 && mutations.every(isPluginMutation)) {
            return;
        }

        const itemId = getCurrentItemId();
        if (itemId) {
            scheduleRun();
        }
    });

    observer.observe(document.documentElement, { childList: true, subtree: true });
    scheduleRun();
}());
