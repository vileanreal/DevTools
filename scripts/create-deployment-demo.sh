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
#   scripts/create-deployment-demo.sh --patch-demo     # two mock systems for Releases: build folders at v1
#                                                       # marked as the latest version, then a simulated new build so
#                                                       # the module table and Compile have changes to show. The second
#                                                       # ("Customer Portal Patches") has ~130 changes across folders.
#   scripts/create-deployment-demo.sh --large-package  # only write SampleApp-Release-3.0-large.zip (~270 files in many
#                                                       # folders, for the Sample App system) to ~/Downloads, to try the
#                                                       # folder groups and "Show more" in the package and deploy dialogs
#   scripts/create-deployment-demo.sh --version-tests  # packages for the Deployment version check: builds a small .NET
#                                                       # SampleApp.Api.dll at several versions (needs the .NET SDK), puts
#                                                       # 1.0.0 on the mock WebApi server, writes test packages to
#                                                       # ~/Downloads/SampleApp-version-tests and turns the check on for
#                                                       # the Sample App system (WebApi main file bin/SampleApp.Api.dll)
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
PATCH_DEMO=0
LARGE_ONLY=0
VERSION_TESTS=0
case "${1:-}" in
  --version-tests) VERSION_TESTS=1 ;;
  --large-package) LARGE_ONLY=1 ;;
  --populate) POPULATE="${2:-24}" ;;
  --make-packages) POPULATE="${2:-24}"; MAKE_ONLY=1 ;;
  --patch-demo) PATCH_DEMO=1 ;;
  "") ;;
  *) echo "Usage: $0 [--populate N | --make-packages N | --patch-demo | --large-package | --version-tests]" >&2; exit 1 ;;
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

# ---------- A large package: many files in many folders ----------
LARGE_PACKAGE="SampleApp-Release-3.0-large.zip"
build_large_package() {
  local root="$work/large/release-3.0"
  rm -rf "$work/large"
  # WebApi: replaces the existing dlls and adds many new files across folders.
  write "$root/WebApi/bin/SampleApp.Api.dll"  "SampleApp.Api v3.0.0"
  write "$root/WebApi/bin/SampleApp.Core.dll" "SampleApp.Core v3.0.0"
  for i in $(seq -f %02g 1 38); do write "$root/WebApi/bin/SampleApp.Module$i.dll" "module $i v3.0.0"; done
  for i in $(seq -f %02g 1 60); do write "$root/WebApi/wwwroot/js/page-$i.js" "console.log('page $i v3');"; done
  write "$root/WebApi/wwwroot/js/app.js" "console.log('SampleApp web v3.0.0');"
  for i in $(seq -f %02g 1 15); do write "$root/WebApi/wwwroot/css/theme-$i.css" "/* theme $i v3 */"; done
  for i in $(seq -f %02g 1 30); do write "$root/WebApi/wwwroot/images/banner-$i.svg" "<svg><!-- banner $i --></svg>"; done
  for i in $(seq -f %02g 1 25); do write "$root/WebApi/Views/Shared/Partial$i.cshtml" "@* partial $i v3 *@"; done
  for i in $(seq -f %02g 1 12); do write "$root/WebApi/Views/Reports/Report$i.cshtml" "@* report $i v3 *@"; done
  write "$root/WebApi/web.config" "<configuration><!-- build settings: excluded by the module --></configuration>"
  write "$root/WebApi/SampleApp.WebApi.deps.json" '{ "version": "3.0.0" }'
  # Worker: replaced files plus new plugins and job definitions.
  write "$root/Worker/SampleApp.Worker.exe" "SampleApp.Worker v3.0.0"
  write "$root/Worker/SampleApp.Worker.dll" "SampleApp.Worker v3.0.0"
  for i in $(seq -f %02g 1 30); do write "$root/Worker/Plugins/Exporter$i.dll" "exporter $i v3.0.0"; done
  for i in $(seq -f %02g 1 40); do write "$root/Worker/Jobs/job-$i.json" "{ \"job\": $i }"; done
  mkdir -p "$DOWNLOADS"
  rm -f "$DOWNLOADS/$LARGE_PACKAGE"
  (cd "$work/large" && zip -qr -X "$DOWNLOADS/$LARGE_PACKAGE" release-3.0)
  echo "Large package: $DOWNLOADS/$LARGE_PACKAGE ($(find "$root" -type f | wc -l | tr -d ' ') files)"
}
if [[ "$LARGE_ONLY" -eq 1 ]]; then build_large_package; exit 0; fi

