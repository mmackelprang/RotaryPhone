# RotaryPhone

RotaryPhone makes a vintage rotary telephone ring and work as a Google Voice phone. An ASP.NET Core
server registers with Google Voice over SIP, rings the rotary phone through a Grandstream HT801 analog
telephone adapter, and relays call audio between Google Voice and the handset. It runs on an Ubuntu box
that it shares with a separate service, Radio Console, which shows call state, voicemail and texts and
talks to RotaryPhone over REST and SignalR.

This is a single-household project. It works for its owner's setup; it is published as a reference, not
as a turnkey product.

## How it works

```
                 SIP over WebSocket + DTLS-SRTP audio
Google Voice  <------------------------------------->  RotaryPhoneController.Server  (Ubuntu, port 5004)
                                                          |        ^
                       SIP/RTP (G.711 u-law), UDP 5060    |        |  CDP on localhost:9224
                                                          v        |  (session cookies)
                                              Grandstream HT801    "Bridge" Google Chrome
                                                     |             (signed in to voice.google.com)
                                              FXS (RJ11)
                                                     |
                                              Rotary phone

Radio Console  <-- REST /api/* and SignalR /hub -->  RotaryPhoneController.Server
```

- **Server** (`src/RotaryPhoneController.Server`): ASP.NET Core host. Runs the call state machine,
  exposes the REST API and the SignalR hub, and serves the React diagnostics UI.
- **Google Voice** (`src/RotaryPhoneController.GVBridge`): the active call path (`GVApi` mode). Inbound
  and outbound calls use Google Voice's SIP-over-WebSocket endpoint; audio is Opus over DTLS-SRTP,
  resampled to G.711 for the HT801. The same project reads voicemail and SMS through Google Voice's web
  API for Radio Console.
- **Bridge Chrome**: a dedicated Google Chrome profile on the box holds one signed-in Google Voice
  session. The server reads that session's cookies over the Chrome DevTools Protocol (CDP, port 9224).
  The browser carries no call audio.
- **HT801**: rings the rotary phone when the server sends it a SIP INVITE, decodes pulse dialing, and
  carries handset audio as RTP. The server learns the HT801's address from its SIP REGISTER.
- **Radio Console**: a separate service on the same box. It reads call state, rings an on-screen banner,
  can decline a ringing call, and shows voicemail and texts.

The full picture is in [ARCHITECTURE.md](ARCHITECTURE.md).

## Status and limitations

Working today: incoming Google Voice calls ring the rotary phone and connect when the handset is lifted;
outgoing calls dialed on the rotary phone are placed through Google Voice; voicemail and SMS reads for
Radio Console; call history; bell-failure reporting; a Google Voice session alarm.

Known limitations:

- **Declining a call does not stop the linked cell phone ringing.** Radio Console's Ignore button
  (`POST /api/phone/decline`) stops the rotary bell and sends `603 Decline` on RotaryPhone's Google Voice
  leg, but Google Voice rings linked phones as separate legs and keeps ringing them until its own
  no-answer timeout.
- **Everything depends on a signed-in browser session.** SIP registration, SMS and voicemail all use the
  Google Voice session held by the bridge Chrome. If Google signs that browser out, the phone stops
  working until someone signs in again in the bridge Chrome on the box. The session alarm reports it. An automatic re-login is
  installed but disabled (see [ROADMAP.md](ROADMAP.md)).
- **Bluetooth HFP is not the active call path.** The code for bridging a mobile phone over Bluetooth HFP
  still exists, but on the deployed box no phone is connected over HFP and calls come from Google Voice.
- SMS sending and Google Voice mark-read are implemented but off by default (`EnableSmsSend`,
  `EnableMarkRead`).

Current defects and their history are tracked in [docs/KNOWN-ISSUES.md](docs/KNOWN-ISSUES.md).

## Requirements

- .NET 10 SDK. Projects target `net10.0`; on Windows they also build a
  `net10.0-windows10.0.19041.0` target.
- Node.js and npm, only to rebuild the React UI (`src/RotaryPhoneController.Client`).
- To run it for real: an Ubuntu 24.04 box, Google Chrome, a Grandstream HT801, a rotary phone and a
  Google Voice account. Setup is described in [docs/SETUP-GVBridge.md](docs/SETUP-GVBridge.md).

## Quick start

```bash
git clone https://github.com/mmackelprang/RotaryPhone.git
cd RotaryPhone

dotnet build RotaryPhoneController.sln
dotnet test RotaryPhoneController.sln
```

Run the server locally (the launch profile listens on `http://0.0.0.0:5555`; the box uses port 5004):

```bash
dotnet run --project src/RotaryPhoneController.Server
```

The built React UI is committed in `src/RotaryPhoneController.Server/wwwroot`. To rebuild it:

```bash
cd src/RotaryPhoneController.Client
npm install
npm run build        # writes to ../RotaryPhoneController.Server/wwwroot
npx vitest run       # UI unit tests
```

The deploy and box-side shell scripts have their own test harnesses in `deploy/tests/`.

## Deployment

The box is deployed from a Windows machine with PowerShell:

```powershell
.\deploy\Deploy-ToLinux.ps1 -TargetHost <box> -Runtime linux-x64
```

The script publishes the server for `linux-x64`, copies it to `/opt/rotary-phone` over ssh/scp, installs
and restarts the `rotary-phone` systemd service, installs the bridge-Chrome scripts and helpers
(`deploy/install-gv-bridge.sh`), the session alarm and the auto-relogin files, and runs
`deploy/check-installed-drift.sh` to confirm the installed copies match the repo. `-PreflightOnly`
checks ssh, scp and sudo without deploying. First-time box setup is in
[docs/SETUP-GVBridge.md](docs/SETUP-GVBridge.md).

## Configuration

Settings live in `src/RotaryPhoneController.Server/appsettings.json` (defaults) and
`appsettings.Production.json`. The deploy never overwrites the box's own `appsettings.Production.json`;
on the box, that file is the source of truth. Secrets such as the HT801 admin password exist only in the
box's copy and are not committed.

- `RotaryPhone`: SIP listen address and port, the phone list and HT801 address, Bluetooth switches.
  See [docs/HT801-ADDRESS.md](docs/HT801-ADDRESS.md) for how the HT801 address is set and verified.
- `GVBridge`: call adapter mode (`DefaultMode: "GVApi"`), CDP port, cookie storage, polling, and
  feature switches for SMS send and mark-read.

The HT801's own settings (SIP server, pulse dialing, PCMU) are covered in
[docs/SETUP-GVBridge.md](docs/SETUP-GVBridge.md).

## Radio Console integration

Radio Console uses the REST API (for example `GET /api/phone/status`, `POST /api/phone/decline`,
`GET /api/gvbridge/status`) and the SignalR hub at `/hub` (events such as `IncomingCall`,
`CallStateChanged`, `SmsReceived`, `VoicemailReceived`). The two services share the box's Bluetooth
adapters and audio stack; the rules for that, and the API contract, are in
[docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md](docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md).

## Documentation

- [docs/README.md](docs/README.md): index of the current documentation.
- [ARCHITECTURE.md](ARCHITECTURE.md): components and call flows.
- [ROADMAP.md](ROADMAP.md): what is done, known limitations and possible future work.
- [docs/archive/](docs/archive/README.md): earlier plans and designs (Raspberry Pi, Windows NUC,
  Bluetooth HFP), kept as history.

## License

Apache License 2.0. See [LICENSE](LICENSE).
