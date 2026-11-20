# System Logs: automatic refresh (backlog)

Status: **not started**. Captured 2026-11-20 for later.

## Goal

Keep the System Logs index up to date automatically, so users don't need to click **Index logs** to see new entries.

## Why not just call the current refresh on a timer

Each refresh currently reads and SHA-256 hashes every unchanged file in full:

- Files with the same size are hashed to confirm they are unchanged (`LogIndexService.cs`, the `record.Length == info.Length` branches in the refresh loop).
- Files that grew are fully re-hashed after their new lines are appended (`finalContentHash` in `ReindexFileAsync`).

So the cost of a refresh grows with the **total size of the log folders**, not with the amount of new data. Rough cost per refresh (assuming hashing at about 0.5–1 GB/s plus disk reads):

| Logs on disk | Cost of each refresh | Polled every 30 s |
|---|---|---|
| ~200 MB | under a second | barely noticeable |
| ~5 GB | several seconds of full disk reads and one CPU core | constant background load |
| 50 GB+ (a long retention folder) | about a minute or more | effectively never idle; competes with the IIS sites on the server |

## Plan

1. **Cheaper change check.** Treat a file as unchanged when its size and last-modified time both match, without reading it. Keep the full content-hash check for manual **Index logs** clicks, or as an occasional deep check. Most files in a log folder are old, rotated files, so this removes most of the work.
2. **Read only new data.** For files that grew, read only the appended bytes and skip the full re-hash. Store enough state to detect truncation or rotation, for example the length plus a hash of the first few KB. Much of the append logic already exists.
3. **Watch for changes instead of only polling.** Use `FileSystemWatcher` on each module's folder (honouring `IncludeSubdirectories` and the file name filter). Debounce bursts of writes into one refresh per system. Keep a slow fallback timer (every few minutes), because watcher events can be missed on network shares and under heavy load.
4. **Safeguards.**
   - A configurable minimum interval per system (for example 1–5 minutes).
   - Never start an automatic run while an index or clear job is running for that system (reuse the existing per-system job lock).
   - Automatic runs should use the existing shared background job, so the UI shows their progress like a manual run.
   - Let users turn automatic refresh off per system.

With 1–3 in place, a refresh where nothing changed only looks up each file's size and date, which takes milliseconds even for thousands of files.

## Hosting caveats (IIS)

- IIS shuts down an idle app pool after 20 minutes by default, and any background timer or watcher stops with it. For refresh with nobody on the page, configure the app pool with **Start Mode = AlwaysRunning** and the site with **Preload Enabled** (document this in the README deployment notes). Alternatively, only poll while someone has System Logs open.
- Searches and indexing share the SQLite index file (WAL mode). Small, frequent updates are fine; frequent large rebuilds would slow searches down.

## Constraints from AGENTS.md to keep

- Never write indexed log content to application logs or external services.
- Discovery has no file-count cap and respects each module's subfolder setting.
- Index and clear jobs stay mutually exclusive per system across concurrent users.

## Open questions

- Should the interval be per system or global?
- Should automatic refresh run when nobody has the page open (needs the IIS settings above), or only while the page is open?
- Should the occasional deep content-hash check be scheduled (for example nightly) or manual only?
