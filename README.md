<p align="center">
  <img src="src/LEO.App/Assets/leo-256.png" width="96" alt="LEO logo: a gold-to-orange circle with a dark curved band">
</p>

<h1 align="center">LEO</h1>

<p align="center">
  <b>Use every network your Windows PC can reach — at the same time.</b><br>
  Multiple Wi-Fi, Ethernet and phone-tethering connections, a one-click Primary network,<br>
  automatic failover, and (soon) per-app and per-website split tunneling.
</p>

<p align="center">
  <a href="https://github.com/unterstellen/leo/actions/workflows/build.yml"><img src="https://github.com/unterstellen/leo/actions/workflows/build.yml/badge.svg?branch=canary" alt="Build"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0--or--later-blue" alt="License: GPL-3.0-or-later"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2011%20x64-0078D4" alt="Windows 11 x64">
</p>

> [!WARNING]
> **LEO is in active development and not ready for everyday use yet.** There is no installer or signed release so far; follow [Build from source](#build-from-source) if you want to try it.

## What LEO does

Your PC may have several ways onto the internet — built-in Wi-Fi, a USB Wi-Fi stick, Ethernet, your phone over USB — but Windows uses one at a time and leaves the rest idle. LEO lets you use them deliberately:

| | Feature | Status |
|---|---|---|
| 🌐 | **See every network** — Wi-Fi, Ethernet, phone USB tethering, Bluetooth, VPNs — with live health and latency | ✅ Done |
| ⭐ | **One-click Primary network** and a priority order, without unplugging or disabling anything | ✅ Done |
| 🏷️ | **Nicknames** for adapters and networks ("Home Fiber", "Phone") | ✅ Done |
| 🛟 | **Automatic failover** — LEO checks each network's *real* internet every few seconds and moves traffic when one fails, then switches back | ✅ Done |
| 📶 | **Wi-Fi control** — keep an adapter on chosen networks, join networks, dual-station cards | ✅ Done |
| 🔔 | **Tray menu & notifications** | ✅ Done |
| 🧩 | **Split tunneling by app** — choose which network each app or game uses | 🚧 Next (M3) |
| 🔗 | **Split tunneling by website/IP** with per-network DNS | Planned |
| ⚡ | **Bandwidth bonding** — spread downloads across several networks for more speed | Planned |
| 🗂️ | **Profiles** (Home / Office / Gaming) and live per-app stats | Planned |
| 🔐 | **VPN-aware** — choose which network a VPN uses, or bypass it for chosen apps | Planned |

The full design and roadmap are in [LEO.md](LEO.md).

## Privacy

**No telemetry. No accounts. No LEO servers.** LEO's only network traffic of its own is:
- the internet checks it runs through each network (Microsoft's connectivity test address, with Cloudflare as a fallback — both changeable in Settings), and
- (planned) an update check against this repository's GitHub Releases.

Wi-Fi passwords you enter are handed straight to Windows, which stores them encrypted; LEO never stores or logs them.

## Requirements

- Windows 11, x64
- Administrator rights to install (LEO runs a background service so routing keeps working without the app open)

## How it works (short version)

- **LEO.Service** runs as a Windows service (LocalSystem). It watches adapters, checks each network's internet *through that adapter*, sets Windows' interface metrics to enforce your priority, and handles Wi-Fi.
- **LEO** (the app) is a non-elevated WinUI 3 window + tray icon that talks to the service over a local, access-controlled named pipe. Standard (non-admin) users can view but not change anything.
- If the service stops, every change it made is handed back to Windows.

## Build from source

Requirements: [.NET SDK 10.0.404+](https://dotnet.microsoft.com/download) (pinned in `global.json`). Visual Studio is not needed.

Working on LEO in an IDE (JetBrains Rider, with ready-made run configurations in `.run/`): see [docs/developing.md](docs/developing.md).

```powershell
git clone https://github.com/unterstellen/leo.git
cd leo
dotnet build LEO.slnx
dotnet test  tests\LEO.Tests
```

Run it (development setup):

```powershell
.\tools\dev-service.ps1 deploy      # builds + installs the service to Program Files (one admin prompt)
src\LEO.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\LEO.exe
.\tools\dev-service.ps1 uninstall   # stops LEO (networking handed back to Windows) and removes the service
```

> Unsigned local builds are blocked by **Smart App Control** if it's on. Development machines need it turned off; signed releases won't have this problem.

## Project layout

```
src/LEO.Core      shared models, rule/priority logic, config
src/LEO.Ipc       JSON-RPC contract + named-pipe transport
src/LEO.Service   the Windows service (networks, health, failover, Wi-Fi)
src/LEO.App       WinUI 3 app + tray
tests/LEO.Tests   unit, in-process IPC/host, and opt-in live tests
tools/            dev scripts (service deploy, icon generation)
```

## Contributing

Issues and ideas are welcome. Pull requests are welcome too — please open an issue first for anything large. By contributing you agree your contribution is licensed under the project's license.

## License

LEO is free software: you can redistribute it and/or modify it under the terms of the **GNU General Public License v3.0 or (at your option) any later version**. See [LICENSE](LICENSE).

LEO comes with **absolutely no warranty**.

Published by **UNTERSTELLEN** ([unterstellen.org](https://unterstellen.org)) · **INFERSTELLEN** ([inferstellen.com](https://inferstellen.com)).
