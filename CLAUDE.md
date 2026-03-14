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
