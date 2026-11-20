#!/usr/bin/env bash
# Creates a mock system for trying the Deployment tool locally (macOS/Linux):
#   - mock "server" folders with version 1 of a web app and a worker
#   - a version 2 upload package (.zip) in your Downloads folder
#   - the matching Deployment system and modules in a running DevTools (optional)
#
# Usage:
#   scripts/create-deployment-demo.sh                  # create everything (resets the mock servers to v1)
#   DEMO_ROOT=~/somewhere scripts/create-deployment-demo.sh
#   DEVTOOLS_URL=http://localhost:5022 scripts/create-deployment-demo.sh
#   SKIP_REGISTER=1 scripts/create-deployment-demo.sh   # files only, don't call the DevTools API
#   scripts/create-deployment-demo.sh --populate [N]    # also upload N test packages (default 24) to try
#                                                       # pagination, badges and history; some are deployed and
#                                                       # the last deployment is rolled back
#   scripts/create-deployment-demo.sh --make-packages [N]
#                                                       # only write N test packages (default 24) to
#                                                       # ~/Downloads/SampleApp-test-packages for uploading by hand;
#                                                       # doesn't reset the mock servers or call DevTools
#
# Test packages (from --populate or --make-packages) are kept in ~/Downloads/SampleApp-test-packages, with a
# descriptions.txt listing a suggested description for each one.
# The mock modules have no IIS app pool or Windows service, because those can only be controlled on Windows;
# the Stop and Start steps are therefore skipped when you deploy the demo.
set -euo pipefail

POPULATE=0
MAKE_ONLY=0
case "${1:-}" in
  --populate) POPULATE="${2:-24}" ;;
  --make-packages) POPULATE="${2:-24}"; MAKE_ONLY=1 ;;
  "") ;;
  *) echo "Usage: $0 [--populate N | --make-packages N]" >&2; exit 1 ;;
esac
[[ "$POPULATE" =~ ^[0-9]+$ ]] || { echo "Usage: $0 [--populate N | --make-packages N]" >&2; exit 1; }

DEMO_ROOT="${DEMO_ROOT:-$HOME/DevToolsDeploymentDemo}"
DOWNLOADS="${DOWNLOADS:-$HOME/Downloads}"
DEVTOOLS_URL="${DEVTOOLS_URL:-http://localhost:5022}"
SYSTEM_NAME="${SYSTEM_NAME:-Sample App}"
PACKAGE_NAME="SampleApp-Release-2.0.zip"

servers="$DEMO_ROOT/servers"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

write() { mkdir -p "$(dirname "$1")"; printf '%s\n' "$2" > "$1"; }

# ---------- Versioned test packages (2.0.1, 2.0.2, ...) ----------
TEST_PACKAGES="$DOWNLOADS/SampleApp-test-packages"
descriptions=("Hotfix: invoice rounding" "Release: reporting API" "Patch: login timeout" "Release: worker retry policy"
  "Hotfix: null reference on export" "Release: new dashboard widgets" "Patch: config clean-up" "Release: performance tuning")
test_description() { echo "${descriptions[$(( ($1 - 1) % ${#descriptions[@]} ))]} (2.0.$1)"; }
# Builds SampleApp-Release-2.0.<i>.zip in $TEST_PACKAGES and prints its path.
build_test_package() {
  local i="$1" version="2.0.$1"
  local build="$work/build-$i/release-$version"
  write "$build/WebApi/bin/SampleApp.Api.dll"   "SampleApp.Api v$version"
  write "$build/WebApi/wwwroot/js/app.js"       "console.log('SampleApp web v$version');"
  write "$build/Worker/SampleApp.Worker.dll"    "SampleApp.Worker v$version"
  if (( i % 3 == 0 )); then write "$build/WebApi/bin/SampleApp.Feature$i.dll" "Feature $i (new in $version)"; fi
  mkdir -p "$TEST_PACKAGES"
  local zip_path="$TEST_PACKAGES/SampleApp-Release-$version.zip"
  rm -f "$zip_path"
  (cd "$work/build-$i" && zip -qr -X "$zip_path" "release-$version")
  printf '%s\t%s\n' "SampleApp-Release-$version.zip" "$(test_description "$i")" >> "$TEST_PACKAGES/descriptions.txt"
  echo "$zip_path"
}

if [[ "$MAKE_ONLY" -eq 1 ]]; then
  mkdir -p "$TEST_PACKAGES"; : > "$TEST_PACKAGES/descriptions.txt"
  for i in $(seq 1 "$POPULATE"); do build_test_package "$i" >/dev/null; done
  echo "Wrote $POPULATE test packages to $TEST_PACKAGES"
  echo "Suggested descriptions: $TEST_PACKAGES/descriptions.txt"
  exit 0
