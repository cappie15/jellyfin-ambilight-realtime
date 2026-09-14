#!/usr/bin/env bash
# CI/local UI smoke test: builds the plugin package, boots a throwaway
# Jellyfin container with it installed, runs the first-time setup wizard
# through its REST API, then drives the settings page in a real browser
# (tests/ui-smoke/smoke.js) and fails if it doesn't load cleanly.
#
# Usage: tests/ui-smoke/run.sh
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repository_root"

container_name="ambilight-ui-smoke-$$"
work_dir="$(mktemp -d)"
port=18096
admin_user="ci-admin"
admin_password="CiSmokeTest123!"
base_url="http://localhost:$port"

cleanup() {
    docker rm -f "$container_name" >/dev/null 2>&1 || true
    # The container writes /config as root; a plain rm from the host user fails.
    docker run --rm -v "$work_dir:/cleanup" alpine sh -c 'rm -rf /cleanup/*' >/dev/null 2>&1 || true
    rm -rf "$work_dir"
}
trap cleanup EXIT

echo "==> Building plugin package"
./build/package.sh >/dev/null

echo "==> Staging plugin into $work_dir/config/plugins"
mkdir -p "$work_dir/config/plugins" "$work_dir/cache"
cp -r "artifacts/Realtime Ambilight" "$work_dir/config/plugins/"

echo "==> Starting Jellyfin container $container_name"
docker run -d --name "$container_name" -p "$port:8096" \
    -v "$work_dir/config:/config" -v "$work_dir/cache:/cache" \
    jellyfin/jellyfin:latest >/dev/null

echo "==> Waiting for Jellyfin to accept API requests"
ready=0
for _ in $(seq 1 60); do
    code=$(curl -s -o /dev/null -w "%{http_code}" "$base_url/System/Info/Public" 2>/dev/null || echo 000)
    if [ "$code" = "200" ]; then
        ready=1
        break
    fi
    sleep 2
done
if [ "$ready" != "1" ]; then
    echo "Jellyfin never became ready" >&2
    docker logs "$container_name" 2>&1 | tail -100
    exit 1
fi

echo "==> Running the first-time setup wizard"
# POST /Startup/User 404s until a first user already exists (GetFirstUser()
# is null) -- Jellyfin only creates the default "root" account as a side
# effect of GET /Startup/User's own _userManager.InitializeAsync() call, so
# that GET must happen first. The route table can also lag a couple of
# seconds behind /System/Info/Public going green, so retry both requests
# instead of failing on a transient 503/404.
initialized=0
for _ in $(seq 1 10); do
    code=$(curl -s -o /dev/null -w "%{http_code}" "$base_url/Startup/User" 2>/dev/null || echo 000)
    if [ "$code" = "200" ]; then
        initialized=1
        break
    fi
    sleep 2
done
if [ "$initialized" != "1" ]; then
    echo "Could not initialize the default user (last HTTP status: $code)" >&2
    exit 1
fi

created=0
for _ in $(seq 1 10); do
    code=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$base_url/Startup/User" \
        -H "Content-Type: application/json" \
        -d "{\"Name\":\"$admin_user\",\"Password\":\"$admin_password\"}" 2>/dev/null || echo 000)
    if [ "$code" = "204" ]; then
        created=1
        break
    fi
    sleep 2
done
if [ "$created" != "1" ]; then
    echo "Could not create the initial admin user (last HTTP status: $code)" >&2
    exit 1
fi

curl -s -o /dev/null -X POST "$base_url/Startup/RemoteAccess" \
    -H "Content-Type: application/json" \
    -d '{"EnableRemoteAccess":false,"EnableAutomaticPortMapping":false}' || true
curl -s -o /dev/null -X POST "$base_url/Startup/Complete" || true

echo "==> Verifying the plugin loaded cleanly"
# Core startup (migrations, ffmpeg probing, etc.) can take longer than the
# setup wizard calls above, so the "Loaded plugin" line may not have been
# written yet the instant Startup/Complete returns -- poll for it instead of
# treating a not-yet-reached log line as a failure.
loaded=0
for _ in $(seq 1 60); do
    if docker logs "$container_name" 2>&1 | grep -q "Loaded plugin: Realtime Ambilight"; then
        loaded=1
        break
    fi
    sleep 2
done
if [ "$loaded" != "1" ]; then
    echo "Plugin never reported as loaded" >&2
    docker logs "$container_name" 2>&1 | tail -100
    exit 1
fi
if docker logs "$container_name" 2>&1 | grep -iE "RealtimeAmbilight.*\b(error|exception)\b"; then
    echo "Plugin logged an error/exception during startup" >&2
    exit 1
fi

echo "==> Waiting for the server to finish starting"
core_started=0
for _ in $(seq 1 60); do
    if docker logs "$container_name" 2>&1 | grep -q "Core startup complete"; then
        core_started=1
        break
    fi
    sleep 2
done
if [ "$core_started" != "1" ]; then
    echo "Server never reported core startup complete" >&2
    docker logs "$container_name" 2>&1 | tail -100
    exit 1
fi

echo "==> Installing Playwright"
(cd tests/ui-smoke && npm ci --no-audit --no-fund >/dev/null && npx playwright install --with-deps chromium >/dev/null)

echo "==> Running the browser smoke test"
node tests/ui-smoke/smoke.js "$base_url" "$admin_user" "$admin_password"
