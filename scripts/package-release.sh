#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:-1.0.0.0}"
TARGET_ABI="${2:-10.11.0.0}"
CHANGELOG="${3:-Initial release.}"
GITHUB_REPOSITORY="${GITHUB_REPOSITORY:-outofgravity/jellyfin-releasedate-upcoming}"
PROJECT="src/Jellyfin.Plugin.ReleaseDateUpcoming/Jellyfin.Plugin.ReleaseDateUpcoming.csproj"
OUT_DIR="dist/release-date-upcoming_${VERSION}"
ZIP_FILE="dist/release-date-upcoming_${VERSION}.zip"

rm -rf "$OUT_DIR" "$ZIP_FILE"
dotnet publish "$PROJECT" -c Release -o "$OUT_DIR"

find "$OUT_DIR" -type f \
  ! -name "Jellyfin.Plugin.ReleaseDateUpcoming.dll" \
  ! -name "Jellyfin.Plugin.ReleaseDateUpcoming.pdb" \
  ! -name "Jellyfin.Plugin.ReleaseDateUpcoming.xml" \
  -delete

python3 - "$OUT_DIR" "$ZIP_FILE" <<'PY'
import sys
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

source = Path(sys.argv[1])
target = Path(sys.argv[2])
target.parent.mkdir(parents=True, exist_ok=True)
with ZipFile(target, "w", ZIP_DEFLATED) as archive:
    for path in sorted(source.rglob("*")):
        if path.is_file():
            archive.write(path, path.relative_to(source))
PY

CHECKSUM="$(md5sum "$ZIP_FILE" | awk '{print $1}')"
SOURCE_URL="https://github.com/${GITHUB_REPOSITORY}/releases/download/v${VERSION}/$(basename "$ZIP_FILE")"
TIMESTAMP="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

python3 - "$VERSION" "$TARGET_ABI" "$SOURCE_URL" "$CHECKSUM" "$TIMESTAMP" "$CHANGELOG" <<'PY'
import json
import sys
from pathlib import Path

version, target_abi, source_url, checksum, timestamp, changelog = sys.argv[1:]
path = Path("manifest.json")
manifest = json.loads(path.read_text(encoding="utf-8"))
entry = manifest[0]
entry["versions"] = [
    release for release in entry.get("versions", [])
    if release.get("version") != version
]
entry["versions"].insert(0, {
    "version": version,
    "changelog": changelog,
    "targetAbi": target_abi,
    "sourceUrl": source_url,
    "checksum": checksum,
    "timestamp": timestamp
})
path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
PY

echo "Created $ZIP_FILE"
echo "Updated manifest.json"
echo "Release asset URL: $SOURCE_URL"
echo "Checksum: $CHECKSUM"