fi

echo "Resetting mock servers in $servers (version 1)..."
rm -rf "$servers"
# WebApi: an IIS-style web app.
write "$servers/WebApi/bin/SampleApp.Api.dll"            "SampleApp.Api v1.0.0"
write "$servers/WebApi/bin/SampleApp.Core.dll"           "SampleApp.Core v1.0.0"
write "$servers/WebApi/bin/Legacy.Reports.dll"     "Legacy.Reports v1.0.0 (removed in 2.0)"
write "$servers/WebApi/wwwroot/js/app.js"          "console.log('SampleApp web v1.0.0');"
write "$servers/WebApi/wwwroot/css/site.css"       "body { color: #333; } /* v1 */"
write "$servers/WebApi/web.config"                 "<configuration><!-- PRODUCTION server settings: must be kept --></configuration>"
write "$servers/WebApi/appsettings.Production.json" '{ "ConnectionStrings": { "Main": "Server=prod-db;Database=SampleApp" } }'
write "$servers/WebApi/logs/app-20261119.log"      "2026-11-19 10:00:00 INFO SampleApp started (v1.0.0)"
mkdir -p "$servers/WebApi/App_Data/uploads"        # an empty folder the app needs; deployments keep it
# Worker: a Windows-service-style background worker.
write "$servers/Worker/SampleApp.Worker.exe"             "SampleApp.Worker v1.0.0"
write "$servers/Worker/SampleApp.Worker.dll"             "SampleApp.Worker v1.0.0"
write "$servers/Worker/Plugins/OldExporter.dll"    "OldExporter v1.0.0 (removed in 2.0)"
write "$servers/Worker/worker.settings.json"       '{ "Queue": "prod-queue" }'
write "$servers/Worker/logs/worker-20261119.log"   "2026-11-19 10:00:00 INFO worker started (v1.0.0)"
mkdir -p "$DEMO_ROOT/backups" "$DEMO_ROOT/packages"

echo "Building the version 2 package..."
pkg="$work/release-2.0"
# WebApi v2: updated and new files, plus files that the module's exclusions keep from being deployed.
write "$pkg/WebApi/bin/SampleApp.Api.dll"                "SampleApp.Api v2.0.0"
write "$pkg/WebApi/bin/SampleApp.Core.dll"               "SampleApp.Core v2.0.0"
write "$pkg/WebApi/bin/SampleApp.Invoicing.dll"          "SampleApp.Invoicing v2.0.0 (new in 2.0)"
write "$pkg/WebApi/wwwroot/js/app.js"              "console.log('SampleApp web v2.0.0');"
write "$pkg/WebApi/wwwroot/js/invoices.js"         "console.log('invoice export v2.0.0');"
write "$pkg/WebApi/wwwroot/css/site.css"           "body { color: #111; } /* v2 */"
write "$pkg/WebApi/web.config"                     "<configuration><!-- DEVELOPMENT settings from the build: excluded --></configuration>"
write "$pkg/WebApi/appsettings.Production.json"    '{ "ConnectionStrings": { "Main": "Server=localhost;Database=SampleApp_Dev" } }'
# Worker v2.
write "$pkg/Worker/SampleApp.Worker.exe"                 "SampleApp.Worker v2.0.0"
write "$pkg/Worker/SampleApp.Worker.dll"                 "SampleApp.Worker v2.0.0"
write "$pkg/Worker/Plugins/InvoiceExporter.dll"    "InvoiceExporter v2.0.0 (new in 2.0)"
# A folder that matches no module: shown as ignored after upload.
write "$pkg/Docs/RELEASE-NOTES.md"                 "# SampleApp 2.0 - invoice export, new worker plugin"
mkdir -p "$DOWNLOADS"
rm -f "$DOWNLOADS/$PACKAGE_NAME"
(cd "$work" && zip -qr -X "$DOWNLOADS/$PACKAGE_NAME" release-2.0)
echo "Package: $DOWNLOADS/$PACKAGE_NAME"

if [[ "${SKIP_REGISTER:-0}" == "1" ]]; then
  echo "Skipping DevTools registration (SKIP_REGISTER=1)."
  exit 0
fi
if ! curl -fsS -o /dev/null "$DEVTOOLS_URL/api/deployment/systems"; then
  echo "DevTools isn't reachable at $DEVTOOLS_URL (or login is on); create the system in the UI instead:"
  echo "  System '$SYSTEM_NAME' · package folder $DEMO_ROOT/packages · backup folder $DEMO_ROOT/backups"
  echo "  Module WebApi -> $servers/WebApi (exclude: web.config, appsettings*.json, logs/)"
  echo "  Module Worker -> $servers/Worker (exclude: worker.settings.json, logs/)"
  exit 0
