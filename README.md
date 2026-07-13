# DevTools

DevTools is a local-first ASP.NET Core web application that collects practical utilities for developers in one clean interface. It is designed for internal and local use, with optional login protection and configurable SQLite storage.

## Features

- Hash generation with SHA-2, SHA-3, keyed HMAC, and legacy compatibility algorithms
- Multi-language code formatting with syntax highlighting
- Log file viewer
- System Logs with incremental indexing, parsing, filtering, charts, and background jobs
- SQL Doc for inspecting SQL Server, SQLite, MySQL, Oracle, and PostgreSQL databases
- Interactive database diagrams with saved layouts and PNG export
- Word (`.docx`) database documentation generation
- Windows service and IIS application-pool monitoring and control
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

For a repeatable IIS deployment, run PowerShell as Administrator from the repository root:

```powershell
.\scripts\Deploy-Iis.ps1
```

The script publishes a complete Release build, stops the `DevTools` application pool, preserves the destination `.env` and SQLite files, copies all runtime/static assets, reapplies inherited application-pool permissions, verifies required resources, and restarts the pool. Override `-AppPoolName`, `-Destination`, or `-PublishDirectory` when your IIS names or paths differ.

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

### Hash algorithms

The Hash Tool supports SHA-256, SHA-384, SHA-512, SHA3-256, SHA3-384, SHA3-512, BLAKE2b-256, BLAKE2b-512, BLAKE2s-256, HMAC-SHA256, HMAC-SHA384, HMAC-SHA512, SHA-1, and MD5. HMAC algorithms require a separate secret key. SHA-1 and MD5 are retained only for compatibility and should not be used for security-sensitive work. SHA-3 and BLAKE2 use Bouncy Castle so their behavior is consistent on supported Windows/IIS installations.

## Data and privacy

- Utility input is intended to remain local.
- Runtime SQLite databases are not committed to source control.
- The System Logs index is rebuildable and stored separately from application configuration.
- Avoid committing environment-specific connection details or credentials.

## License

No license has been selected yet. Add a license file before distributing or accepting external contributions.
