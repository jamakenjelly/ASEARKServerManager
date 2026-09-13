# Server Managers

Managers for different steam dedicated game servers.

This is a personal fork/standalone build of the original open-source ARK Server Manager project:
**https://github.com/Bletch1971/ServerManagers** — credit and thanks to the original authors and contributors.

## Changes in this fork

- **Auto-Backup overhaul**: replaced the old per-server "backup shortly after this server's own save" cadence with a single fleet-wide timer (Global Settings → "Servers Back Up Every"), so every enabled server saves and backs up together on one schedule. Backups now verify the save actually completed (retrying once if not) and skip a redundant forced save when the server's own native Auto Save just ran.
- **Save-health indicator**: a new ⚠️ tab icon flags any server whose last save didn't actually take effect, alongside the existing 💾 "backing up now" icon.
- **Status/Initializing watchdogs**: optional per-server timers that stop and restart a server if it stops responding, or if it gets stuck in "Initializing" for too long - now correctly disarmed on every manual stop/shutdown/restart path (not just the direct one), so a deliberately-stopped server is never fought by the watchdog.
- **Wipe scheduling removed**: the wipe-timer feature was fully removed (it was fundamentally broken - the overdue-detection logic could never fire); the Discord restart/shutdown schedule embed and admin panel are unaffected.
- **Discord improvements**:
  - Restart-timer embeds now split into separate PVE and PVP channels, based on each server's own PVP setting.
  - Added a self-healing watchdog for the Discord auto-post loop, so a stalled post cycle recovers on its own instead of requiring a manual "Post to Discord" click.
  - Fixed stale "ago" timestamps and per-server backup progress reporting in embeds.
- **PVE/PVP tab split**: the server tab strip now has a PVE/PVP mode toggle in the header, so you can flip between your PVE and PVP fleets instead of one long interleaved tab list.
- **Performance**: tab/mode switching is significantly faster - the game-data-derived dropdown lists (dinos, map spawners, items, supply crates) are now built once and cached instead of being rebuilt via reflection on every single switch.
- **WMI Provider Host fix**: server status polling now does one bulk WMI query per tick across all servers instead of one query per process per server, eliminating the `WBEM_E_CALL_CANCELLED` errors this used to generate on larger fleets.
- **Closing mid-backup**: closing the manager while a save/backup is in progress now shows a live-updating wait dialog (with a force-close option) instead of just a blocking prompt.
- **ArkApi Plugin Sync**: fixed the current profile being excluded from its own sync target list; added one-click "Select All PVE" / "Select All PVP" buttons.
