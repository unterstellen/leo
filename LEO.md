# LEO — Multi-Network Manager & Split Tunneling for Windows

| | |
|---|---|
| **App name** | LEO |
| **Publisher (brand)** | UNTERSTELLEN — unterstellen.org |
| **Company** | INFERSTELLEN — inferstellen.com |
| **Code signing** | **Not decided yet** (choose at M8; SignPath ruled out 2026-11-22). Options in §16.1 |
| **Repository** | https://github.com/unterstellen/leo-windows (public) |
| **Releases / updates** | https://github.com/unterstellen/leo-windows/releases |
| **Platform** | Windows 11, x64 only |
| **Language** | English only (permanently) |
| **Price** | **Free forever and open source.** No accounts, no telemetry, no hosted services |
| **License** | **GPL-3.0-or-later** (`LICENSE` in the repo: official GNU text, SHA-256 `3972dc97…dfb36986`) |
| **Status** | Specification (v0.1, 2026-11-20) |

---

## 1. Purpose

LEO lets one PC stay connected to **several networks at once**: multiple Wi-Fi adapters, multiple Ethernet ports, USB phone tethering (RNDIS/NCM) and VPN adapters. With LEO you can:

1. **Pick the Primary Network** with one click. You never unplug or disable an adapter.
2. **Give nicknames** to adapters and networks ("Home Fiber", "Phone (USB)", "Office Wi-Fi").
3. **Split-tunnel** traffic:
   - **Apps/Software**: this app uses network X.
   - **URLs/IPs/addresses**: these domains or IPs use network Y.
   - Everything else uses the Primary Network.
4. **Fail over automatically** when a network loses internet, and switch back when it recovers.

---

## 2. Decisions Made

| Topic | Decision |
|---|---|
| Architecture | **Windows Service (LocalSystem)** plus a separate **non-elevated tray/desktop UI**, connected by a secured named pipe |
| Split-tunnel tech | **Wintun TUN adapter** plus a user-mode packet engine (no custom kernel driver) |
| Stack | **C# / .NET 8+**, **WinUI 3** UI. Packet hot path in a native **Rust** core (see §5.3) |
| Code signing | **Not decided yet**: choose at M8 from §16.1 (SignPath Foundation ruled out 2026-11-22). Release builds are signed in CI, never on a dev PC. Every binary and the installer get signed |
| Branding | No custom brand colors: follow Windows 11 (system accent, light/dark). **Logo**: a yellow circle face wearing dark glasses, with no eyes, nose or mouth (`assets/leo.svg`) |
| Language | English only, permanently. No localization layer |
| Failover | **Auto-failover by priority**, based on real internet health checks. Switches back when the network recovers |
| Pinned network down | **Per-rule choice**: `Block` (kill switch) or `Fall back to Primary` |
| Extras | Live per-network stats, Profiles with auto-switch, Per-network DNS, **Bandwidth bonding** |
| VPN | LEO is the **underlay** (the VPN rides on top of the network LEO picks) **and** can **bypass** the VPN for chosen apps/URLs |
| Distribution | Signed **MSI installer** published on **GitHub Releases**, with auto-update |
| UI style | **Windows 11 Fluent** (Mica, system theme and accent) |
| Min OS / CPU | **Windows 11 only, x64** (re-confirmed 2026-11-20). ARM64 later, after v1 |
| Control surfaces | Main window, **tray quick menu**, **toast notifications** |
| App rule picking | Browse .exe, pick a running process, installed apps (incl. Store/UWP), folder/wildcard |
| URL rules | Domains + subdomains, IP / CIDR / ports / protocol, importable rule lists |
| Rule precedence | **Most specific wins**: App+URL > URL > App > Primary. Manual reordering allowed |
| Auto-start toggle | Controls **UI only**. The engine always runs at boot. A separate "Disable LEO engine" option stops it |
| Wi-Fi management | **Yes**: LEO pins each Wi-Fi adapter to an SSID and keeps it connected |
| Data | **Local only**, no telemetry. Config is JSON in ProgramData, with import/export |

---

## 3. Core Concepts

- **Adapter**: a physical or virtual NIC (Wi-Fi card, Ethernet port, USB tether, VPN adapter). Identified by interface GUID and MAC, so it stays the same across reboots and USB re-plugs.
- **Network**: what an adapter is connected to. Identified by the NLA network profile, plus SSID/BSSID (Wi-Fi) or gateway MAC (wired/tether).
- **Nickname**: a user label on an Adapter and/or a Network. When both exist, the UI shows `Network nickname · Adapter nickname`.
- **Route Target**: where a rule sends traffic. One of:
  - `Primary`: whatever is primary right now.
  - `Adapter:<id>`: a specific adapter, whatever it's connected to.
  - `Network:<id>`: a specific network, on any adapter.
  - `Group:<id>`: a bonding group of several targets (§6.8).
  - `Block`.
- **Priority list**: the ordered failover chain for Primary.
- **Profile**: a named set of {priority list, rules, DNS settings, Wi-Fi pins}. It can be turned on by hand or automatically by a trigger (§6.6).
- **Rule**: a match (app and/or destination) → Route Target, plus an `onTargetDown` choice (`Block` | `FallbackPrimary`).

---

## 4. Architecture

```
┌───────────────────────────── User session (non-elevated) ─────────────────────────────┐
│  LEO.App (WinUI 3)                                                                    │
│   • Main window (Fluent)  • Tray icon + quick menu  • Toasts                          │
│   • Starts at login via HKCU Run (if enabled)                                         │
└──────────────────────────────────────┬────────────────────────────────────────────────┘
                                       │ Named pipe \\.\pipe\LEO.Control (ACL'd, JSON-RPC)
┌──────────────────────────────────────┴──────────── Session 0, LocalSystem ────────────┐
│  LEO.Service (.NET Worker Service, auto-start, failure recovery = restart)            │
│   ├─ NetworkInventory   – adapters, networks, NLA, change notifications               │
│   ├─ WifiManager        – Native Wifi API, per-adapter SSID pinning                   │
│   ├─ HealthMonitor      – per-interface internet probes (latency, loss, captive)      │
│   ├─ PrimaryController  – interface metrics, default routes, failover state machine   │
│   ├─ RuleEngine         – compiles rules/profiles → engine routing table              │
│   ├─ DnsManager         – per-network DNS / DoH resolvers, fake-IP mapping            │
│   ├─ StatsCollector     – per-adapter + per-app counters                              │
│   ├─ ProfileManager     – triggers, activation                                        │
│   ├─ Updater            – signed update check & install                               │
│   └─ leo-engine (Rust, native DLL via FFI)                                            │
│        • Wintun adapter "LEO"   • user-mode TCP/IP stack   • flow table               │
│        • process lookup         • SNI/Host sniffing         • per-interface dialers   │
└───────────────────────────────────────────────────────────────────────────────────────┘
       │ bound sockets (IP_UNICAST_IF / IPV6_UNICAST_IF + source IP)
       ▼
  Wi-Fi 1 · Wi-Fi 2 · Ethernet 1 · Ethernet 2 · USB Tether · VPN adapter(s)
```

