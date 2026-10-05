# Contributing to RotaryPhone

RotaryPhone is a single-maintainer project that runs on one Ubuntu box (`radio`) alongside the
Radio Console service. Contributions are welcome; this guide covers how changes are made, built,
tested and deployed.

## Branches and pull requests

- Do not commit to `main` directly. Create a short-lived branch per change, named for its kind and
  topic, for example `feat/<topic>`, `fix/<topic>` or `docs/<topic>`.
- Open a pull request against `main`. One reviewable change per pull request.
- Build and test locally before opening the pull request, and say in its description what you ran
  and on which platform.

## Commit messages

Use [Conventional Commits](https://www.conventionalcommits.org/), as in the existing history:

```
<type>(<optional scope>): <summary>
```

Types in use include `feat`, `fix`, `docs`, `chore` and `revert`. Scopes in use include `gv`,
`gvbridge`, `gv-bridge`, `sip`, `core`, `deploy`, `alarm`, `api` and `boundary`. Examples:

```
fix(gv): decline a ringing inbound call with 603 Decline, not 480
feat(deploy): install the GV bridge launch scripts into ~/bin on every deploy
```

## Building

The solution is `RotaryPhoneController.sln`. It requires the .NET 10 SDK.

Projects target `net10.0`. On Windows, most also build a `net10.0-windows10.0.19041.0` variant.
`RotaryPhoneController.Core` lists the Windows target unconditionally, so on Linux or macOS pass
`-p:EnableWindowsTargeting=true` and select the cross-platform framework with `-f net10.0`:

```bash
# Windows
dotnet build RotaryPhoneController.sln
dotnet test RotaryPhoneController.sln

# Linux / macOS
dotnet build RotaryPhoneController.sln -p:EnableWindowsTargeting=true -f net10.0
dotnet test RotaryPhoneController.sln -p:EnableWindowsTargeting=true -f net10.0
```

Test projects: `RotaryPhoneController.Tests`, `RotaryPhoneController.Server.Tests`,
`RotaryPhoneController.GVBridge.Tests` and `RotaryPhoneController.GVTrunk.Tests`.

`src/BluetoothPoC` is a Windows-only proof of concept and is not part of the solution.

The web client in `src/RotaryPhoneController.Client` is a Vite and React app:

```bash
cd src/RotaryPhoneController.Client
npm ci
npm run lint
npm run build
```

## Deploy tooling checks

The shell scripts under `deploy/` have their own harnesses in `deploy/tests/`. They run locally,
need no box, and never contact Google. Run the ones that cover the scripts you changed, for example:

```bash
bash deploy/tests/check-bridge-chrome-flags.sh
bash deploy/tests/check-alarm-copy-drift.sh
bash deploy/tests/repro-installed-drift.sh
bash deploy/tests/repro-tar-clobber.sh
```

Each script's header states what it covers and any prerequisites (some need Python or a local
Chrome).

## Deploying

Deploys go from a Windows machine to the box with `deploy/Deploy-ToLinux.ps1`:

```powershell
.\deploy\Deploy-ToLinux.ps1 -PreflightOnly   # check interpreter, SSH transport and sudo; change nothing
.\deploy\Deploy-ToLinux.ps1                  # build, sync, restart
.\deploy\Deploy-ToLinux.ps1 -Logs            # same, then tail the service journal
```

- **Never commit secrets.** Passwords, API keys, cookie encryption keys and the inter-service auth
  key stay empty in the tracked `appsettings*.json` files.
- **The box keeps its own `appsettings.Production.json`.** The deploy excludes that file from the
  sync, so the box's configured copy is never overwritten. Change production settings on the box.
- Setup and operations for the Google Voice bridge browser are in
  [docs/SETUP-GVBridge.md](docs/SETUP-GVBridge.md).

## The Radio Console boundary

RotaryPhone shares the `radio` box with Radio Console. Before any change to Bluetooth, audio,
WirePlumber configuration, the GV bridge, or the REST and SignalR surface Radio Console consumes,
read [docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md](docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md).

- It defines which Bluetooth adapter, profiles and WirePlumber configs each service owns. Breaking
  it breaks the other service's audio.
- A change to a contract Radio Console relies on is announced in that document's Change Log first,
  before the change ships.
- Exchanges with Radio Console live in `docs/prompts/` (inbound) and `docs/handoffs/` (outbound).
  See [docs/README.md](docs/README.md).

## Documentation

- Current documentation is indexed in [docs/README.md](docs/README.md). Update the relevant
  document in the same pull request as the change.
- Decisions that span pull requests or services are recorded as ADRs in
  `docs/architecture/decisions/`, indexed in [docs/architecture/README.md](docs/architecture/README.md).
- User-visible changes go in [CHANGELOG.md](CHANGELOG.md) under `[Unreleased]`.
- `docs/archive/` is history. It is kept as written and is not maintained; do not update it to
  match current behaviour.

## License

By contributing, you agree that your contributions are licensed under the terms in [LICENSE](LICENSE).
