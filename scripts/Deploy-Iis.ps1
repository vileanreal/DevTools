[CmdletBinding()]
param(
    [string]$AppPoolName = "DevTools",
    [string]$Destination = "C:\Sites\DevTools",
    [string]$PublishDirectory = "C:\Publish\DevTools"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot "src\DevTools.Web\DevTools.Web.csproj"
$destinationPath = [IO.Path]::GetFullPath($Destination)
$publishPath = [IO.Path]::GetFullPath($PublishDirectory)

if ([IO.Path]::GetPathRoot($destinationPath) -eq $destinationPath) {
    throw "Destination cannot be a drive root."
}

Import-Module WebAdministration -ErrorAction Stop

Write-Host "Publishing DevTools to $publishPath..."
dotnet publish $projectFile --configuration Release --output $publishPath
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$poolExists = Test-Path "IIS:\AppPools\$AppPoolName"
if ($poolExists) {
    Write-Host "Stopping IIS application pool $AppPoolName..."
    Stop-WebAppPool -Name $AppPoolName
}

try {
    New-Item -Path $destinationPath -ItemType Directory -Force | Out-Null
    Write-Host "Copying the complete publish output while preserving local .env and SQLite files..."
    Copy-Item -Path (Join-Path $publishPath "*") -Destination $destinationPath -Recurse -Force

    $principal = "IIS AppPool\$AppPoolName"
    & icacls.exe $destinationPath /inheritance:e /grant "${principal}:(OI)(CI)M" /T /C | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Unable to apply IIS application-pool permissions." }

    $requiredFiles = @(
        "DevTools.Web.dll",
        "DevTools.Application.dll",
        "BouncyCastle.Cryptography.dll",
        "web.config",
        "wwwroot\js\site.js",
        "wwwroot\js\system-logs-tool.js",
        "wwwroot\js\sql-doc-tool.js",
        "wwwroot\css\site.css",
        "wwwroot\favicon.ico"
    )
    $missingFiles = $requiredFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $destinationPath $_)) }
    if ($missingFiles) { throw "Deployment is incomplete. Missing: $($missingFiles -join ', ')" }

    if (-not (Test-Path -LiteralPath (Join-Path $destinationPath ".env"))) {
        Write-Warning "No production .env was found in $destinationPath. Login-enabled startup will fail until it is added."
    }
}
finally {
    if ($poolExists) {
        Write-Host "Starting IIS application pool $AppPoolName..."
        Start-WebAppPool -Name $AppPoolName
    }
}

Write-Host "Deployment completed. Open /DevTools and hard-refresh the browser (Ctrl+F5)." -ForegroundColor Green
