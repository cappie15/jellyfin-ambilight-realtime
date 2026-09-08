#!/usr/bin/env bash
# Builds the plugin and stages an installable package plus a Jellyfin plugin
# repository manifest.
#
# The manifest field names and the MD5 checksum match Jellyfin's own
# MediaBrowser.Model.Updates PackageInfo/VersionInfo DTOs.
#
# Usage: build/package.sh [base-url-the-zip-will-be-served-from]
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

metadata="plugin-package/meta.json"
artifacts="artifacts"
plugin_project="src/Jellyfin.Plugin.RealtimeAmbilight"
output="$plugin_project/bin/Release/net9.0"

read_metadata() {
    python3 -c "import json,sys; print(json.load(open('$metadata'))['$1'])"
}

name="$(read_metadata name)"
version="$(read_metadata version)"
target_abi="$(read_metadata targetAbi)"
guid="$(read_metadata guid)"

# Where the release asset will be downloadable from once published. Override it
# for a fork, or pass it as the first argument.
base_url="${1:-https://github.com/cappie15/jellyfin-ambilight-realtime/releases/download/v$version}"

echo "==> Building $name $version"
# PathMap rewrites the checkout path embedded in the assemblies to a fixed root,
# so the artifact does not leak the build machine's directory layout.
#
# Scope of "reproducible": the archive itself is deterministic (fixed entry
# timestamps, sorted entries), and repeated builds from the same checkout
# produce an identical checksum. Builds from two different checkout directories
# were measured to still differ in 72 bytes -- the PE timestamp, MVID and PDB
# signature, all derived from a content hash. Treat the CI artifact as the
# canonical package rather than expecting a local rebuild to match its checksum.
dotnet build Jellyfin.RealtimeAmbilight.sln --configuration Release \
    -p:ContinuousIntegrationBuild=true \
    -p:PathMap="$repository_root=/src"

echo "==> Staging"
rm -rf "$artifacts"
staging="$artifacts/$name"
mkdir -p "$staging"

# Both assemblies must ship together: deploying the plugin against a stale Core
# assembly fails at runtime with MissingMethodException.
cp "$output/Jellyfin.Plugin.RealtimeAmbilight.dll" "$staging/"
cp "$output/Jellyfin.Plugin.RealtimeAmbilight.Core.dll" "$staging/"
cp "$metadata" "$staging/meta.json"
cp "plugin-package/ambilight-logo.png" "$staging/ambilight-logo.png"

archive_name="realtime-ambilight_$version.zip"
archive="$artifacts/$archive_name"

echo "==> Archiving $archive_name"
# Written with a fixed entry timestamp and sorted entries, so identical input
# always produces a byte-identical archive and therefore a stable checksum.
STAGING="$staging" ARCHIVE="$archive" python3 - <<'ZIP'
import os, zipfile

staging = os.environ["STAGING"]
entries = sorted(
    os.path.relpath(os.path.join(root, name), staging)
    for root, _, names in os.walk(staging)
    for name in names
)
with zipfile.ZipFile(os.environ["ARCHIVE"], "w", zipfile.ZIP_DEFLATED) as archive:
    for entry in entries:
        info = zipfile.ZipInfo(entry, date_time=(1980, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o644 << 16
        with open(os.path.join(staging, entry), "rb") as handle:
            archive.writestr(info, handle.read())
ZIP

checksum="$(md5sum "$archive" | cut -d' ' -f1)"
timestamp="$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)"

echo "==> Writing manifest"
CHECKSUM="$checksum" TIMESTAMP="$timestamp" VERSION="$version" \
TARGET_ABI="$target_abi" GUID="$guid" NAME="$name" \
SOURCE_URL="$base_url/$archive_name" IMAGE_URL="$base_url/ambilight-logo.png" METADATA="$metadata" \
python3 - "$artifacts/manifest.json" <<'PY'
import json, os, sys

metadata = json.load(open(os.environ["METADATA"]))
manifest = [{
    "guid": os.environ["GUID"],
    "name": os.environ["NAME"],
    "description": metadata["description"],
    "overview": metadata["overview"],
    "owner": metadata["owner"],
    "category": metadata["category"],
    "imageUrl": os.environ["IMAGE_URL"],
    "versions": [{
        "version": os.environ["VERSION"],
        "changelog": "",
        "targetAbi": os.environ["TARGET_ABI"],
        "sourceUrl": os.environ["SOURCE_URL"],
        "checksum": os.environ["CHECKSUM"],
        "timestamp": os.environ["TIMESTAMP"],
    }],
}]
with open(sys.argv[1], "w") as handle:
    json.dump(manifest, handle, indent=2)
    handle.write("\n")
PY

echo
echo "    package  $archive"
echo "    md5      $checksum"
echo "    manifest $artifacts/manifest.json"
echo "    source   $base_url/$archive_name"
