# RotaryPhone documentation

An index of the current documentation, grouped by purpose. Start with the
[project README](../README.md) for an overview.

## Setup and operations

| Document | What it covers |
|---|---|
| [SETUP-GVBridge.md](SETUP-GVBridge.md) | Setting up and operating the Google Voice bridge browser on the box |
| [HT801-ADDRESS.md](HT801-ADDRESS.md) | Where the HT801 address is configured, how to change it, and how to verify it |
| [gv-relogin-driver-contract.md](gv-relogin-driver-contract.md) | The contract the Google Voice sign-in driver must meet for automatic re-login |
| [KNOWN-ISSUES.md](KNOWN-ISSUES.md) | Open and resolved issues, with workarounds |
| [../CONTRIBUTING.md](../CONTRIBUTING.md) | Branch and pull request workflow, build, test and deploy |

## Architecture and decisions

| Document | What it covers |
|---|---|
| [../ARCHITECTURE.md](../ARCHITECTURE.md) | System architecture |
| [architecture/README.md](architecture/README.md) | Index of architecture decision records (ADRs) in `architecture/decisions/` |
| [research/gv-protocol-notes.md](research/gv-protocol-notes.md) | Google Voice SIP-over-WebSocket and authentication reference |
| [api-research/signaler-protocol.md](api-research/signaler-protocol.md) | Google Voice signaler protocol notes |
| [api-research/signaler-subscriptions-todo.md](api-research/signaler-subscriptions-todo.md) | Open research notes on signaler subscriptions |

## Radio Console integration

RotaryPhone shares its Ubuntu box with the Radio Console service.

| Document | What it covers |
|---|---|
| [prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md](prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md) | The boundary contract: which Bluetooth adapter, profiles and WirePlumber configs each service owns, the shared REST and SignalR surface, and a Change Log of cross-service changes. Read it before any Bluetooth, audio or bridge change |

Exchanges between the two services are kept as files in two active folders:

- [`prompts/`](prompts/) is RotaryPhone's inbound lane: requests from Radio Console that are still
  open or in progress, alongside the boundary contract.
- [`handoffs/`](handoffs/) is RotaryPhone's outbound record: replies and contracts RotaryPhone has
  sent to Radio Console that Radio Console still relies on.

Closed exchanges are moved to [`archive/radio-console/`](archive/README.md#radio-console).

## Plans in progress

| Document | What it covers |
|---|---|
| [plans/gv-reachable-reauth.md](plans/gv-reachable-reauth.md) | Planned human fallback for Google Voice re-authentication when automatic re-login cannot recover |
| [superpowers/specs/2026-09-25-gv-reachable-reauth-design.md](superpowers/specs/2026-09-25-gv-reachable-reauth-design.md) | The design behind that plan |
| [plans/build-stamp-and-deploy-verification.md](plans/build-stamp-and-deploy-verification.md) | Planned build stamp and deploy verification (not yet built) |

## Project history

| Document | What it covers |
|---|---|
| [../CHANGELOG.md](../CHANGELOG.md) | Release notes |
| [../ROADMAP.md](../ROADMAP.md) | What is planned next |
| [archive/README.md](archive/README.md) | Index of archived development documents: earlier plans, designs and closed exchanges. Kept as written and not maintained |
