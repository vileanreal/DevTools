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
- Optional cookie authentication is enabled by `Login:UseLogin` in `appsettings.json`. Unsalted SHA-256 credential hashes come from the ignored `src/DevTools.Web/.env` file through `Login__UsernameSha256` and `Login__PasswordSha256`. When login is enabled, the fallback authorization policy protects all MVC and API endpoints except the login page and static assets. Credentials must never be logged.

## Hosting and Deployment

- Local `dotnet run` uses the Development environment and `appsettings.Development.json`, which clears `Hosting:PathBase`; local routes, login redirects, cookies, assets, and APIs therefore run from `/`.
- The committed Production configuration mounts the application at `/DevTools`. Login redirects to `/DevTools/`, and the authentication cookie path is `/DevTools`.
- Always deploy the complete output of `dotnet publish src/DevTools.Web -c Release -o <target>`. Do not deploy the project directory or `bin/Release`; generated static assets such as `DevTools.Web.styles.css` are materialized correctly only in the publish output.
- For IIS, place `.env` beside `DevTools.Web.dll` in the publish directory, install the .NET 9 Hosting Bundle, and grant the application-pool identity read/execute access plus write access to configured SQLite directories.
- Keep IIS in Production unless intentionally debugging locally. Browser-facing error pages must remain environment-neutral and must never recommend enabling Development on a deployed server.

## Architecture

- `src/DevTools.Web`: composition root, MVC controllers, views, and static assets. Keep controllers thin.
- `src/DevTools.Application`: use-case interfaces and implementations. Each tool has a focused interface so implementations can be replaced and tested independently.
- `src/DevTools.Shared`: reusable request/response models, enums, constants, and stateless helpers. It must not depend on Web or Application.

Dependencies point inward: Web -> Application -> Shared. Web may also reference Shared for contracts. Do not introduce a reverse dependency.

## Engineering Rules

- Use `main` as the only working branch. Commit and push directly to `main`; do not create feature branches or pull requests unless the user explicitly requests one.
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
- Client-side requests and navigation must use the shared `appUrl`/`jsonRequest` helpers or ASP.NET Core URL generation so the application works both at `/` and under an IIS application path such as `/DevTools`.
- The optional `Hosting:PathBase` setting forces the deployment mount path when IIS or a reverse proxy doesn't populate `Request.PathBase`; leave it empty for root hosting.
- Client-generated IDs must not depend solely on secure-context-only browser APIs such as `crypto.randomUUID()`. Use the shared `createUuid` helper so tools continue to work over an internal HTTP deployment.

## Verification

Run `dotnet build DevTools.sln` after changes. For hosting or path changes, also publish to a clean directory and verify both Development at `/` and Production at `/DevTools`: login form action, successful and failed login, authentication cookie path, protected MVC redirects, anonymous API `401` responses, tool routes, static assets, and API calls. Add unit tests for parsing, transformations, validation, and edge cases as the toolset grows.
