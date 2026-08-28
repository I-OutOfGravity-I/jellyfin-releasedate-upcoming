(function () {
    'use strict';

    const pluginClass = 'release-date-upcoming';
    const upcomingClass = 'release-date-upcoming-panel';
    const processedAttribute = 'data-release-date-upcoming';
    let lastSeasonId = null;
    let lastRun = 0;

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

    function parseDate(value) {
        if (!value) {
            return null;
        }

        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? null : date;
    }

    function formatDate(date) {
        return new Intl.DateTimeFormat(undefined, {
            weekday: 'short',
            year: 'numeric',
            month: 'short',
            day: 'numeric'
        }).format(date);
    }

    function isFuture(date) {
        const today = new Date();
        today.setHours(0, 0, 0, 0);
        return date >= today;
    }

    function normalize(value) {
        return (value || '').toString().trim().toLowerCase();
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
        if (typeof apiClient.getItem === 'function') {
            return apiClient.getItem(userId, itemId);
        }

        return getJson(apiClient, `/Users/${encodeURIComponent(userId)}/Items/${encodeURIComponent(itemId)}`);
    }

    async function getEpisodePage(apiClient, userId, season, isMissing) {
        const params = new URLSearchParams({
            userId,
            seasonId: season.Id,
            IsMissing: isMissing.toString(),
            fields: 'PremiereDate,Overview,IndexNumber,ParentIndexNumber,SortName,LocationType'
        });

        const seriesId = season.SeriesId || season.ParentId;
        const path = seriesId
            ? `/Shows/${encodeURIComponent(seriesId)}/Episodes?${params}`
            : `/Users/${encodeURIComponent(userId)}/Items?parentId=${encodeURIComponent(season.Id)}&recursive=false&includeItemTypes=Episode&fields=PremiereDate,Overview,IndexNumber,ParentIndexNumber,SortName,LocationType`;

        const result = await getJson(apiClient, path);
        return result.Items || [];
    }

    async function getEpisodes(apiClient, userId, season) {
        const pages = await Promise.allSettled([
            getEpisodePage(apiClient, userId, season, false),
            getEpisodePage(apiClient, userId, season, true)
        ]);

        const byId = new Map();
        for (const page of pages) {
            if (page.status !== 'fulfilled') {
                continue;
            }

            for (const episode of page.value) {
                byId.set(episode.Id || `${episode.ParentIndexNumber}-${episode.IndexNumber}-${episode.Name}`, episode);
            }
        }

        return Array.from(byId.values());
    }

    function findEpisodeRows() {
        return Array.from(document.querySelectorAll('.listItem, .card, [data-id]'))
            .filter((element) => element.offsetParent !== null && element.querySelector('.listItemBody, .cardText, .cardText-first, .itemName, bdi, h3, a'));
    }

    function rowText(row) {
        return normalize(row.innerText || row.textContent);
    }

    function matchesEpisode(row, episode) {
        if (episode.Id && row.getAttribute('data-id') === episode.Id) {
            return true;
        }

        const text = rowText(row);
        const name = normalize(episode.Name);
        if (name && text.includes(name)) {
            return true;
        }

        if (!episode.IndexNumber) {
            return false;
        }

        const escapedNumber = episode.IndexNumber.toString().replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        return new RegExp(`(^|\\D)${escapedNumber}(\\D|$)`).test(text);
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
        if (row.getAttribute(processedAttribute) === identity) {
            return;
        }

        row.querySelectorAll(`.${pluginClass}`).forEach((node) => node.remove());

        const badge = document.createElement('div');
        badge.className = pluginClass;
        badge.textContent = isFuture(date) ? `Releases ${formatDate(date)}` : `Premiered ${formatDate(date)}`;

        const target = findOverviewTarget(row);
        target.insertBefore(badge, target.firstChild);
        row.setAttribute(processedAttribute, identity);
    }

    function renderUpcoming(container, episodes) {
        container.querySelectorAll(`.${upcomingClass}`).forEach((node) => node.remove());

        const upcoming = episodes
            .map((episode) => ({ episode, date: parseDate(episode.PremiereDate) }))
            .filter((entry) => entry.date && isFuture(entry.date))
            .sort((a, b) => a.date - b.date);

        if (!upcoming.length) {
            return;
        }

        const panel = document.createElement('section');
        panel.className = upcomingClass;

        const title = document.createElement('h2');
        title.textContent = 'Upcoming episodes';
        panel.appendChild(title);

        const list = document.createElement('div');
        list.className = `${upcomingClass}-list`;

        upcoming.forEach(({ episode, date }) => {
            const row = document.createElement('div');
            row.className = `${upcomingClass}-item`;

            const name = document.createElement('span');
            name.className = `${upcomingClass}-name`;
            name.textContent = episode.IndexNumber ? `${episode.IndexNumber}. ${episode.Name}` : episode.Name;

            const release = document.createElement('span');
            release.className = `${upcomingClass}-date`;
            release.textContent = formatDate(date);

            row.append(name, release);
            list.appendChild(row);
        });

        panel.appendChild(list);

        const insertionPoint = container.querySelector('.itemsContainer, .verticalSection, .detailSectionContent') || container;
        insertionPoint.insertAdjacentElement('afterend', panel);
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
            .${upcomingClass} {
                margin: 1.5em 3.3% 0;
                max-width: 70em;
            }
            .${upcomingClass} h2 {
                margin: 0 0 .75em;
                font-size: 1.35em;
                font-weight: 500;
            }
            .${upcomingClass}-list {
                display: grid;
                gap: .5em;
            }
            .${upcomingClass}-item {
                display: flex;
                justify-content: space-between;
                gap: 1em;
                padding: .75em 0;
                border-bottom: 1px solid rgba(255, 255, 255, .12);
            }
            .${upcomingClass}-name {
                min-width: 0;
                overflow-wrap: anywhere;
            }
            .${upcomingClass}-date {
                flex: 0 0 auto;
                color: var(--theme-primary-color, #00a4dc);
            }
        `;
        document.head.appendChild(style);
    }

    async function run() {
        const now = Date.now();
        if (now - lastRun < 500) {
            return;
        }

        lastRun = now;
        const apiClient = getApiClient();
        const userId = getUserId(apiClient);
        const itemId = getCurrentItemId();
        if (!apiClient || !userId || !itemId) {
            return;
        }

        try {
            const season = await getSeason(apiClient, userId, itemId);
            if (!season || season.Type !== 'Season') {
                return;
            }

            const episodes = await getEpisodes(apiClient, userId, season);
            if (!episodes.length) {
                return;
            }

            injectStyles();

            const rows = findEpisodeRows();
            for (const episode of episodes) {
                const date = parseDate(episode.PremiereDate);
                if (!date) {
                    continue;
                }

                const row = rows.find((candidate) => matchesEpisode(candidate, episode));
                if (row) {
                    addDateToRow(row, episode, date);
                }
            }

            const page = document.querySelector('.page, .view, main, body') || document.body;
            renderUpcoming(page, episodes);
            lastSeasonId = season.Id;
        } catch (error) {
            console.warn('Release Date Upcoming failed to update the season page.', error);
        }
    }

    function scheduleRun() {
        window.setTimeout(run, 250);
        window.setTimeout(run, 1250);
    }

    window.addEventListener('hashchange', scheduleRun);
    window.addEventListener('popstate', scheduleRun);
    document.addEventListener('viewshow', scheduleRun);
    document.addEventListener('pageshow', scheduleRun);

    const observer = new MutationObserver(() => {
        const itemId = getCurrentItemId();
        if (itemId) {
            scheduleRun();
        }
    });

    observer.observe(document.documentElement, { childList: true, subtree: true });
    scheduleRun();
}());