### 4.1 Why a service as LocalSystem
- Starts at **boot, before login**. Routing works on the lock screen and for every user.
- Has more rights than an admin user, with **no UAC prompt** ever.
- The UI can crash or restart without touching networking.
- Service recovery: restart on the 1st, 2nd and later failures, with backoff.

### 4.2 IPC security
- Pipe ACL: `SYSTEM`, `Administrators` = full control. Interactive `Users` = **read-only** by default. A setting lets standard users change rules.
- The service checks the client's PID → image path → Authenticode signature (must carry the same signature as the service itself, i.e. the release certificate; see §9) before it accepts commands.
- Protocol: JSON-RPC 2.0 over the pipe. Events (adapter up/down, failover, stats ticks) are pushed to subscribed UIs.

---

## 5. Technical Approach

### 5.1 Primary network (works even with the engine off)
- Turn off "Automatic metric" on managed interfaces and set `InterfaceMetric` with `SetIpInterfaceEntry` (IPv4 and IPv6): Primary = lowest metric, then the priority order.
- Every adapter keeps its own default route, so bound sockets on non-primary adapters still work.
- When the TUN engine is active, the engine's default outbound is the Primary, and metrics are still set so traffic that bypasses the TUN behaves the same.
- Switching takes milliseconds. Existing TCP connections on the old primary keep running until they close. Optionally, "reset connections on switch" can be turned on.

### 5.2 Split tunneling: TUN engine
1. LEO creates a **Wintun** adapter named `LEO` and adds `0.0.0.0/1`, `128.0.0.0/1`, `::/1` and `8000::/1` routes, so all traffic enters LEO.
2. A **user-mode TCP/IP stack** ends each flow from the TUN side.
3. For each new flow:
   - **Process lookup**: local port → PID → image path / package family (`GetExtendedTcpTable` / `GetExtendedUdpTable`, cached; ETW as a later option).
   - **Destination lookup**: IP → domain from the fake-IP DNS table (§5.4), or **TLS SNI / HTTP Host sniffing** for apps that use hard-coded IPs or their own DoH.
   - **RuleEngine** matches the flow and gives back a Route Target.
