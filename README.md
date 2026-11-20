# DevTools

DevTools is a local-first ASP.NET Core web application that collects practical utilities for developers in one clean interface. It is designed for internal and local use, with optional login protection and configurable SQLite storage.

## Features

- Hash generation with SHA-2, SHA-3, keyed HMAC, and legacy compatibility algorithms
- Multi-language code formatting with syntax highlighting
- Log file viewer
- System Logs with incremental indexing, parsing, filtering, charts, and background jobs
- SQL Doc for inspecting SQL Server, SQLite, MySQL, Oracle, and PostgreSQL databases
- Interactive database diagrams with saved layouts and PNG export
- Database schema comparison with side-by-side object diffs and target update scripts
- Word (`.docx`) database documentation generation
- Windows service and IIS application-pool monitoring and control
- Deployment of `.zip`/`.7z` packages to IIS sites and Windows services with automatic `.7z` backups, progress tracking, auto-restore and rollback
- Light and dark themes
- Optional cookie-based login protection

## Technology

- .NET 9 and ASP.NET Core MVC
- Bootstrap 5 and vanilla JavaScript
- Entity Framework Core with SQLite
- SQL Server, SQLite, MySQL, Oracle, and PostgreSQL database providers

## Project structure

```text
src/
├── DevTools.Web          ASP.NET Core MVC application and composition root
├── DevTools.Application  Use cases and service implementations
└── DevTools.Shared       Reusable contracts, models, enums, and helpers
```

Dependencies point inward: `Web → Application → Shared`.

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Windows is required for controlling Windows services and IIS application pools. Other tools can run wherever ASP.NET Core and the configured database providers are supported.

## Run locally

```powershell
dotnet restore DevTools.sln
dotnet run --project src/DevTools.Web/DevTools.Web.csproj
```

Open the HTTP or HTTPS address printed by ASP.NET Core in the terminal.

To build the complete solution:

```powershell
dotnet build DevTools.sln
```

## Configuration

Configuration is stored in `src/DevTools.Web/appsettings.json`.

For an IIS sub-application hosted at `http://server/DevTools`, configure:

```json
"Hosting": {
  "PathBase": "/DevTools"
}
```

Leave `PathBase` empty when hosting DevTools at the website root.

Individual tools can be removed from the home screen and their MVC routes by setting their switch to `false`:

```json
"Tools": {
  "Hash": true,
  "Formatter": true,
  "Logs": true,
  "SystemLogs": true,
  "SystemServices": true,
  "SqlDoc": true,
  "DatabaseCompare": true,
  "Deployment": true
}
```

For a repeatable IIS deployment, run PowerShell as Administrator from the repository root:

```powershell
.\scripts\Deploy-Iis.ps1
```

The script publishes a complete Release build, stops the `DevTools` application pool, preserves the destination `.env` and SQLite files, copies all runtime/static assets, reapplies inherited application-pool permissions, verifies required resources, and restarts the pool. Override `-AppPoolName`, `-Destination`, or `-PublishDirectory` when your IIS names or paths differ.

### Database Compare sample data

Generate a left desired schema and a deliberately older right target schema with:

```bash
./scripts/generate-database-compare-test-data.sh
```

The ignored SQLite files are created in `src/DevTools.Web/sample-databases`. Save both paths as SQLite connections in Database Compare, using `commerce-desired.db` on the left and `commerce-target.db` on the right. The pair includes added, deleted, changed, and unchanged tables, views, columns, and indexes.

SQLite does not support stored procedures, so long stored-procedure comparison samples are also provided in `scripts/database-compare-samples/sqlserver-desired.sql` and `sqlserver-target.sql`. Run them in two empty SQL Server test databases and compare the desired database on the left with the target database on the right. They include long changed procedures plus added, deleted, and unchanged procedures.

### SQLite paths

```json
"DatabasePaths": {
  "ConfigurationDatabase": "devtools.db",
  "LogIndexDatabase": "devtools-log-index.db"
}
```

Relative paths are resolved from the web application's content root. Database files and their SQLite WAL/SHM sidecars are ignored by Git.

### Login

The login switch remains in `appsettings.json`:

```json
"Login": {
  "UseLogin": true
}
```

Copy `src/DevTools.Web/.env.example` to `src/DevTools.Web/.env`. The local `.env` file is ignored by Git and contains the credential hashes:

```dotenv
Login__UsernameSha256=8c6976e5b5410415bde908bd4dee15dfb167a9c873fc4bb8a81f6f2ab448a918
Login__PasswordSha256=8c6976e5b5410415bde908bd4dee15dfb167a9c873fc4bb8a81f6f2ab448a918
```

The initial credentials are `admin` / `admin`. Change both hashes before exposing the application to other users. Set `UseLogin` to `false` to disable authentication. Operating-system environment variables with the same names take precedence over `.env` values.

Generate an unsalted SHA-256 value in PowerShell:

```powershell
$value = "your-value"
$bytes = [Text.Encoding]::UTF8.GetBytes($value)
$hash = [Security.Cryptography.SHA256]::HashData($bytes)
[Convert]::ToHexString($hash).ToLowerInvariant()
```