# ---------- Packages for the Deployment version check ----------
if [[ "$VERSION_TESTS" -eq 1 ]]; then
  # Prefer a user-local SDK (~/.dotnet), which is where the .NET 9 SDK is often installed on macOS.
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then dotnet_cmd="$HOME/.dotnet/dotnet"; else dotnet_cmd="$(command -v dotnet || true)"; fi
  [[ -x "$dotnet_cmd" ]] || { echo "The .NET SDK is needed to build the test dll (dotnet was not found)." >&2; exit 1; }
  out="$DOWNLOADS/SampleApp-version-tests"
  lib="$work/versionlib"
  mkdir -p "$lib" "$out"
  cat > "$lib/SampleApp.Api.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>SampleApp.Api</AssemblyName><GenerateDocumentationFile>false</GenerateDocumentationFile></PropertyGroup>
</Project>
XML
  echo 'namespace SampleApp.Api { public static class Info { public const string Name = "SampleApp.Api"; } }' > "$lib/Info.cs"
  build_dll() { # $1 = version, prints the dll path
    if ! "$dotnet_cmd" build "$lib/SampleApp.Api.csproj" -c Release -nologo -v q -p:Version="$1" -p:FileVersion="$1.0" -p:AssemblyVersion="$1.0" -o "$work/dll-$1" > "$work/build-$1.log" 2>&1; then
      echo "Building the test dll failed (needs the .NET 9 SDK):" >&2; tail -20 "$work/build-$1.log" >&2; exit 1
    fi
    echo "$work/dll-$1/SampleApp.Api.dll"
  }
  version_package() { # $1 = zip name, $2 = dll to include (or "none" / "text")
    local root="$work/vt/$1"
    rm -rf "$root"; mkdir -p "$root/WebApi/bin"
    case "$2" in
      none) ;;
      text) echo "not a real dll" > "$root/WebApi/bin/SampleApp.Api.dll" ;;
      *) cp "$2" "$root/WebApi/bin/SampleApp.Api.dll" ;;
    esac
    write "$root/WebApi/wwwroot/js/app.js" "console.log('$1');"
    rm -f "$out/$1.zip"
    (cd "$root" && zip -qr -X "$out/$1.zip" WebApi)
  }

  echo "Building SampleApp.Api.dll 0.9.0, 1.0.0 and 1.1.0..."
  v090=$(build_dll 0.9.0) || exit 1; v100=$(build_dll 1.0.0) || exit 1; v110=$(build_dll 1.1.0) || exit 1
  echo "Putting version 1.0.0 on the mock WebApi server ($servers/WebApi/bin)..."
  mkdir -p "$servers/WebApi/bin"
  cp "$v100" "$servers/WebApi/bin/SampleApp.Api.dll"

  version_package "1-same-version-1.0.0" "$v100"
  version_package "2-newer-version-1.1.0" "$v110"
  version_package "3-older-version-0.9.0" "$v090"
  version_package "4-main-file-missing" none
  version_package "5-no-readable-version" text
  cat > "$out/README.txt" <<'TXT'
Version check test packages for the Sample App system (module WebApi, main file bin/SampleApp.Api.dll).
The mock WebApi server has SampleApp.Api.dll version 1.0.0.

Upload each package, open Deploy and choose an update type. With "Require a new version on every deployment" on:
  1-same-version-1.0.0.zip    blocked: same version as the server
  2-newer-version-1.1.0.zip   allowed: 1.0.0 -> 1.1.0
  3-older-version-0.9.0.zip   blocked: older than the server (use Rollback to go back)
  4-main-file-missing.zip     blocked: the package doesn't contain the main file
  5-no-readable-version.zip   blocked: the main file has no readable version
