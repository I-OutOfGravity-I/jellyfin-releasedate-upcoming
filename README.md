# Jellyfin Release Date Upcoming

Native Jellyfin plugin that injects a small Jellyfin Web enhancement into `index.html`.

On season detail pages it:

- shows each episode's `PremiereDate` in the episode list
- adds a compact `Upcoming episodes` section when the season metadata contains future or missing episodes with premiere dates

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

## Docker note

This plugin patches Jellyfin Web's `index.html` so the browser loads the embedded plugin script. Docker installs may mount `/usr/share/jellyfin/web` read-only. If patching fails, mount a writable `index.html` or install the plugin in an environment where the Jellyfin process can write that file.

The patch is idempotent and marked with:

```html
<!-- Jellyfin.Plugin.ReleaseDateUpcoming -->
```