Run the command separately for the username and password. SHA-256 was selected because the configuration is quick to update and requires no salt, as intended for a locally hosted utility. Unsalted password hashes are vulnerable to offline dictionary attacks, so keep `.env` private, use a strong password and HTTPS, and do not expose this authentication scheme directly to the public internet.

### Deployment

The Deployment tool patches web apps and Windows services on the machine running DevTools, so you don't need remote desktop.

1. Create a **system** (optionally with its own **package folder** for uploads and **backup folder**) and add one **module** per deployable folder: the module name must match its top-level folder inside the package. Set the target folder, and optionally the IIS app pool and/or Windows service name, plus default files to exclude.
2. **Upload** a `.zip` or `.7z` with a required **description** (up to 500 characters). The upload is streamed to disk with progress (never held in memory). Top-level folders are matched to module names; a single wrapper folder (for example `release-2.0/`) is skipped automatically.
3. **Deploy**: choose the modules, then explicitly choose *Partial update* (replace and add files) or *Whole package* (delete everything except excluded files, then copy) for each one — nothing is preselected. Adjust the exclusions, use **Show files** to preview the files (patch files only by default — new and replaced; switch it off to also see excluded, deleted and kept files), and choose whether to auto-restore on failure. The job then:
   - backs up into one `mmddyyyy.7z` per deployment (`-2`, `-3`… for later ones that day): only the files that will be replaced for a partial update, the entire folder for a whole package;
   - stops every selected module's app pools and services;
   - patches every module;
   - starts the app pools and services that were running before.
   Progress, package status and history refresh live, including for jobs started from another browser.
4. **Rollback** restores a deployment from its backup and removes the files it added. Any deployed package can be rolled back while its backup exists, in any order. If a newer deployment changed the same module, the confirmation warns that its files will be overwritten.
5. **Packages list**: shows 10 rows per page by default (10/25/50). Click a package to see its details: description, dates, backup, the files in each module, and its history. A deployed package only offers **Rollback**; it can't be deployed again or deleted until it's rolled back. Uploading asks for confirmation when the system still has packages that were never deployed.
6. **History**: each package keeps a detailed, dated history (who, how long, what each module changed, which services were stopped and started, the backup's size, and any failed step) — uploaded (with notes such as a skipped wrapper folder or ignored folders), each deployment started and its outcome (deployed, failed, restored, interrupted), and each rollback — with the modules, backup file and any error.

To try it locally, run `scripts/create-deployment-demo.sh` with DevTools running (add `--populate 24` to also upload 24 test packages, deploy some and roll the last one back, for trying pagination, badges and history; or `--make-packages 24` to only write the test packages to `~/Downloads/SampleApp-test-packages` for uploading by hand). Test packages are kept in that folder with a `descriptions.txt`. It creates mock server folders with version 1 of a web app and a worker in `~/DevToolsDeploymentDemo`, writes a version 2 package to `~/Downloads/SampleApp-Release-2.0.zip`, and registers the **Sample App** system. Running it again resets the mock servers to version 1.

Exclusion patterns are one per line: a name such as `web.config` matches at any depth, `*`/`?` are wildcards, a trailing `/` means a folder (`logs/`), and patterns containing `/` are anchored to the module folder (`App_Data/**`). Excluded files are never replaced, deleted or restored over.

```json
"Deployment": {
  "WorkingDirectory": "deployments",
  "MaxUploadMegabytes": 4096,
  "MaxExtractedMegabytes": 20480
}
```

- `WorkingDirectory` holds temporary extraction folders, plus uploaded packages and backups for systems without their own package or backup folder. Relative paths resolve from the content root.
- Folder fields accept Windows paths (`C:\Backups\MyApp`, `\\server\share\backups`) on any OS, and local absolute paths such as `/Users/me/backups` for testing on macOS. A relative path resolves under the DevTools folder.
- IIS limits request bodies to about 30 MB by default. The project's `web.config` raises `maxAllowedContentLength` to the IIS maximum (about 4 GB); `MaxUploadMegabytes` is the effective limit.
- The DevTools app-pool identity needs modify permission on every module target folder, the backup folders and the working folder, and permission to stop and start the configured app pools and services (usually an administrator account).
- `.zip` and `.7z` are read and `.7z` backups are written with the MIT-licensed SharpCompress library; 7-Zip doesn't need to be installed.
- Modules can't target a drive root, DevTools' own folder or its working folder, and a system's package and backup folders must be outside every module folder.

### Hash algorithms

The Hash Tool supports SHA-256, SHA-384, SHA-512, SHA3-256, SHA3-384, SHA3-512, BLAKE2b-256, BLAKE2b-512, BLAKE2s-256, HMAC-SHA256, HMAC-SHA384, HMAC-SHA512, SHA-1, and MD5. HMAC algorithms require a separate secret key. SHA-1 and MD5 are retained only for compatibility and should not be used for security-sensitive work. SHA-3 and BLAKE2 use Bouncy Castle so their behavior is consistent on supported Windows/IIS installations.

## Data and privacy

- Utility input is intended to remain local.
- Runtime SQLite databases are not committed to source control.
- The System Logs index is rebuildable and stored separately from application configuration.
- Avoid committing environment-specific connection details or credentials.
- Deployment packages and backups stay on the DevTools machine, in the configured working and backup folders.

## License

No license has been selected yet. Add a license file before distributing or accepting external contributions.