4. The engine opens the real outbound socket **bound to the chosen interface** (`IP_UNICAST_IF` + bind to that interface's source IP) and copies data both ways.
5. **Loop prevention**: the engine's own sockets are always bound to physical interfaces and never go back through the TUN. LEO.Service's own traffic (updates, probes) is excluded.
6. **UDP** (games, QUIC, VoIP): per-flow NAT sessions with idle timeouts. QUIC is matched by SNI from the Initial packet where possible.
7. **ICMP**: ping/traceroute go to the Primary or follow IP rules.
8. **IPv6**: handled the same as IPv4. If a rule's target network has no IPv6, that rule's IPv6 traffic is **blocked**, not leaked.

### 5.3 Engine implementation
- A **Rust** native library (`leo-engine.dll`) handles the packet hot path: Wintun I/O, the TCP/IP stack (e.g. `smoltcp`-based), the flow table, sniffing and dialers. C# handles policy, config, IPC and UI.
- The C# service sends the compiled rule table to the engine. The engine sends flow events and counters back.
- **Licensing**: LEO is **open source and free forever, licensed GPL-3.0-or-later** (decided 2026-11-20). Dependencies must be GPL-3.0-compatible: GPL-3.0/LGPL, MIT, Apache-2.0, BSD are fine (so GPL engines such as sing-box *could* be reused); GPL-2.0-only code and proprietary libraries are not. Wintun ships as Microsoft-signed `wintun.dll` under its redistribution license.

### 5.4 DNS
- LEO runs a DNS resolver on the TUN interface. Windows' DNS points at it.
- *(As built in M4: **redir-host** instead of fake-IP, so cached answers survive an engine stop; see §18.)*
- **Fake-IP mode** (original plan): domains get answers from `198.18.0.0/15` (and an IPv6 ULA range). LEO keeps the fake IP → domain map, so domain rules match exactly. The real lookup happens **through the target network** using that network's DNS.
- **Per-network DNS**: each network can use its DHCP-provided DNS, custom DNS servers, or DoH/DoT.
- Leak protection: plain DNS (port 53) to other servers is caught and answered by LEO.
- Limits: browsers with Secure DNS / ECH may hide the domain. SNI sniffing covers most cases, and ECH-only connections fall back to IP rules or the app rule. Shown in the UI as "domain unknown".

### 5.5 Wi-Fi management
- Native Wifi API (`WlanOpenHandle`, `WlanScan`, `WlanConnect` per interface GUID, `WlanSetProfile`).
- Each Wi-Fi adapter can be **pinned** to a preferred SSID list. LEO reconnects it when it drops and stops Windows from moving it to another SSID.
- Saved Wi-Fi keys are stored as Windows WLAN profiles (Windows encrypts them). LEO never stores Wi-Fi passwords itself.

### 5.6 USB tethering
- Phones show up as RNDIS (Android), NCM or Apple Mobile Device Ethernet adapters. LEO detects them by driver/hardware ID, names them "Phone (USB)" by default, and remembers them by MAC across re-plugs.
- Optional "metered" flag: rules can avoid metered networks, and stats show data used per session/day/month with a cap warning.

### 5.7 Health monitoring & failover
- A probe runs on **each interface** every N seconds (default 5): a small HTTP check, a DNS lookup, and TCP connect latency. Detects captive portals.
- **Default probe targets** (changeable in Settings):
  1. Microsoft NCSI, the same check Windows uses for its "No internet" icon: `http://www.msftconnecttest.com/connecttest.txt`, expecting the body `Microsoft Connect Test`.
  2. Fallback: Cloudflare `http://cp.cloudflare.com/generate_204`, expecting HTTP 204.
  - A network is `Up` if either probe succeeds. A redirect or unexpected content means `Captive`. No LEO-hosted endpoint is needed.
- States: `Up` / `Degraded` (high loss or latency) / `Captive` / `NoInternet` / `Disconnected`.
- Failover state machine: if Primary is not `Up` for X seconds (default 6), promote the next healthy network in priority order. After the preferred network has been `Up` for Y seconds (default 30), **fail back**, with hysteresis to stop flapping.
- A toast is shown on every failover and failback.

### 5.8 VPN coexistence
- **Underlay**: VPN client apps (e.g. `wireguard.exe`, `openvpn.exe`, the NordVPN service) are ordinary processes, so an app rule decides which physical network carries the VPN tunnel. Built-in presets cover common VPN clients.
- **VPN as a network**: a connected VPN adapter appears in LEO as a Route Target. "Route via VPN" works for chosen apps/URLs.
- **Bypass VPN**: when a VPN installs its own catch-all routes, LEO watches route changes (`NotifyRouteChange2`) and keeps the TUN as the catch-all path. The VPN adapter then becomes one of LEO's outbounds, so traffic not routed to the VPN can skip it.
- **Known limit**: VPNs with WFP-based kill switches (many commercial VPNs, corporate always-on VPNs) may block bypassed traffic. LEO detects this and warns.

### 5.9 Safety: never brick the network
- If the engine crashes or the service stops, LEO **removes its routes, restores DNS, and puts Windows networking back as it was**. Rules marked `Block` are the only fail-closed exception, and the user can configure this.
- **Safe mode**: holding Shift while LEO starts, or `services.msc` → stop LEO, puts plain Windows networking back.
- On uninstall, the Wintun adapter, metric overrides, DNS settings and the firewall/route state are fully reverted.

---

## 6. Features

### 6.1 Network dashboard
- Cards for every adapter/network: nickname, type icon, SSID/gateway, IP, link speed, health state, latency, live up/down throughput, data used, and a **Primary** badge.
- Actions per card: Make Primary, Rename, Move in priority order, Mark metered, Pin SSID, Exclude from LEO.

### 6.2 Primary switching
- From the main window or the tray menu with one click. Drag to reorder the failover priority list.

### 6.3 Nicknames
- Rename any adapter or network. Nicknames are kept across reboots and re-plugs, and are used everywhere: UI, tray, toasts and rules.

### 6.4 Split tunneling: Apps
- Add an app by: **browse .exe**, **running processes** (live list of processes using the network), **installed apps** (Win32 + Store/UWP by package family), or **folder/wildcard** (`C:\Games\**`, `*chrome*.exe`).
- Each rule has: Route Target, `onTargetDown` (Block / Fall back to Primary), an option to include child processes, and an enable toggle.
- **Presets** for common apps: browsers, Steam, Discord, Teams, Zoom, VPN clients, torrent clients.

### 6.5 Split tunneling: URLs / IPs
- **Domain** (`youtube.com` also matches all subdomains; exact-match option).
- **IP / CIDR** (IPv4 + IPv6), with optional **port / port range** and **TCP/UDP** filter.
- **App + destination combos** (e.g. Chrome **and** youtube.com → Ethernet).
- **Rule list import** from a file or URL (plain domain/CIDR lists). Lists from a URL refresh on a schedule.
- **Precedence**: App+Dest > Dest > App > Primary. Within the same specificity, the user's order wins (drag to reorder).
- **"Test a rule"** box: type an app and/or a URL and see which network it would use, and why.

### 6.6 Profiles & auto-switch
- Profiles such as Home / Office / Gaming / Travel, each with its own priority list, rules, DNS and Wi-Fi pins.
- Triggers: a specific SSID is visible or connected, an adapter is plugged in (e.g. the phone tether), or manual from the tray.

### 6.7 Live stats
- Per adapter: throughput graph, latency/loss history, totals (session/day/month).
- Per app: which network each app is using right now, plus bytes used per app per network.
- A live **connections view** (app → destination/domain → network → rule that matched).

### 6.8 Bandwidth bonding
- **Bond group** = several Route Targets with a balancing mode: weighted, round-robin, least-latency, or least-loaded.
- Works **per connection**: new flows are spread across members. Many-connection downloads (browsers, Steam, torrents, download managers) get roughly the combined bandwidth. A **single TCP stream cannot go faster than one link** without an aggregation point on the internet side; that's only a future idea (§15). M7 bonding itself needs **no server of any kind**.
- Sticky sessions: flows to the same destination stay on the same member for a set time, so logins and sessions don't break.
- Members that go down are removed from the group, and their flows are re-dialed where possible.

### 6.9 Tray
- Icon shows the current Primary and overall health (badge on failover or when offline).
- **Quick menu**: list of networks (nickname + state) → click to make Primary; Profile switcher; Pause split tunneling; Open LEO; Quit UI (the engine keeps running).

### 6.10 Notifications (toasts)
- Failover/failback, network up/down, new adapter detected ("Name it?"), traffic blocked by a rule (rate-limited), metered cap warning, update available.

### 6.11 Settings
- Start LEO UI at login (on by default).
- Disable LEO engine: stops all routing and restores Windows networking.
- Health probe endpoint, interval and thresholds.
- Allow standard users to change rules.
- Import / Export config (JSON).
- Logs: open folder, verbosity.
- Updates: auto-check, channel (Stable / Canary).

---

## 7. Startup & Privileges

| Component | Runs as | Starts |
|---|---|---|
| `LEO.Service` | LocalSystem | **Automatic** at boot (always, unless the engine is disabled) |
| `LEO.App` (UI/tray) | Logged-in user, non-elevated | At login via `HKCU\...\Run` **if "Start at login" is on** |

- Turning "Start at login" off removes only the Run entry. Rules keep working in the background.
- "Disable LEO engine" sets the service to Manual and stops it, and Windows networking is restored.

---

## 8. Data & Storage
- Config: `%ProgramData%\UNTERSTELLEN\LEO\config.json`, ACL'd so only SYSTEM and Administrators can write.
- Stats DB: `%ProgramData%\UNTERSTELLEN\LEO\stats.db` (SQLite).
- Logs: `%ProgramData%\UNTERSTELLEN\LEO\logs\` (rolling, 7 days).
- UI preferences: `%LocalAppData%\UNTERSTELLEN\LEO\`.
- **No telemetry. No accounts.** LEO's only network calls of its own are the update check to `api.github.com` (unterstellen/leo-windows releases) and the internet health probes (§5.7).

---

## 9. Distribution
- **MSI** built with the WiX Toolset. MSIX isn't used because it can't install the Wintun driver adapter or a LocalSystem service cleanly.
- Installer: installs the service, Wintun, the UI and Start menu shortcuts, with the option "Start LEO at login".
- Everything Authenticode-signed (with timestamp) using the certificate chosen at M8 (§16.1), in the GitHub Actions release workflow. Local dev builds stay unsigned (Smart App Control off on the dev PC).
- **Hosting: GitHub Releases** at https://github.com/unterstellen/leo-windows/releases (public repo). Each release has the signed `LEO-<version>-x64.msi` attached. Tags look like `v0.2.0`.
- **Auto-update**: the service calls `GET https://api.github.com/repos/unterstellen/leo-windows/releases/latest` (anonymous; GitHub allows 60 requests/hour per IP, so it checks at most every 6 h), compares the tag (`v0.2.0`) with its own version, downloads the MSI asset, **verifies the MSI has a valid Authenticode signature from the same certificate as the installed LEO**, then installs silently and restarts. The user can defer it. The Canary channel reads pre-releases instead of `latest`.
- Channels: `canary` (git branch `canary`) → `main` (stable).

---

## 10. Out of Scope (v1)
- Windows 10, macOS/Linux. ARM64 comes after v1.
- Single-stream bonding via an aggregation point ("Bond Server"): **not planned**, kept as a future idea (§15). LEO uses no hosted servers, ever.
- CLI and global hotkeys (could come later).
- Cloud sync, accounts, Microsoft Store, winget.
- Bandwidth limiting/QoS per app (a possible later feature).

---

## 11. Solution Layout (proposed)

```
leo-windows/
├─ LEO.md
├─ src/
│  ├─ LEO.App/            # WinUI 3 desktop + tray (H.NotifyIcon), MVVM (CommunityToolkit.Mvvm)
│  ├─ LEO.Service/        # .NET Worker Service (LocalSystem)
│  ├─ LEO.Core/           # Models, rule compiler, profiles, config schema (shared)
│  ├─ LEO.Ipc/            # JSON-RPC contracts + named-pipe transport
│  ├─ LEO.Interop/        # P/Invoke: IP Helper, Native Wifi, NLA, Wintun, engine FFI
│  └─ leo-engine/         # Rust crate → leo-engine.dll (TUN, netstack, sniffing, dialers)
├─ installer/             # WiX MSI
├─ tests/                 # unit + integration (rule matching, failover state machine)
└─ tools/                 # dev scripts (signing, packaging)
```

---

## 12. Milestones

| # | Milestone | Done when |
|---|---|---|
| **M0** | Scaffolding | Solution builds; service installs/uninstalls; UI connects over the pipe; tray icon shows |
| **M1** | Inventory + Primary + Nicknames | All adapters listed live; rename; one-click Primary via metrics; tray quick menu |
| **M2** | Health + Failover + Wi-Fi mgmt | Per-interface probes; auto failover/failback; SSID pinning per adapter; toasts |
| **M3** | TUN engine + App rules (**built**, §17) | Wintun up; all traffic goes through the engine; per-app routing works for TCP/UDP; safe restore if it crashes |
| **M4** | Destination rules + DNS (**built**, §18) | Engine DNS (redir-host instead of fake-IP, §18), SNI sniffing, domain/CIDR/port rules, per-network DNS, precedence, "test a rule" |
| **M5** | Profiles + Stats | Profiles with triggers; per-adapter/per-app stats; connections view |
| **M6** | VPN coexistence | VPN as underlay + as target + bypass; kill-switch detection |
| **M7** | Bonding | Bond groups, balancing modes, sticky sessions |
| **M8** | Ship | Self-contained builds (bundled .NET runtime, no prerequisites), WiX MSI, code signing in GitHub Actions (§16.1), GitHub Releases auto-update, uninstall cleanup, public beta on more hardware (§16), hardening, docs |
| *Future idea* | Bond through your own PC | Not planned. Single-stream bonding via the user's own second PC, never a hosted server (§15) |

---

## 12a. Development

**Requirements:** .NET SDK 10.0.404+ (pinned in `global.json`). No Visual Studio needed: Windows App SDK / WinUI 3 come in through NuGet. The split-tunneling engine for development comes from `tools\get-engine.ps1` (pinned mihomo release, SHA-256 checked; `-FromZip <file>` for an already-downloaded zip); `tools\dev-service.ps1 deploy` runs it and copies the engine next to the service.

```
dotnet build LEO.slnx                      # build everything
dotnet test  tests\LEO.Tests               # unit + in-process IPC/host tests
.\tools\dev-service.ps1 deploy             # build service, copy to Program Files\UNTERSTELLEN\LEO\Service, (re)install + start (1 UAC)
.\tools\dev-service.ps1 logs               # tail today's service log
src\LEO.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\LEO.exe   # run the UI
.\tools\dev-service.ps1 uninstall          # stop (metrics handed back to Windows) + remove
```

- The service runs from Program Files (admin-only writable), never from `bin\`, so rebuilding never collides with the running service.
- **CI:** `.github/workflows/build.yml` builds the solution (Release) and runs the tests on GitHub-hosted `windows-latest` for every push to canary/main/preview and every pull request. Tests run through `LEO.slnx` so the x64 platform mapping (`bin\x64\Release`) matches the build. Live tests stay opt-in (`LEO_LIVE_TESTS`) and never run in CI.
- `LEO.Service.exe` with no arguments runs as a console app when started from a terminal. Logs: `%ProgramData%\UNTERSTELLEN\LEO\logs\` (service), `%LocalAppData%\UNTERSTELLEN\LEO\logs\` (UI connection errors).
- Optional test switches: `LEO_LIVE_TESTS=1` runs read-only checks against the installed service; `LEO_DUMP=<file.dmp>` prints managed stacks and faulted tasks from a service dump (ClrMD, dev-only).

### IPC rules learned the hard way (don't regress these)
- **Never impersonate pipe clients** (`RunAsClient`). An identification-level token left on a thread-pool thread makes later lazy assembly loads fail with an empty `FileNotFoundException` (0x80070002), which silently killed StreamJsonRpc's read loop. Clients connect `Anonymous`; the service identifies them from the client process token (`GetNamedPipeClientProcessId` → `OpenProcessToken`, needs `TOKEN_QUERY | TOKEN_DUPLICATE`).
- **Pipe buffers must be non-zero.** With 0-byte buffers a write only completes against a real read, but System.IO.Pipelines waits with zero-byte reads.
- StreamJsonRpc's formatter is warmed up once at process start (`PipeTransport.Warmup()`), and JSON-RPC creation is serialized.
- **Smart App Control** (Windows 11) blocks unsigned, locally built binaries, including LEO's own builds and test DLLs. A dev PC needs Smart App Control turned **off**. The alternative is signing every build with a publicly trusted certificate (e.g. Azure Trusted Signing). Self-signed certificates are not accepted.

### M0 status
- Done: solution, shared build props, central package versions; `LEO.Core` (identity, paths); `LEO.Ipc` (JSON-RPC contract, ACL'd named pipe); `LEO.Service` (Windows service host, control pipe server, per-client access level, file logging, `install`/`uninstall` verbs with recovery actions); `LEO.App` (single-instance WinUI 3 window with Mica, NavigationView skeleton, tray icon + menu, close-to-tray, auto-reconnect to the service, "Start at login" toggle); `LEO.Tests`.
- LEO icon used for the exe, window, title bar and tray.
- Deferred to M8: Authenticode check of pipe clients, MSI.

### M1 status (2026-11-20)
- **Inventory** (`NetworkInventory`): .NET adapters + driver flags + Windows' visible-adapter list (MSFT_NetAdapter, which hides extra WiFiCx ports such as WiFi 3/4) + NLA profiles (SSID, connectivity, metered; stale profiles ignored) + IPv4 metrics. Kinds: Wi-Fi, Ethernet, USB tether, cellular, Bluetooth, VPN; hypervisor/WAN-miniport/tunnel adapters hidden. Network identity: `wifi:<SSID>`, else `gw:<gateway MAC>`, else `nla:<profile>`.
- **Primary + priority** (`PriorityPlanner`, `NetworkManager`): ranked adapters get metrics 10, 20, 30… (IPv4 + IPv6). Primary = first ranked adapter that is up with a gateway. Unranked adapters append wired → Wi-Fi → tether → cellular → Bluetooth, connected ones first. VPNs are listed but not managed (M6). All pinned metrics go back to automatic when the service stops.
- **Nicknames**: per adapter (matched by GUID, falling back to MAC for USB re-plugs) and per network (follows the network to any adapter). Stored in `config.json`.
- **UI**: Networks page (cards, Make primary, move up/down, Rename dialog, read-only notice), Primary card on the Dashboard, tray quick menu listing networks with a check on the Primary (click to switch).
- **Security**: changes need Full access (admins, including non-elevated split-token admins). Standard users are read-only, enforced in the service (`AccessDenied`).
- Verified on the dev PC: WiFi (Zixel) primary at metric 10, WiFi 2/Bluetooth ranked behind it, Tailscale untouched; Make primary, reorder and rename work through the real UI; internet unaffected; tray quick menu lists networks with a check on the Primary (native popup menu mode).

### M2 status (2026-11-20)
- **Health probes** (`InterfaceProbe`): every 5 s per connected candidate, *through that adapter*: DNS via the network's own DNS servers (minimal UDP resolver on a bound socket), then HTTP on a socket pinned with `IP_UNICAST_IF` + source bind. Microsoft NCSI first, Cloudflare `generate_204` second. States: Up, Degraded (slow, > 1000 ms), Captive (redirect / wrong body), NoInternet, Disconnected, Unknown.
- **Failover** (`FailoverController`, `PriorityPlanner`): failing for 6 s demotes an adapter (it keeps its rank, but its metric moves behind every healthy one); healthy for 30 s restores it. Any category change restarts the clock (no flapping). Auto-failover can be switched off in Settings.
- **Notifications** (`NotificationHub` → IPC → tray app toasts): failover, failback, primary changed, no network, new adapter, sign-in page, Wi-Fi reconnected. Rate-limited per kind/adapter. "Show notifications" is a per-user setting (`ui.json`).
- **Wi-Fi** (`WlanApi`, `WifiManager`): includes Windows 11 *secondary STA* interfaces (e.g. "WiFi 2" on DBS cards), which `WlanEnumInterfaces` hides; they come from `wlan_intf_opcode_secondary_sta_interfaces` (17). A secondary often has no scan list of its own, so LEO uses its siblings'. Connect uses the adapter's own profile, copies a saved one from another adapter, or creates one from a passphrase (Open/WPA/WPA2/WPA3-Personal; enterprise → Windows UI). Pins ("Keep connected"): checked every 15 s, reconnect with a 30 s backoff, never fights a manual Disconnect for 2 min.
- **UI**: health and latency on cards ("Internet · 8 ms"), "Skipped (failover)" badge with the switch-back countdown, failover banner (Networks + Dashboard), Wi-Fi networks dialog (scan, Keep connected, Connect with inline password, Disconnect), Settings toggles.
- **Lessons**: a converter bound to `Visibility` must return `Visibility` (a bool crashes XAML with E_NOINTERFACE). Actions must come from user events (`Click`), not from two-way-binding property changes: closing the Wi-Fi dialog once pinned a network by itself.
- Verified on the dev PC: WiFi probes Up (6–27 ms) through itself; WiFi 2 discovered as a secondary station; the Wi-Fi dialog lists Zixel as "Saved on another adapter" and WPA3 networks with password entry; pins change only on real clicks; a phone USB tether (RNDIS) was detected live, ranked, probed Up (4 ms) and announced as a new adapter.
- **Secondary STA limit (found live):** copying the Zixel profile to WiFi 2 works, but `WlanConnect` on a secondary station fails with ERROR_NOT_FOUND (1168). Windows drives secondaries itself: with `wlan_intf_opcode_secondary_sta_synchronized_connections` (18) on, the secondary joins the **same network as its primary** on the other band. So a DBS card's second station gives a second link to the *same* router, not a different network. LEO now explains this instead of showing the raw error. Turning synchronized mode on (admin) was not tried yet.
- **Safety hardening (after a live incident):** an automation mistake on my side switched the *primary* WiFi from Zixel to another network for about a minute, and more connect requests followed than were clicked. The Wi-Fi dialog now: acts only on Click events; runs one action at a time with a settle window; ignores stale rows; logs every action to the UI log; and **requires a confirming second click to move the adapter that carries the PC's internet**. Controls carry per-network accessible names ("Connect to Zixel"), which also fixes screen readers hearing five identical "Connect" buttons.
- **Dual link (built and tested live):** button on the primary Wi-Fi card that sets Windows' synchronized dual-station mode (opcode 18, admin via the service); the choice is saved (`AdapterSettings.DualLink`) and re-applied if Windows resets it. On this PC, Windows accepted the setting and **immediately started trying to connect WiFi 2 to Zixel** (2.4 GHz BSS available alongside the 5 GHz one), but every attempt ends "The operation was cancelled", RSSI 255, retrying about once a minute. This looks like the Qualcomm driver declining a secondary station on this network/AP. The card says so instead of claiming success. Turned back **off** on the dev PC (saved as off, so LEO won't re-enable it). The feature stays: on cards/drivers that allow a secondary station it works with one click. Open question: does it work with a different AP, or with a newer driver?
- **Wi-Fi "Forget" rules (user's decision):** when the user forgets a network in any Windows menu, LEO forgets it on **every** adapter, including hidden secondary stations Windows' menus can't reach, and removes it from all pins (`ForgetTracker`, checked every 15 s *before* pin enforcement). **LEO never forgets a network on its own**: only a removal on a primary adapter that's still present counts; unplugged adapters, secondaries and LEO's own deletes don't. Automatic reconnects (pins) **never copy** a profile from another adapter; copying happens only on an explicit Connect click. If a pinned network isn't saved anymore, the pin pauses and LEO says so once. (Found live: after the user forgot Zixel, a pin restored it from a hidden copy on WiFi 2. Fixed, and verified live: forgetting Zixel removed the WiFi 2 copy and the pin, and WiFi stayed disconnected.)
- **Verified live on the dev PC:** pinning (LEO connected WiFi to Zixel 4 s after the pin, twice); Wi-Fi switched off → traffic on the phone tether instantly; advanced settings load/save; dual link toggle; forget-everywhere; **joining a network with a password from LEO's Wi-Fi dialog** (profile created, stored by Windows).
- **Real failover verified live (2026-11-20 18:43–18:45):** the phone tether was made Primary, then its upstream Wi-Fi was turned off while the USB link stayed up. 18:43:32 tether NoInternet → **18:43:40 failover to WiFi** (metrics swapped, "Switched to WiFi" notification). The tether came back, blipped again at 18:44:12 (which correctly **restarted the 30 s switch-back clock**), was steady from 18:44:47 → **18:45:23 switch-back** ("Back on Ethernet"). WiFi restored as Primary afterwards.
- Last fixes from the live run: a manual Connect pauses the pin check for that adapter (it had started a second, competing connect); metrics are only rewritten/logged when they really change (cached values between scans caused repeated identical writes).

**M2 closed (2026-11-20).**
- **Robustness fixes from live testing:** the UI logs unhandled exceptions instead of vanishing; one dialog at a time; an idle secondary's radio error no longer breaks the Wi-Fi list ("Wi-Fi is turned off" shown when it is); "No network available" waits 10 s so a quick takeover doesn't trigger it; the pin check stays quiet while Wi-Fi is off; notifications name adapters "WiFi", not the driver description.
- **Card layout:** actions sit in their own row under the name and details (in a small custom `WrapPanel`, wrapping onto more lines in narrow windows), so details use the full card width and buttons never get clipped. Wi-Fi networks and Rename have text labels.
- **Page area:** a rounded panel inset from the window edges on all sides (LEO draws it instead of NavigationView's built-in layer, which runs into the right and bottom edges); page padding sits inside each page's scroll area, so the scrollbar hugs the panel's right edge.
- Probes: an adapter whose IP is changing (network switch, DHCP) is now reported **Unknown** instead of NoInternet, so a network switch can't cause a false failover.
- **Not yet exercised live: an actual failover/failback** (the phone tether is a second internet path), **and a toast on screen.**

---

## 13. Resolved Questions (2026-11-20)
1. **Signing**: not decided (SignPath ruled out 2026-11-22; options in §16.1). Whatever certificate is used, the pipe-client check (§4.2) and the update check (§9) compare against the release certificate rather than a fixed name.
2. **Branding**: follow Windows 11, with no custom colors. Logo = yellow face + dark glasses, no other features. Source `assets/leo.svg`, rendered by `tools/make-icon.ps1` into `src/LEO.App/Assets/leo.ico` (16–256 px; a simplified, bolder variant at ≤ 24 px for the tray). One icon works on both light and dark taskbars.
3. **Update hosting**: GitHub Releases on the public repo https://github.com/unterstellen/leo-windows.
4. **Health probe**: Microsoft NCSI + Cloudflare fallback, configurable (§5.7).
5. **Localization**: English only, permanently.

## 14. Open Questions
1. Contributions: should outside contributors sign off (DCO, `git commit -s`) or a CLA? Not needed until the repo accepts pull requests from others.

**Resolved 2026-11-20 — License: GPL-3.0-or-later.** Every fork or redistribution must stay open source under the GPL, which keeps LEO free forever. GPL-compatible components (GPL, LGPL, MIT, Apache-2.0, BSD) may be reused. The official text is in `LICENSE`; the build metadata (`PackageLicenseExpression`) and Settings → About say so.

---

## 15. Future idea (not planned): Bond through your own PC

**Status:** not planned (decided 2026-11-20). LEO is free and open source and will **never depend on hosted servers**. Bonding in v1 (M7) needs no server at all and stays in the plan. This section only records the idea and its limits.

### 15.1 The limit it would remove
M7 bonding spreads *connections* across networks: browsing, Steam, torrents and download managers get roughly the combined speed. A **single** connection (one big file over one connection) can't be split, because the website only knows one of the PC's public addresses. Splitting it needs a **meeting point on the internet side** that collects the pieces and talks to the website with one address.

### 15.2 Whose computer could be that meeting point?
- **A hosted server (VPS / INFERSTELLEN service): ruled out.** No hosting, no running costs, no dependency on anyone's infrastructure.
- **Other LEO users' PCs: ruled out.** Traffic would leave from a stranger's home connection (they could see it, and everything would appear to come from their address, a privacy and legal problem for both). It wouldn't even be faster: downloads would be capped by that person's home *upload* speed.
- **The user's own second PC: possible, in one situation.** E.g. a home desktop on fast fiber while the laptop is elsewhere with Wi-Fi + a phone. LEO on the laptop would tunnel over all its networks to LEO on the home PC, which reassembles and fetches from the internet.
  - Needs: the second PC always on, reachable from outside (port forwarding/UPnP; carrier-grade NAT blocks it), and a fast **upload**, because every download passes *out* of it.
  - Useless when both machines share the same home connection (the common case).

### 15.3 If it is ever built
- A "relay" mode in LEO itself (no separate server software): pair two of the user's own PCs with a code.
- Encrypted UDP tunnel per network with a packet scheduler and reorder buffer (WireGuard-style Noise + ChaCha20-Poly1305) or multipath QUIC, built on the M3 engine; bonded groups gain a "via my PC" option.
- Extras it would bring: connections that survive a network dropping (no new public IP), and an optional redundant mode for calls/games.

---

## 16. Release readiness: "works on anyone's PC"

Goal: anyone on Windows 11 x64 downloads LEO from GitHub Releases, installs it with one admin prompt, and it just works. Status as of 2026-11-20: **runs only as a developer build**. Everything below is part of M8 unless noted.

| Area | Needed for release | Status |
|---|---|---|
| Install | WiX MSI: service in Program Files, Start menu entry, optional "start at login", one admin prompt | Dev script only |
| Prerequisites | **Self-contained** builds: .NET runtime and Windows App SDK bundled, nothing to install first | App/service currently need .NET 10 installed |
| Signing | A trusted certificate signs MSI + all binaries in the release workflow (option chosen at M8, §16.1). Unsigned builds are **blocked by Smart App Control** (on by default on many fresh Windows 11 PCs) and warned about by SmartScreen | Not decided |
| Updates | GitHub Releases + in-app updater (§9), signature-checked | Designed, not built |
| Uninstall | Removes service, Wintun, metric overrides, DNS changes; restores Windows networking; keeps or asks about config | Partly (service stop restores metrics) |
| Hardware coverage | Public **beta** on GitHub Releases (pre-release) before v1; test matrix: Intel / Realtek / MediaTek / Qualcomm Wi-Fi, USB Ethernet dongles, Android + **iPhone** USB tethering, Bluetooth PAN, docks; with common VPNs (WireGuard, OpenVPN, commercial), antivirus suites and Windows Firewall on | Tested on one PC (Qualcomm DBS Wi-Fi, Android tether, Tailscale) |
| Graceful limits | Features that a driver/PC can't do (e.g. dual link, secondary stations) hide or explain themselves instead of failing | Done for Wi-Fi dual link / secondary STA |
| Antivirus | Signed releases + submitting each release to Microsoft Defender's false-positive portal; the M3 virtual adapter (Wintun) is Microsoft-signed | Not started |
| Managed PCs | Document: needs admin to install; company policies may block services/drivers | — |
| Docs | README (what it does, screenshots, install, uninstall, limits), CONTRIBUTING, privacy statement ("no telemetry, no accounts, no servers") | `LICENSE` done (GPL-3.0-or-later) |

Platform scope (decided 2026-11-20): **Windows 11 only, x64 only for v1**; ARM64 after v1; no Windows 10.

### 16.1 Code signing options (decide at M8)
Windows trusts any **valid signature from a recognised certificate authority**; per Microsoft, Smart App Control lets validly signed apps run. **SignPath Foundation is ruled out** (decided 2026-11-22: its requirements, e.g. the project being findable by name, don't fit).

| Option | Cost | Publisher shown | Notes |
|---|---|---|---|
| **Certum Open Source Code Signing** | Low (tens of € per year; check at purchase) | The developer's name ("Open Source Developer, …") | Aimed at individual OSS developers, ID verification, no project-popularity rule. Cloud key (SimplySign) needs extra setup for CI. **Current favourite.** |
| Azure Trusted Signing | ~$10/month | INFERSTELLEN (or the individual) | Easiest CI integration; eligibility for companies/individuals is strict and region-limited — check INFERSTELLEN. |
| Classic OV certificate (Sectigo, DigiCert, …) | ~€100–400/year | INFERSTELLEN | Standard; hardware token or cloud HSM. |
| Microsoft Store | Free | Microsoft | Poor fit: MSIX can't install drivers (Wintun) and services need a restricted capability; a Win32 installer in the Store still needs our own certificate. |
| Unsigned | Free | — | SmartScreen warning, and **Smart App Control blocks it outright**: not acceptable for "everyone". |

Rules regardless of option: release builds are made and signed in GitHub Actions (never on a dev PC); only LEO's own binaries are signed (Wintun and Microsoft runtime files keep their upstream signatures); the installer announces system changes (service; from M3 a virtual network adapter) and a real uninstaller exists.

---

## 17. M3 decisions and engine trial (2026-11-22)

**Decisions:**
- **Engine: embed mihomo** (GPL-3.0, github.com/MetaCubeX/mihomo) instead of writing our own. LEO.Service runs it as a child process, generates its config from LEO's rules, and controls it through its local API. It covers M3 (app rules), M4 (domain/IP rules, DNS) and most of M7 (load-balance groups). LEO stays the UI, the network brains (inventory, health, failover, Wi-Fi) and the rule model.
- **New app rules fall back to Primary** by default when their network is down (per-rule option: Block).
- **App rules cover child processes** by default (per-rule option to turn off).
- **Releases build mihomo from source in CI** and sign it with LEO (allowed by GPL), so users never run an unsigned engine. Development uses the official release binary, pinned by version and SHA-256.

**Trial on the dev PC (mihomo v1.19.34, `mihomo-windows-amd64-compatible`, zip SHA-256 `CD6738D4…AB443A79`):**
- TUN mode with `auto-route` created its own virtual adapter ("Meta Tunnel": mihomo embeds Wintun, no separate `wintun.dll` needed) and took over routing.
- Two `type: direct` outbounds bound with `interface-name` (WiFi, Ethernet tether) plus one rule `PROCESS-NAME,curl.exe,via-eth`, with `find-process-mode: always`.
- Two simultaneous 5 MB downloads to the same server: mihomo logged `(curl.exe) … match ProcessName(curl.exe) using via-eth` and `(powershell.exe) … match Match using via-wifi`; adapter counters agreed (tether +4.26 MB, WiFi +5.19 MB). Every other app stayed on the default route.
- Stopping the engine removed the virtual adapter and restored normal routing immediately.
- Notes for integration: mihomo watches the default interface itself (`default interface changed by monitor`): LEO must own that decision (`auto-detect-interface: false`, LEO rewrites the "default" outbound on Primary changes/failover). LEO's own health probes must keep bypassing the tunnel (they bind to the physical interface with `IP_UNICAST_IF`) — to verify. Process names alone are ambiguous; LEO's rules use full paths (`PROCESS-PATH`) and package families for Store apps.
- Found on the way: GitHub's anonymous REST API was rate-limited on this network. LEO's updater (M8) should read `github.com/<repo>/releases/latest` (a redirect, not rate-limited) instead of `api.github.com`.

**M3 as built (2026-11-22, live-tested on the dev PC):**
- Apps page: master on/off, "Add app" from running apps (programs using the network or with a window), installed apps (Start menu + Microsoft Store apps by install folder), an .exe picker, a whole folder, or a file-name pattern (`game*.exe`). Each rule card: network, "if unavailable" (use Primary / block), "include programs it starts", active, remove, and a live "Now:" line saying where the app's traffic goes right now.
- The engine only runs while split tunneling is on **and** at least one rule is active; otherwise LEO doesn't touch app traffic at all. Unruled traffic follows LEO's Primary (`MATCH,LEO-PRIMARY`); failover just re-points select groups through the engine API (no restart). Local/private ranges bypass the tunnel. Crash restarts back off 5/15/60 s and give up (with a notification) after 3 crashes in 10 minutes. The engine dies with the service (job object).
- Verified: rule targets and fallback (target down → Primary, or Block → the app is cut off while other apps keep working); switching off removes the engine adapter and restores normal routing; child programs are picked up via process-start events.
- **Child programs are routed by path** (the engine matches programs, not process IDs), so a child is routed wherever it runs. To keep one launcher from dragging shared tools along, programs inside the Windows folder (cmd, PowerShell, curl, ping, conhost…) are never added as children. A child's very first connection can slip out before the engine has its updated list; later ones are routed.
- Privacy: the engine logs at warning level and LEO drops every per-connection line (app + address) before writing its own log.
- Verified with two working networks (WiFi = Primary, phone USB tether = Ethernet, rule `curl.exe` → Ethernet): a 3 MB curl download arrived entirely on Ethernet (+2.98 MB, WiFi +0.00) while unruled apps stayed on WiFi; the engine didn't reduce WiFi throughput (same 3 MB download with the engine on and off).
- Verified failover with the rule active: unplugging the tether → LEO marked Ethernet "no internet" and within ~1 s re-pointed the rule's group to Primary ("Now: WiFi (fallback: Ethernet is unavailable)"); curl kept working over WiFi without restarting anything.

---

## 18. M4 decisions: destination rules + DNS (2026-11-23)

**Decisions:**
- **Precedence** (implements §2/§6.5): **App + destination > destination > app > Primary.** Among destination rules: exact host name > domain with subdomains (more labels first) > keyword > IP range (narrower first) > "any destination" (ports only); a rule with a port/protocol filter beats the same rule without one; then the user's order (move up/down on the card). One pure function (`DestinationMatcher.Ordered`) decides the order for both the engine config and "test a rule", so they can't disagree.
- **DNS: redir-host, not fake-IP.** The engine answers DNS with *real* addresses and remembers address→name, so domain rules still match exactly. Reason: with fake-IP, answers cached by Windows/apps point at addresses that stop working the moment the engine stops or crashes, which breaks §5.9 ("never brick the network"). With redir-host, stopping the engine leaves every cached answer valid.
- **Sniffing**: TLS SNI, HTTP Host and QUIC (ports 80/8080-8880, 443/8443) are read while any domain rule is active, so domain rules work for apps with their own DNS (DoH) or cached addresses. `override-destination: false`: the connection still goes to the address the app chose; the name is only used for matching.
- **Per-network DNS**: each network uses its own (DHCP) DNS servers, or the user's custom list (IPs, `https://` DoH, `tls://` DoT; up to 4). Every engine lookup is **bound to a network**: by default through the Primary to the Primary's DNS; for domains with a rule, through the target network to *its* DNS (`nameserver-policy`), so a CDN answer fits the network the traffic really uses. A target that is down gets no policy entry (its traffic falls back to Primary or is blocked, and so does its DNS). A network reporting no DNS falls back to 1.1.1.1/8.8.8.8 through that network.
- **How Windows' DNS reaches the engine**: the engine's tunnel adapter gets DNS server 198.18.0.2 and `dns-hijack: any:53` answers it (and any plain DNS an app sends to a public server through the tunnel). DNS to the router itself (192.168.x.x) is a local address, so it bypasses the tunnel; Windows prefers the tunnel's DNS, and sniffing covers the rest.
- **Rule syntax** in the engine: `DOMAIN`, `DOMAIN-SUFFIX`, `DOMAIN-KEYWORD`, `IP-CIDR(6) … no-resolve`, `DST-PORT` (`80/443/8000-8100`), `NETWORK`, combined with `AND,((…),(…))`; combos add the app's `PROCESS-PATH-REGEX` (literal parentheses written `\x28`/`\x29` because the logic parser pairs raw brackets). Destination rules are inline in `rules:` (before the app rule lists), so editing one reloads the engine config; app rules and failover stay live. A primary change now also reloads (its DNS servers change).
- **Validation**: domains are cleaned up (pasted URLs, `*.`, IDN → punycode), IP ranges are normalised (`203.0.113.77/24` → `203.0.113.0/24`); private/local ranges are refused (they never enter the tunnel); **IPv6 ranges are refused for now** because the engine runs with IPv6 off (rules would silently never match).
- **Privacy**: the service logs a destination rule's id and kind, never the domain/address; engine lines about DNS, lookups and sniffing are dropped like per-connection lines (§17). The UI log records rule ids only.
- **Combos keep their app**: deleting an app rule doesn't widen its combos to every app; they show "Not active (its app rule was removed)".

**As built:**
- Core: `DestinationRule` (+ `NetworkDnsSetting`) in `SplitTunnelSettings`; `DestinationMatcher` (normalise/validate, match, precedence, `Decide`, engine rule lines, URL/host:port parsing); `EnginePlan` emits destination rules, the `dns:` section (redir-host, bound nameservers, policy) and `sniffer:`.
- Service: `SplitTunnelManager` saves/deletes/moves destination rules, sets per-network DNS, answers "test a rule" (one DNS lookup to check IP ranges), and runs the engine when either app or destination rules are active. IPC protocol 7: `SaveDestinationRuleAsync`, `DeleteDestinationRuleAsync`, `MoveDestinationRuleAsync`, `SetNetworkDnsAsync`, `TestRouteAsync`; `SplitTunnelInfo` carries `Destinations` and `NetworkDns`.
- UI: **URLs & IPs** page: Add destination dialog (match type, address, ports, protocol, "From" app for combos, network, if unavailable, name; validated before it closes), rule cards (network, fallback, active, edit, move up/down, remove, "Now:" route, match rank), "Test a rule" (address + optional app → rule, network and why), "DNS per network" cards.
- Tests: matcher/normalisation, precedence, `Decide` (combos, app fallback), engine rule lines, DNS plan, and a test that runs the real engine's config check (`mihomo -t`) on a config with every rule kind (skipped where `artifacts\engine` is absent, e.g. CI).

**Verified live on the dev PC (2026-11-23, WiFi = Primary, phone USB tether = Ethernet, both behind one public IP, so checked by adapter byte counters):**
- Rule `speed.cloudflare.com → Ethernet` (added and edited through the URLs & IPs page): two 5 MB downloads from it arrived **entirely on Ethernet** (+4.96 MB each, WiFi +0.00); unruled downloads from proof.ovh.net (10 MB and 100 MB) arrived **entirely on WiFi** (+10.71 / +103.68 MB, Ethernet +0.00).
- Combined rule `141.95.207.0/24 · port 443 → Ethernet` (typed as an IP in the address box, detected as an IP range): a 10 MB HTTPS download from proof.ovh.net went over Ethernet (+10.39 MB, WiFi +0.01). Engine line: `AND,((IP-CIDR,141.95.207.0/24,no-resolve),(DST-PORT,443)),…`.
- The engine's connection list showed the domain both from its DNS mapping and from SNI (`host`/`sniffHost` = example.com, rule `DomainSuffix`).
- DNS: the tunnel adapter got DNS 198.18.0.2, Windows lookups went through the engine (≈ 90 ms uncached), and the ruled domain's DNS went to the tether's own DNS server bound to Ethernet (`'+.speed.cloudflare.com': '10.234.251.166#net-…'`). The service log contains rule ids only, no domains.
- "Test a rule": `https://www.example.com/page` → "Rule: example.com and its subdomains · Uses: WiFi"; `proof.ovh.net` → "Rule: 141.95.207.0/24 · port 443 · Uses: Ethernet · proof.ovh.net resolves to 141.95.207.211, which is in 141.95.207.0/24. Port 443 assumed."
- Plugging the tether in reloaded the engine with both networks without user action; removing the last rule stopped the engine, removed its adapter and left normal networking.
- Not exercised live in M4: a destination rule's fallback/block when its network fails (same select-group mechanism as app rules, verified in M3), QUIC sniffing, and DoH/DoT custom servers (covered by the engine's config check only).
