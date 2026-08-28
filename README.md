# Jellyfin Release Date Upcoming

Native Jellyfin plugin that injects a small Jellyfin Web enhancement into `index.html`.

On season detail pages it:

- shows each episode's `PremiereDate` in the episode list
- shows the highest available episode number over the last known season episode number near the top of the page

The season progress uses Jellyfin data by default. Configure Sonarr in the plugin settings to use Sonarr's episode list for the season total, which helps with future episodes Jellyfin has not imported yet.

## Build

Install the .NET SDK version used by your Jellyfin server/plugin target, then run:

```bash
dotnet build -c Release
```

The plugin assembly is emitted under:

```text
src/Jellyfin.Plugin.ReleaseDateUpcoming/bin/Release/net9.0/
```

Copy the built plugin files into Jellyfin's plugin folder, restart Jellyfin, then check the Jellyfin logs for the `Release Date Upcoming` startup entry.

The project currently targets `net9.0` and references Jellyfin `10.11.10`. Jellyfin plugins must be built against the same Jellyfin package line as the server that will load them. If your server is on another version, update the target framework plus the `Jellyfin.Controller` and `Jellyfin.Model` versions in [Jellyfin.Plugin.ReleaseDateUpcoming.csproj](src/Jellyfin.Plugin.ReleaseDateUpcoming/Jellyfin.Plugin.ReleaseDateUpcoming.csproj) before building.

## Install from Jellyfin plugin catalog

Publish this repository to GitHub, then create a release ZIP and update [manifest.json](manifest.json):

```bash
chmod +x scripts/package-release.sh
GITHUB_REPOSITORY=your-github-user/jellyfin-releasedate-upcoming ./scripts/package-release.sh 1.0.0.0 10.11.0.0
```

Upload the generated ZIP from `dist/` to a GitHub release tagged:

```text
v1.0.0.0
```

Commit and push the updated [manifest.json](manifest.json). In Jellyfin, open:

```text
Dashboard -> Plugins -> Repositories -> Add
```

Use this repository URL, replacing the GitHub owner/repo when needed:

```text
https://raw.githubusercontent.com/your-github-user/jellyfin-releasedate-upcoming/main/manifest.json
```

After saving the repository, go to `Catalog`, install `Release Date Upcoming`, and restart Jellyfin.

## Docker note

This plugin patches Jellyfin Web's `index.html` so the browser loads the embedded plugin script. Docker installs may mount `/usr/share/jellyfin/web` read-only. If patching fails, mount a writable `index.html` or install the plugin in an environment where the Jellyfin process can write that file.

The patch is idempotent and marked with:

```html
<!-- Jellyfin.Plugin.ReleaseDateUpcoming -->
```