fi

json_string() { python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$1"; }
existing=$(curl -fsS "$DEVTOOLS_URL/api/deployment/systems" | python3 -c '
import json,sys
name=sys.argv[1]
print(next((str(s["id"]) for s in json.load(sys.stdin)["data"] if s["name"]==name), ""))' "$SYSTEM_NAME")
if [[ -n "$existing" ]]; then
  echo "System '$SYSTEM_NAME' already exists in DevTools (id $existing); mock servers were reset to v1."
  system_id="$existing"
else
echo "Registering system '$SYSTEM_NAME' in DevTools..."
system_id=$(curl -fsS -X POST "$DEVTOOLS_URL/api/deployment/systems" -H 'Content-Type: application/json' \
  -d "{\"name\":$(json_string "$SYSTEM_NAME"),\"packageDirectory\":$(json_string "$DEMO_ROOT/packages"),\"backupDirectory\":$(json_string "$DEMO_ROOT/backups")}" \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["data"]["id"])')
add_module() {
  curl -fsS -o /dev/null -X POST "$DEVTOOLS_URL/api/deployment/systems/$system_id/modules" -H 'Content-Type: application/json' \
    -d "{\"name\":$(json_string "$1"),\"targetPath\":$(json_string "$2"),\"defaultExclusions\":$(json_string "$3")}"
}
add_module "WebApi" "$servers/WebApi" $'web.config\nappsettings*.json\nlogs/'
add_module "Worker" "$servers/Worker" $'worker.settings.json\nlogs/'
fi

if [[ "$POPULATE" -eq 0 ]]; then
  echo "Done. Open $DEVTOOLS_URL/tools/deployment, open '$SYSTEM_NAME' and upload $PACKAGE_NAME."
  exit 0
fi

# ---------- Test packages for pagination, badges and history ----------
api="$DEVTOOLS_URL/api/deployment"
json_field() { python3 -c 'import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1], {"d": d}))' "$1"; }
modules=$(curl -fsS "$api/systems" | python3 -c '
import json,sys
s=next(s for s in json.load(sys.stdin)["data"] if s["name"]==sys.argv[1])
print(" ".join(m["name"] + "=" + str(m["id"]) for m in s["modules"]))' "$SYSTEM_NAME")
webapi_id=$(tr ' ' '\n' <<<"$modules" | sed -n 's/^WebApi=//p'); worker_id=$(tr ' ' '\n' <<<"$modules" | sed -n 's/^Worker=//p')

wait_for_job() {
  for _ in $(seq 1 120); do
    status=$(curl -fsS "$api/systems/$system_id/job" | json_field 'd["data"]["status"] if d["data"] else "none"')
    [[ "$status" != "running" ]] && { echo "$status"; return; }
    sleep 0.5
  done
  echo "timeout"
}

echo "Uploading $POPULATE test packages to '$SYSTEM_NAME' (copies kept in $TEST_PACKAGES)..."
mkdir -p "$TEST_PACKAGES"; : > "$TEST_PACKAGES/descriptions.txt"
last_deployed=""
for i in $(seq 1 "$POPULATE"); do
  version="2.0.$i"
  zip_path=$(build_test_package "$i")
  description=$(test_description "$i")
  package_id=$(curl -fsS -X POST "$api/systems/$system_id/packages" -F "description=$description" -F "package=@$zip_path" | json_field 'd["data"]["id"]')
  printf '  %-28s %s\n' "SampleApp-Release-$version.zip" "$description"
  # Deploy every fifth package (alternating partial and whole-package updates) so badges and history vary.
  if (( i % 5 == 0 )); then
    mode=$([[ $(( i / 5 % 2 )) -eq 0 ]] && echo full || echo partial)
    curl -fsS -o /dev/null -X POST "$api/packages/$package_id/deploy" -H 'Content-Type: application/json' \
      -d "{\"modules\":[{\"moduleId\":$webapi_id,\"mode\":\"$mode\",\"exclusions\":\"web.config\\nappsettings*.json\\nlogs/\"},{\"moduleId\":$worker_id,\"mode\":\"partial\",\"exclusions\":\"worker.settings.json\\nlogs/\"}],\"autoRestore\":true}"
    echo "    deployed ($mode): $(wait_for_job)"
    last_deployed="$package_id"
  fi
done
if [[ -n "$last_deployed" ]]; then
  curl -fsS -o /dev/null -X POST "$api/packages/$last_deployed/rollback"
  echo "  rolled back the latest deployment: $(wait_for_job)"
fi
echo "Done. Open $DEVTOOLS_URL/tools/deployment and open '$SYSTEM_NAME'."
