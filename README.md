<p align="center">
  <img src="src/LEO.App/Assets/leo-256.png" width="96" alt="LEO logo: a gold-to-orange circle with a dark curved band">
</p>

<h1 align="center">LEO</h1>

<p align="center">
  <b>Use every network your Windows PC can reach, at the same time.</b><br>
  Multiple Wi-Fi, Ethernet and phone-tethering connections, a one-click Primary network,<br>
  automatic failover, per-app and per-website split tunneling, and bonding.
</p>

<p align="center">
  <a href="https://github.com/unterstellen/leo/actions/workflows/build.yml"><img src="https://github.com/unterstellen/leo/actions/workflows/build.yml/badge.svg?branch=canary" alt="Build"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0--or--later-blue" alt="License: GPL-3.0-or-later"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2011%20x64-0078D4" alt="Windows 11 x64">
</p>

> [!NOTE]
> **LEO is in Preview until version 1.0.** It is tested on a handful of PCs so far; please report how it does on yours (see [Help test LEO](#help-test-leo)).

<!-- Screenshots: Networks, Apps, URLs & IPs, Bonding, Stats, tray (to be added with the first release). -->

## What LEO does

Your PC may have several ways onto the internet (built-in Wi-Fi, a USB Wi-Fi stick, Ethernet, your phone over USB), but Windows uses one at a time and leaves the rest idle. LEO lets you use them deliberately:

| | Feature |
|---|---|
| 🌐 | **See every network**: Wi-Fi, Ethernet, phone USB tethering, Bluetooth, VPNs, with live health and latency |
| ⭐ | **One-click Primary network** and a priority order, without unplugging or disabling anything |
| 🛟 | **Automatic failover**: LEO checks each network's *real* internet every few seconds and moves traffic when one fails, then switches back |
| 📶 | **Wi-Fi control**: keep an adapter on chosen networks, join networks, dual-station cards |
| 🧩 | **Split tunneling by app**: choose which network each app or game uses |
| 🔗 | **Split tunneling by website, domain or IP**, with per-network DNS |
| 🗂️ | **Profiles** (Home / Office / Phone) that switch by themselves, and **statistics** per network and app |
| 🔐 | **VPN-aware**: choose which network a VPN uses, send apps into or around it |
| ⚡ | **Bonding**: spread connections across several networks |
| 🔔 | **Tray menu and notifications**, nicknames for adapters and networks |

The full design is in [LEO.md](LEO.md).

## Install

1. Download `LEO-<version>-x64.msi` from [Releases](https://github.com/unterstellen/leo/releases).
2. Run it and confirm the one administrator prompt.

LEO installs a background service and, while split tunneling is on, a virtual network adapter named **LEO**. Nothing else needs to be installed first (.NET and the Windows App SDK are included). Requirements: **Windows 11, x64**, administrator rights to install.

New releases are code-signed, but Windows may still show *"Windows protected your PC"* (SmartScreen) for a new version until enough people have downloaded it. Choose *More info → Run anyway* if the publisher shown is LEO's.

Installing on a company PC, silent installs: [docs/managed-pcs.md](docs/managed-pcs.md).

## Update

LEO updates itself: once a day the service checks this repository's releases and installs a new signed version on its own. Split tunneling pauses for a few seconds while it does, and a notification tells you afterwards. Settings → Updates lets you switch automatic installing off (you then get *Update now*) or stop checking. Only administrators can install updates.

## Uninstall

Settings → Apps → Installed apps → LEO → Uninstall (or Settings → About → *Uninstall LEO* in LEO).

Uninstalling stops the service and puts Windows networking back the way it was: interface metrics, DNS, LEO's firewall rules, dual-link Wi-Fi. It deletes **all** of LEO's settings, rules, statistics and logs, for every user of the PC. Your saved Wi-Fi networks stay (they belong to Windows).

## If something goes wrong

LEO never takes your network hostage: **stop the service** and everything LEO changed is handed back to Windows at once.

```powershell
Stop-Service LEO
```

(or Services → *LEO Network Engine* → Stop). Start it again with `Start-Service LEO`.

## Limits

- **One connection uses one network.** Bonding spreads *connections* across networks, so many downloads at once get faster; a single download or video call doesn't ([LEO.md](LEO.md) §22.6).
- **Website rules need a name to match.** Apps that hide the site name (Encrypted Client Hello) are matched by address only ("domain unknown"), §5.4.
- **IPv6 traffic isn't steered by app and website rules** yet; it follows the Primary network (§20.7).
- **VPNs with a kill switch** that only allow their own tunnel make LEO step aside for split tunneling while they're connected (§20.4).
- **Secondary Wi-Fi stations** (dual-station cards) work only on drivers that support them; LEO hides what a card can't do.
- **Windows 11 x64 only** for now. ARM64 comes later.

## Privacy

**No telemetry. No accounts. No LEO servers.** LEO's own network traffic is the daily update check against github.com, the internet checks it runs through each network, and health checks for bonds. Statistics are daily totals; which sites you visit is never stored. Details: [PRIVACY.md](PRIVACY.md).

## Help test LEO

Settings → About → *Copy diagnostics* puts a report on your clipboard (network names and addresses left out unless you tick the box). Paste it into a [hardware report](https://github.com/unterstellen/leo/issues/new/choose), especially for Intel, Realtek or MediaTek Wi-Fi, USB Ethernet dongles, iPhone tethering, docks and VPN clients. Security problems: [SECURITY.md](SECURITY.md).

## Build from source

Requirements: [.NET SDK 10.0.404+](https://dotnet.microsoft.com/download) (pinned in `global.json`). Visual Studio is not needed. Working in an IDE (JetBrains Rider, ready-made run configurations in `.run/`): [docs/developing.md](docs/developing.md).

```powershell
git clone https://github.com/unterstellen/leo.git
cd leo
dotnet build LEO.slnx
dotnet test  LEO.slnx
```

Run it (development setup):

```powershell
.\tools\dev-service.ps1 deploy      # builds + installs the dev service to Program Files (one admin prompt)
src\LEO.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\LEO.exe
.\tools\dev-service.ps1 uninstall   # stops LEO (networking handed back to Windows) and removes the service
```

Local unsigned test MSI: `.\tools\build-release.ps1` (writes `artifacts\release\LEO-<version>-x64.msi`).

> Unsigned local builds are blocked by **Smart App Control** if it's on. Development machines need it turned off; signed releases don't have this problem.

## Project layout

```
src/LEO.Core      shared models, rule/priority logic, config, update and cleanup rules
src/LEO.Ipc       JSON-RPC contract + named-pipe transport
src/LEO.Service   the Windows service (networks, health, failover, Wi-Fi, engine, updater)
src/LEO.App       WinUI 3 app + tray
installer/        WiX MSI
tests/LEO.Tests   unit, in-process IPC/host, and opt-in live tests
tools/            dev and release scripts
```

## Contributing

Issues, hardware reports and pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

LEO is free software: you can redistribute it and/or modify it under the terms of the **GNU General Public License v3.0 or (at your option) any later version**. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

LEO comes with **absolutely no warranty**.

Published by **UNTERSTELLEN** ([unterstellen.org](https://unterstellen.org)) · **INFERSTELLEN** ([inferstellen.com](https://inferstellen.com)).
