# CLAUDE.md — RotaryPhone

## Cross-Service Boundary (IMPORTANT)

This service shares the Ubuntu box (`radio`) with Radio Console. **Read before any BT/audio work:**

**`docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md`** — Defines which BT adapter, profiles, and WirePlumber configs each service owns. Violating these boundaries will break the other service's audio.

Key rules:
- RotaryPhone owns **Intel AX201** (`hci1`, `10:91:D1:FE:00:46`) for voice/HFP
- Radio Console owns **TP-Link UB500** (`hci0`, `78:20:51:F5:FB:A7`) for music/A2DP
- Do NOT modify `/etc/wireplumber/bluetooth.lua.d/` without updating the boundary doc
- Always `bluetoothctl select 10:91:D1:FE:00:46` before any bluetoothctl commands
- If you need to change any boundary, update the boundary doc first

## Repo map

- `src/`: .NET 10 solution `RotaryPhoneController.sln` (Server, Core, GVBridge, GVTrunk, four test
  projects) and the React UI in `src/RotaryPhoneController.Client` (builds into the Server's `wwwroot`).
- `deploy/`: `Deploy-ToLinux.ps1`, box-side scripts (bridge Chrome, session alarm, auto-relogin, drift
  check), systemd units, the GNOME Shell extension, and shell test harnesses in `deploy/tests/`.
- `scripts/`: Python helpers, including `bt_manager.py` for the Bluetooth path.
- `docs/`: current docs (start at `docs/README.md`), ADRs in `docs/architecture/decisions/`, open plans
  in `docs/plans/`, and the Radio Console exchange lanes: `docs/prompts/` (inbound) and `docs/handoffs/`
  (outbound).
- `docs/archive/`: superseded plans, specs and closed exchanges. Not maintained; do not treat as current.
- Overview docs at the root: `README.md`, `ARCHITECTURE.md`, `ROADMAP.md`.

## Build, deploy and drift

- Build and test: `dotnet build RotaryPhoneController.sln`, `dotnet test RotaryPhoneController.sln`
  (needs the .NET 10 SDK).
- Deploy from Windows: `.\deploy\Deploy-ToLinux.ps1` (default target `radio`; `-PreflightOnly` checks
  transport without deploying). It never overwrites the box's `appsettings.Production.json`, which is
  the box's source of truth and holds its secrets. Never commit secrets to the repo copy.
- After installing box-side scripts the deploy runs `deploy/check-installed-drift.sh --group
  <alarm|bridge|relogin>`, which compares repo, shipped and installed copies (`~/bin`,
  `~/.config/systemd/user`). Exit 0 = match, 1 = drift, 2 = cannot determine. Treat 2 as a failure,
  not a pass.
