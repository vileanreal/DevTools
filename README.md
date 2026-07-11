# DevTools

DevTools is a local-first ASP.NET Core web application that collects practical utilities for developers in one clean interface. It is designed for internal and local use, with optional login protection and configurable SQLite storage.

## Features

- Hash generation
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
Login__UsernameMd5=21232f297a57a5a743894a0e4a801fc3
Login__PasswordMd5=21232f297a57a5a743894a0e4a801fc3
```

The initial credentials are `admin` / `admin`. Change both hashes before exposing the application to other users. Set `UseLogin` to `false` to disable authentication. Operating-system environment variables with the same names take precedence over `.env` values.

> MD5 compatibility is currently provided by the application, but MD5 is not suitable for strong password storage. Keep configuration files private, use HTTPS, and migrate to a modern password hashing algorithm for internet-facing deployments.

## Data and privacy

- Utility input is intended to remain local.
- Runtime SQLite databases are not committed to source control.
- The System Logs index is rebuildable and stored separately from application configuration.
- Avoid committing environment-specific connection details or credentials.

## License

No license has been selected yet. Add a license file before distributing or accepting external contributions.