With the setting off, nothing is blocked; the versions are only shown.
After deploying 2-newer-version-1.1.0.zip, the server has 1.1.0, so the newer package becomes "same" (blocked) if uploaded again.
Run the script with --version-tests again to put 1.0.0 back on the server.
TXT

  if [[ "${SKIP_REGISTER:-0}" != "1" ]] && curl -fsS -o /dev/null "$DEVTOOLS_URL/api/deployment/systems"; then
    echo "Turning the version check on for '$SYSTEM_NAME' (WebApi main file bin/SampleApp.Api.dll)..."
    curl -fsS "$DEVTOOLS_URL/api/deployment/systems" | python3 -c '
import json, sys, urllib.request
url, name = sys.argv[1], sys.argv[2]
system = next((item for item in json.load(sys.stdin)["data"] if item["name"] == name), None)
if system is None:
    sys.exit(f"System {name!r} not found; run the script without options first.")
def put(path, body):
    request = urllib.request.Request(url + path, data=json.dumps(body).encode(), method="PUT", headers={"Content-Type": "application/json"})
    urllib.request.urlopen(request).read()
sid = system["id"]
put(f"/api/deployment/systems/{sid}", {"name": system["name"], "backupDirectory": system["backupDirectory"], "packageDirectory": system["packageDirectory"], "requireVersionChange": True})
module = next(item for item in system["modules"] if item["name"] == "WebApi")
mid = module["id"]
put(f"/api/deployment/systems/{sid}/modules/{mid}", {"name": module["name"], "targetPath": module["targetPath"], "appPoolName": module["appPoolName"],
    "serviceName": module["serviceName"], "defaultExclusions": module["defaultExclusions"], "versionFile": "bin/SampleApp.Api.dll"})
' "$DEVTOOLS_URL" "$SYSTEM_NAME"
  else
    echo "DevTools isn't reachable (or SKIP_REGISTER=1): in Sample App, set WebApi's main file to bin/SampleApp.Api.dll and turn on Require a new version."
  fi
  echo "Test packages: $out (see README.txt)"
  exit 0
fi

