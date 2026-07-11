# DevTools Project Context

## Product

DevTools is a privacy-friendly ASP.NET Core web application containing focused utilities for software developers. Current tools cover hashing, multi-language formatting, ad-hoc and indexed log analysis, database documentation/diagrams, and local system-service operations. Prefer utilities that work locally and do not persist user input unless the feature explicitly requires configuration.

## Technology

- .NET 9 / ASP.NET Core MVC
- Bootstrap 5 with small project-owned CSS and vanilla JavaScript
- `System.ServiceProcess.ServiceController` for local Windows service control; IIS application pools use the installed Windows `appcmd.exe` utility.
- C# nullable reference types and implicit usings enabled
- No database until a feature explicitly requires persistence
- SQLite via Entity Framework Core. Database paths come from the `DatabasePaths` section in `src/DevTools.Web/appsettings.json`; relative paths resolve from the web content root. Configuration and SQL Doc data use `ConfigurationDatabase`, while the rebuildable System Logs index uses the independently deletable `LogIndexDatabase`.
- Optional cookie authentication is configured by the `Login` section in `appsettings.json`. When `UseLogin` is true, the fallback authorization policy protects all MVC and API endpoints except the login page and static assets. Credentials are compared against the configured MD5 hashes and must never be logged.

## Architecture

- `src/DevTools.Web`: composition root, MVC controllers, views, and static assets. Keep controllers thin.
- `src/DevTools.Application`: use-case interfaces and implementations. Each tool has a focused interface so implementations can be replaced and tested independently.
- `src/DevTools.Shared`: reusable request/response models, enums, constants, and stateless helpers. It must not depend on Web or Application.

Dependencies point inward: Web -> Application -> Shared. Web may also reference Shared for contracts. Do not introduce a reverse dependency.

## Engineering Rules

- Apply SOLID principles: focused types, dependency injection, small interfaces, and extension through new services rather than large switch-heavy controllers.
- Put reusable models in `DevTools.Shared/Models` and reusable stateless helpers in `DevTools.Shared/Helpers`.
- Validate all input at the boundary and return useful, safe error messages. Never expose stack traces to the browser.
- Treat tool input as sensitive. Do not log or persist source text, logs, hashes, tokens, or formatted payloads.
- System Logs stores configuration plus a rebuildable SQLite search index of parsed log entries. The index exists only for local querying, pagination, statistics, and full-text search; never write indexed content to application logs or external services.
- System Logs discovery has no file-count cap. Refresh incrementally skips unchanged files, appends newly written UTF-8 lines where safe, and rebuilds only changed/truncated files.
- System Logs indexing runs as a shared server-side background job. Leaving the page must not cancel it; concurrent users attach to the same per-system job and can view its progress.
- Clearing a System Logs index runs as a shared background job, explicitly deletes indexed entries, checkpoints WAL, vacuums the index database, and removes/compacts any legacy index tables left in `devtools.db` from older builds. Index and clear jobs are mutually exclusive per system across concurrent users.
- Each System Logs module persists a reader mode (`auto`, `text`, `json`, or `custom`). Custom regex readers require lowercase named `timestamp`, `level`, and `message` groups and must use a match timeout.
- System Services stores only logical system/service configuration in `devtools.db`. It controls services on the local Windows host only; start, stop, and restart are explicit user actions, serialized per target, and may require administrator permission.
- Prefer built-in framework functionality. Add NuGet packages only when they remove substantial, well-tested complexity.
- Keep JavaScript framework-free unless application complexity clearly justifies a change.
- New tools should include a shared contract, an application service interface/implementation, a thin endpoint, and a workbench UI panel.
- Every toolbox entry must have its own route, Razor partial component, and isolated `*-tool.js` script. Shared navigation/toast behavior belongs in `site.js`.

## Verification

Run `dotnet build DevTools.sln` after changes. Add unit tests for parsing, transformations, validation, and edge cases as the toolset grows.
