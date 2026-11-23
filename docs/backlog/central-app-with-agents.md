# One central DevTools with agents on target servers (backlog)

Status: **not started**. Captured 2026-11-23 for later.

## Goal

Install DevTools once, centrally, instead of on every server. Each target server runs a small **agent** that does the local work (services, app pools, deployments, logs, folder scans) on the central app's behalf.

## Shape

- **Central DevTools (one install):** web UI, users, configuration database, histories, package storage. Decides what should happen, orchestrates jobs, shows progress.
- **Agent (one per target server):** a small Windows service — single-file .NET 9 worker, nothing else to install. Executes jobs locally and reports progress. No UI.

## Connection: the agent connects out to central

- Each agent keeps a persistent outbound HTTPS connection (SignalR or WebSockets) to central; central sends jobs down it.
- No inbound ports on target servers (firewall-friendly), and central never stores credentials for them.
- Central knows immediately when an agent is offline.
- Not WinRM / PowerShell remoting: that needs inbound ports and admin credentials stored centrally.

## Reuse of the current code

The Application layer is already behind interfaces (`IDeploymentExecutor`, `ISystemServiceRuntime`, `IPatchScanner`, log readers). The agent hosts the same implementations; central gets "remote" implementations of those interfaces that send a job to the right agent and stream progress back. Systems and modules gain a **Server** (agent) field.

| Tool | Central | Agent |
|---|---|---|
| Deployment | Packages, plans, history, version check | Receives the package (streamed, SHA-256 checked, resumable), backs up, stops, patches, starts, rolls back |
| System Services | Configuration and buttons | Reads status, starts/stops |
| System Logs | Queries, charts | Keeps its own index of that server's logs and answers queries (logs never move in bulk) |
| Releases | Snapshots, patches, version history | Scans the build folders (on the build server) |
| Connectivity checker | Results | Runs the checks from that server |
| File explorer (read-only) | UI | Lists, previews and streams files from allowed locations |

## Security (central becomes a high-value target)

1. **Named users, roles and an audit log first** (viewer / operator / admin; who did what on which server).
2. **Agent enrollment:** a one-time token installs the agent, which then receives its own key or certificate. Agents accept jobs only from their central server, always over TLS.
3. **The agent enforces its own limits** from a local config file: which folders, services and app pools it may touch. Even a compromised central can't go beyond them. This is the most important safeguard.
4. Agents run with least privilege; file contents and secrets are never logged.

## Edge cases

- **Agent offline:** jobs show "server offline"; nothing is queued silently; a deployment starts only when all its target agents are online.
- **Version mismatch:** central and agents check compatibility on connect → leads to **agent self-update** from central (ties into the self-update idea).
- **Large packages:** chunked, resumable, hash-checked streaming; never buffered in memory.
- **Central down:** nobody can deploy, but target servers keep running; agents only act on request.
- **Disconnect mid-deployment:** the agent finishes or restores on its own and reports when it reconnects.

## When it's worth it

- Worth it with several servers (UAT/production, multiple client environments) where DevTools is installed repeatedly.
- With two or three servers, separate installs (plus maybe a simple central page linking to them) are much cheaper.

## Order

1. Users, roles and audit log.
2. Agent skeleton: enrollment, outbound connection, heartbeat and online status per server; **System Services** as the first remote tool.
3. **Deployment** through the agent (package transfer, then the existing executor).
4. **System Logs** queries, **Connectivity checker**, read-only **File explorer**.
5. Agent self-update.

## Estimate

Several weeks, mostly steps 1–3 plus testing failure cases.
