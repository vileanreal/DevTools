# Connectivity checker (backlog)

Status: **not started**. Captured 2026-11-23 for later.

## Goal

A tool that tests, from the DevTools server itself, whether it can reach the things a system depends on: names, ports, URLs, databases and file shares. It answers "is it the firewall, DNS, the certificate or the login?" without remote desktop and command-line tools.

## Checks

Enter a target and the matching checks run:

| Check | How | Answers |
|---|---|---|
| **DNS** | `Dns.GetHostAddressesAsync` | Does the name resolve, to which IPs, how long it took |
| **TCP port** | `TcpClient.ConnectAsync` with a timeout | Open / refused (nothing listening) / timed out (usually a firewall) |
| **HTTP(S)** | `HttpClient` GET or HEAD, redirects followed step by step, no retries | Status code, time, redirect chain; for HTTPS also TLS version and the certificate's subject, issuer and expiry (warn when under 30 days) |
| **Ping** | `System.Net.NetworkInformation.Ping` | Round trip time; reported as "blocked" rather than "down", since many hosts ignore ping |
| **Database** | A saved SQL Doc connection: open it and run `SELECT 1` | Login works and timing; provider errors translated (login failed, server not found, TLS problem) |
| **Folder / share** | A UNC path such as `\\server\share` | Whether this server's app-pool identity can list and write there (the usual cause of backup/deploy folder errors) |

## Diagnosis

Each check stops at the first failing layer and says why in plain words, for example: "DNS resolved to 10.1.2.3, but port 1433 timed out. This is usually a firewall between this server and the database." Refused means reachable but nothing listening; a timeout means something in between drops the traffic.

## Screen

- A quick-check box that accepts `db01:1433`, `https://api.client.local/health` or `\\files01\backups` and picks the checks.
- Optional **check sets per system** (for example "Sample App: API URL, DB server, file share"), run with one click.
- Results table: target, check, ✓ / ✗ / ⚠, time in ms, reason. **Copy report** for tickets.
- Checks run in parallel with about a 5 second timeout each; the run can be cancelled.

## Architecture (follows AGENTS.md)

- **Shared:** `ConnectivityTarget`, `ConnectivityCheckResult`, and a stateless parser that turns input into a target (unit-tested).
- **Application:** `IConnectivityChecker` with one small probe per check — `DnsProbe`, `TcpProbe`, `HttpProbe`, `PingProbe`, `DatabaseProbe` (reuses SQL Doc's provider code), `SharePathProbe` — each replaceable and testable on its own.
- **Web:** thin `ConnectivityController`, `_ConnectivityTool.cshtml`, `connectivity-tool.js`, a `ToolCatalog` entry and a `Tools:Connectivity` switch.
- **Persistence:** none for quick checks. Saved check sets need one table, added as an additive, versioned schema upgrade with a backup first.

## Safety

The server makes outbound requests to whatever is typed, which makes it a probe into the internal network.

- Only available when login is on (or off by default via `Tools:Connectivity`).
- No request bodies, custom headers or stored credentials, except saved database connections that already exist.
- Short timeouts and a cap on concurrent checks.
- Results are not logged; no URLs or hosts go to the application log.

## Tests

- Real local listeners: a `TcpListener` for "open", an unused port for "refused", an unroutable address for "timeout".
- A small local HTTP server returning redirects and errors.
- `localhost` for DNS.
- Parser tests for host:port, URLs, UNC paths and invalid input.

## Estimate

About a day for quick checks; about half a day more for saved check sets.