# ---------- Mock system for Releases ----------
if [[ "$PATCH_DEMO" -eq 1 ]]; then
  build="$DEMO_ROOT/build"
  patch_system="${PATCH_SYSTEM_NAME:-Sample App Patches}"
  papi="$DEVTOOLS_URL/api/patch-management"
  if ! curl -fsS -o /dev/null "$papi/systems"; then echo "DevTools isn't reachable at $DEVTOOLS_URL (or login is on)." >&2; exit 1; fi
  jstr() { python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$1"; }

  echo "Writing version 1 of the build to $build..."
  rm -rf "$build"
  write "$build/WebApi/bin/SampleApp.Api.dll"             "SampleApp.Api v1.0.0"
  write "$build/WebApi/bin/SampleApp.Core.dll"            "SampleApp.Core v1.0.0"
  write "$build/WebApi/bin/SampleApp.Reports.dll"         "SampleApp.Reports v1.0.0"
  write "$build/WebApi/bin/Legacy.Export.dll"             "Legacy.Export v1.0.0 (removed in 1.1)"
  write "$build/WebApi/wwwroot/js/app.js"                 "console.log('SampleApp web v1.0.0');"
  write "$build/WebApi/wwwroot/css/site.css"              "body { color: #333; } /* v1.0 */"
  write "$build/WebApi/web.config"                        "<configuration><!-- ignored by the module --></configuration>"
  write "$build/WebApi/appsettings.Production.json"       '{ "ignored": "by the module" }'
  write "$build/WebApi/logs/build.log"                    "build 1.0"
  write "$build/Worker/SampleApp.Worker.exe"              "SampleApp.Worker v1.0.0"
  write "$build/Worker/SampleApp.Worker.dll"              "SampleApp.Worker v1.0.0"
  write "$build/Worker/Plugins/CsvExporter.dll"           "CsvExporter v1.0.0"
  mkdir -p "$DEMO_ROOT/patches"

  system_id=$(curl -fsS "$papi/systems" | python3 -c '
import json,sys
print(next((str(s["id"]) for s in json.load(sys.stdin)["data"] if s["name"]==sys.argv[1]), ""))' "$patch_system")
  if [[ -z "$system_id" ]]; then
    echo "Registering '$patch_system' in Releases..."
    system_id=$(curl -fsS -X POST "$papi/systems" -H 'Content-Type: application/json' \
      -d "{\"name\":$(jstr "$patch_system"),\"outputDirectory\":$(jstr "$DEMO_ROOT/patches")}" | python3 -c 'import json,sys; print(json.load(sys.stdin)["data"]["id"])')
    curl -fsS -o /dev/null -X POST "$papi/systems/$system_id/modules" -H 'Content-Type: application/json' \
      -d "{\"name\":\"WebApi\",\"sourcePath\":$(jstr "$build/WebApi"),\"exclusions\":$(jstr $'web.config\nappsettings*.json\nlogs/')}"
    curl -fsS -o /dev/null -X POST "$papi/systems/$system_id/modules" -H 'Content-Type: application/json' \
      -d "{\"name\":\"Worker\",\"sourcePath\":$(jstr "$build/Worker"),\"exclusions\":$(jstr 'logs/')}"
  else
    echo "'$patch_system' already exists (id $system_id); resetting its build to version 1."
  fi

  echo "Marking version 1 as the latest version (what UAT/production has)..."
  curl -fsS -o /dev/null -X POST "$papi/systems/$system_id/latest" -H 'Content-Type: application/json' -d '{"label":"UAT baseline v1.0"}'

  sleep 1.2   # so changed files get a newer timestamp
  echo "Simulating build 1.1..."
  write "$build/WebApi/bin/SampleApp.Api.dll"             "SampleApp.Api v1.1.0"            # CHANGED
  write "$build/WebApi/bin/SampleApp.Core.dll"            "SampleApp.Core v1.1.0"           # CHANGED
  write "$build/WebApi/bin/SampleApp.Invoicing.dll"       "SampleApp.Invoicing v1.1.0 (new)" # ADDED
  write "$build/WebApi/wwwroot/js/invoices.js"            "console.log('invoice export v1.1.0');" # ADDED
  write "$build/WebApi/wwwroot/js/app.js"                 "console.log('SampleApp web v1.1.0');"  # CHANGED
  rm -f "$build/WebApi/bin/Legacy.Export.dll"                                                 # DELETED
  touch "$build/WebApi/bin/SampleApp.Reports.dll"          # rebuilt but identical: not a change (same hash)
  write "$build/WebApi/web.config"                        "<configuration><!-- edited, but ignored --></configuration>"
  echo "build 1.1" >> "$build/WebApi/logs/build.log"       # ignored folder
  write "$build/Worker/SampleApp.Worker.dll"              "SampleApp.Worker v1.1.0"         # CHANGED
  write "$build/Worker/Plugins/PdfExporter.dll"           "PdfExporter v1.1.0 (new)"        # ADDED
  rm -f "$build/Worker/Plugins/CsvExporter.dll"                                              # DELETED

  echo "Done. Open $DEVTOOLS_URL/tools/patch-management?system=$system_id — the pending changes load automatically."
  echo "Expected: 4 changed, 3 added, 2 deleted (web.config, logs/ and the untouched Reports.dll are not listed)."

  # A second, larger system: many changes spread over several folders, for trying folder groups and "Show more".
  big="$DEMO_ROOT/portal-build"
  big_system="${PATCH_LARGE_SYSTEM_NAME:-Customer Portal Patches}"
  echo
  echo "Writing version 1 of the larger build to $big..."
  rm -rf "$big"
  for i in $(seq -f %02g 1 40); do write "$big/Portal/wwwroot/js/page-$i.js" "page $i v1"; done
  for i in $(seq -f %02g 1 8);  do write "$big/Portal/wwwroot/css/theme-$i.css" "theme $i v1"; done
  for i in $(seq -f %02g 1 12); do write "$big/Portal/wwwroot/images/banner-$i.png" "banner $i v1"; done
  for i in $(seq -f %02g 1 25); do write "$big/Portal/bin/Portal.Feature$i.dll" "feature $i v1"; done
  for i in $(seq -f %02g 1 10); do write "$big/Portal/Views/Shared/Partial$i.cshtml" "partial $i v1"; done
  write "$big/Portal/Portal.dll" "Portal v1"
  write "$big/Portal/web.config" "<configuration><!-- ignored --></configuration>"
  for i in $(seq -f %02g 1 10); do write "$big/Scheduler/bin/Scheduler.Lib$i.dll" "lib $i v1"; done
  for i in $(seq -f %02g 1 30); do write "$big/Scheduler/Jobs/job-$i.json" "{\"job\": $i, \"v\": 1}"; done
  write "$big/Scheduler/Scheduler.exe" "Scheduler v1"
  for i in $(seq -f %02g 1 5);  do write "$big/Reports/Templates/report-$i.rdlc" "report $i v1"; done

  big_id=$(curl -fsS "$papi/systems" | python3 -c '
import json,sys
print(next((str(s["id"]) for s in json.load(sys.stdin)["data"] if s["name"]==sys.argv[1]), ""))' "$big_system")
  if [[ -z "$big_id" ]]; then
    echo "Registering '$big_system' in Releases..."
    big_id=$(curl -fsS -X POST "$papi/systems" -H 'Content-Type: application/json' \
      -d "{\"name\":$(jstr "$big_system"),\"outputDirectory\":$(jstr "$DEMO_ROOT/patches")}" | python3 -c 'import json,sys; print(json.load(sys.stdin)["data"]["id"])')
    for module in Portal Scheduler Reports; do
      curl -fsS -o /dev/null -X POST "$papi/systems/$big_id/modules" -H 'Content-Type: application/json' \
        -d "{\"name\":\"$module\",\"sourcePath\":$(jstr "$big/$module"),\"exclusions\":$(jstr $'web.config\nlogs/')}"
    done
  else
    echo "'$big_system' already exists (id $big_id); resetting its build to version 1."
  fi
  curl -fsS -o /dev/null -X POST "$papi/systems/$big_id/latest" -H 'Content-Type: application/json' -d '{"label":"Portal 3.0 (production)"}'

  sleep 1.2
  echo "Simulating build 3.1..."
  for i in $(seq -f %02g 1 22);  do write "$big/Portal/wwwroot/js/page-$i.js" "page $i v3.1"; done        # 22 CHANGED
  for i in $(seq -f %02g 41 52); do write "$big/Portal/wwwroot/js/page-$i.js" "page $i (new)"; done        # 12 ADDED
  for i in $(seq -f %02g 1 3);   do write "$big/Portal/wwwroot/css/theme-$i.css" "theme $i v3.1"; done     # 3 CHANGED
  for i in $(seq -f %02g 8 12);  do rm -f "$big/Portal/wwwroot/images/banner-$i.png"; done                # 5 DELETED
  for i in $(seq -f %02g 1 18);  do write "$big/Portal/bin/Portal.Feature$i.dll" "feature $i v3.1"; done   # 18 CHANGED
  for i in $(seq -f %02g 26 29); do write "$big/Portal/bin/Portal.Feature$i.dll" "feature $i (new)"; done  # 4 ADDED
  for i in $(seq -f %02g 1 4);   do write "$big/Portal/Views/Shared/Partial$i.cshtml" "partial $i v3.1"; done # 4 CHANGED
  write "$big/Portal/Portal.dll" "Portal v3.1"                                                       # 1 CHANGED (module root)
  for i in $(seq -f %02g 1 6);   do write "$big/Scheduler/bin/Scheduler.Lib$i.dll" "lib $i v3.1"; done     # 6 CHANGED
  for i in $(seq -f %02g 31 50); do write "$big/Scheduler/Jobs/job-$i.json" "{\"job\": $i, \"new\": true}"; done # 20 ADDED
  for i in $(seq -f %02g 1 5);   do rm -f "$big/Scheduler/Jobs/job-$i.json"; done                         # 5 DELETED
  # Reports is untouched, so it shows as up to date.

  echo "Done. Open $DEVTOOLS_URL/tools/patch-management?system=$big_id"
  echo "Expected: Portal 48 changed, 16 added, 5 deleted; Scheduler 6 changed, 20 added, 5 deleted; Reports up to date."
  exit 0
fi

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
build_large_package

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
