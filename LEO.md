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
- **On by default, set by the app itself (2026-11-23):** on first launch, with no choice saved in `ui.json`, the app registers `LEO.exe --tray` in HKCU Run, so it starts hidden in the tray at every login like other tray apps. A user who turns it off keeps it off; if it's on but points at another copy of LEO.exe, it's re-pointed to the running one. (Found live: the service was running but no tray icon, because the entry had never been created on the dev PC.) The M8 installer still offers the option.
- "Disable LEO engine" sets the service to Manual and stops it, and Windows networking is restored.

---

## 8. Data & Storage
- Config: `%ProgramData%\UNTERSTELLEN\LEO\config.json`, ACL'd so only SYSTEM and Administrators can write.
- Stats DB: `%ProgramData%\UNTERSTELLEN\LEO\stats.db` (SQLite through Windows' own `winsqlite3.dll`). Daily byte totals only; never connections or destinations (§19).
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
| **M5** | Profiles + Stats (**built**, §19) | Profiles with triggers; per-adapter/per-app stats; connections view |
| **M6** | VPN coexistence (**built**, §20–§21) | VPN as underlay + as target + bypass; kill-switch detection |
| **M7** | Bonding (**built**, §22–§23) | Bond groups, balancing modes, sticky sessions |
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
- **Safety hardening (after a live incident):** an automation mistake on my side switched the *primary* WiFi from Zixel to another network for about a minute, and more connect requests followed than were clicked. The Wi-Fi dialog now: acts only on Click events; runs one action at a time with a settle window; ignores stale rows; logs every action to the UI log; and required a confirming second click to move the adapter that carries the PC's internet (**removed 2026-11-23 at the user's request**: Connect switches on the first click, like Windows' own Wi-Fi menu; the one-action-at-a-time gate stays). **Changed 2026-11-23 at the user's request: no silent waiting.** The 1.5 s settle window after each action is gone (the stale-row check alone stops replayed clicks on rebuilt rows), and while an action or scan runs, Rescan and every row's checkbox/buttons are visibly greyed out next to the spinner, so a click is never ignored without the user seeing why. Same rule for the split tunneling on/off button (Apps and URLs & IPs): greyed out with "Turning on…/off…" while the request is applied; before, a quick second click re-sent the same request and looked like it did nothing (the UI log showed six "switch on" in a row). **UI rule from now on:** no hidden time limits on buttons; anything that can't be clicked right now is shown disabled. Controls carry per-network accessible names ("Connect to Zixel"), which also fixes screen readers hearing five identical "Connect" buttons.
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

---

## 19. M5 decisions: profiles + stats (2026-11-23)

**Decisions (profiles):**
- **What a profile holds** (§3): priority list, app rules, destination rules, per-network DNS, Wi-Fi pins. **Machine-wide, not per profile:** nicknames, health/failover settings, dual link, the split-tunneling master switch (the tray's "pause" stays one switch).
- **Storage: the active profile is live.** The top-level config fields (`priorityOrder`, `splitTunnel.*Rules`, `splitTunnel.networkDns`, `adapters[].pinnedSsids`) always hold the *active* profile's settings, so every existing manager and page keeps working unchanged and edits on the Networks / Apps / URLs & IPs pages belong to the active profile. Inactive profiles keep a stored copy (`profiles[].settings`). A switch is one atomic config save: store the live settings in the outgoing profile, put the target's live (`ProfileOps.Switch`). Pins are matched by adapter GUID, then MAC (USB re-plugs).
- **Migration (schema 1 → 2):** the existing settings become the active **Default** profile, untouched; the old file is kept once as `config.json.v1.bak`. Invariants are repaired on every load (Default exists and is listed last, unique ids, the active profile exists).
- **Default** always exists, can't be deleted, has no triggers, and is used whenever no other profile's trigger matches.
- **Triggers:** a Wi-Fi network is connected (any adapter; SSID exact, case-sensitive), a Wi-Fi network is in range (Windows' cached scan lists, read every 15 s only while such a trigger exists; LEO never starts scans for it), an adapter is plugged in and connected (GUID, MAC fallback). No triggers = manual only. Any one trigger of a profile is enough.
- **Precedence:** the first profile in the user's list whose trigger matches wins (move up/down on the card); Default is always last. Chosen over "most specific trigger wins" because it is predictable and visible.
- **Hysteresis (`ProfileSwitcher`):** a new choice must hold **5 s** before LEO switches; when the active profile's own triggers stop matching LEO waits **20 s** (re-plugs, Wi-Fi blips); at least **30 s** between two automatic switches; **4 automatic switches within 10 minutes pause automatic switching** (with a notification; "Back to automatic" resumes). The pause catches loops such as a profile whose pins move Wi-Fi away from the network that triggered it.
- **Manual vs automatic:** a profile picked by hand (Profiles page or tray) holds **until the automatic choice changes** from what it was at the moment of the pick (e.g. the tether gets plugged in); then automatic switching takes over again. Picking what the triggers would pick anyway counts as automatic. "Back to automatic" switches to the triggers' choice at once. Automatic switching can be turned off entirely. The manual flag survives a restart.
- **Wi-Fi safety:** a switch only rewrites pins in config. Wi-Fi profiles are never created, copied or deleted by a switch, and pin enforcement keeps its M2 rules (no copying on automatic reconnects; a user's Disconnect hold is respected). A network the user forgets in Windows is removed from the live pins **and from every stored profile**, so no profile can bring it back.
- **Notifications:** every automatic switch ("LEO switched to Phone: Ethernet is connected."), and the flap pause.
- **Privacy:** profile names and SSIDs are the user's own labels; the service log records profile ids and trigger kinds only.

### 19.1 Wi-Fi pins respect manual switches (user's decision, 2026-11-23)
Found live: after the user switched Wi-Fi in Windows' tray, LEO's "Keep connected" pin moved it back to Zixel every ~35 s, even with no LEO window open (the service enforces pins). Changed (`WifiPinPolicy`):
- Windows records how each Wi-Fi connection was made (`WLAN_CONNECTION_MODE`): **Auto** = Windows connected or roamed on its own; **Profile** (and the other explicit modes) = someone picked the network. LEO remembers its own automatic reconnects so they aren't mistaken for the user's.
- **The user picked another network on the Primary Wi-Fi** → it becomes that adapter's **first pinned network**; the old pins stay behind it as backups (notification "Wi-Fi stays on 'X'"). Stored in the active profile like any pin change.
- **The user picked another network on a non-Primary Wi-Fi** → LEO leaves it there and keeps the saved pins unchanged; they apply again only if that connection drops (notification once).
- **Drops and Windows' own automatic moves** are still corrected to the pinned network (the M2 purpose of pins). Forget-everywhere (§M2) and "never copy a profile on an automatic reconnect" are unchanged.
- Tests: `WifiPinPolicyTests` (7).

**Decisions (stats):**
- **Per adapter:** Windows' own interface byte counters (the ones `Get-NetAdapterStatistics` shows), sampled every 2 s → throughput (5-minute graph), **session** (since the adapter last connected), **today**, **this month**. A counter reset (re-plug) counts from zero. Unplugged adapters used this month stay listed.
- **Latency/loss history:** one point per existing internet check (no extra probes), last hour, in memory; failed checks shown as a percentage.
- **Per app per network:** deltas of the engine's per-connection byte counters (mihomo `/connections`, polled every 2 s), attributed to the program's file name and to the adapter whose outbound really carried the connection (fallbacks included, blocked bytes excluded). Only while the engine runs (at least one active rule), which the UI says. Local-network traffic bypasses the engine and isn't counted per app.
- **What is stored (stats.db):** only **daily byte totals** per adapter/network and per app (file name, e.g. `chrome.exe`) per network. **Never** connections, destinations, domains, addresses, ports, or anything finer than a day. A test pins the exact table/column list. **Retention:** adapter totals 400 days (13 months), app totals 90 days, pruned daily; "Clear statistics" deletes everything.
- **Live connections view** (app → destination → network → rule): fetched from the engine on demand while the Stats page is open, kept in memory only, never logged or saved. The rule shown comes from the same precedence function as the engine config (`DestinationMatcher.Decide`), plus "started by" for child programs and "No rule (Primary)" for the engine's `MATCH`. **Administrators only**, because it shows what every user's apps connect to; standard users see stats without connections.
- **SQLite:** through **`winsqlite3.dll`, the SQLite that ships with Windows 10/11** (System32), via a small P/Invoke wrapper. No NuGet package, no bundled native binary, nothing downloaded; SQLite is public domain, which fits GPL-3.0-or-later. If the database can't be opened, stats stay in memory and the service keeps running.
- **IPC protocol 8:** `GetProfilesAsync`, `SaveProfileAsync`, `DeleteProfileAsync`, `MoveProfileAsync`, `ActivateProfileAsync` (null = back to automatic), `SetProfileAutoSwitchAsync`, `ProfilesChanged` (`ProfilesInfo`: profiles, active one, manual/paused, "active because…", pending countdown); `GetStatsAsync`, `GetConnectionsAsync`, `ClearStatsAsync`; notification kind `ProfileSwitched`. Stats are polled by the open page instead of pushed, so nothing flows when nobody looks.

**As built:**
- Core: `Profiles/Profile.cs` (model, triggers, state), `ProfileOps` (capture/apply/switch/forget), `ProfileSelector` + `ProfileSwitcher` (pure, time injected), `Config/ConfigMigration`; `Stats/StatsMath.cs` (counter deltas, retention, ring buffer, connection deltas, `ConnectionDescriber`).
- Service: `Profiles/ProfileManager` (evaluates on network changes, every second while a switch is pending; applies through config and nudges NetworkManager, SplitTunnelManager, WifiManager), `Stats/StatsCollector`, `Stats/StatsStore`, `Stats/Sqlite.cs`; `MihomoEngine.GetConnectionsAsync`.
- UI: **Profiles** page (active profile + reason + countdown, Back to automatic, automatic switching on/off; cards with triggers, contents and priority; Switch to / Edit / move / Delete; add/edit dialog with a trigger editor and "start with a copy / with no rules"); **tray** profile switcher (Automatic + every profile); **Stats** page (per-network cards with throughput graph, session/today/month and latency graph; per-app per-network table; live connections with filter and pause; privacy note and Clear statistics).
- Tests (41 new, 166 total): migration (v1 file → Default + backup, repairs), selector precedence and MAC fallback, switcher delays/blips/dwell/manual hold/restart/flap pause, switch round-trips and pin matching, forget-everywhere, ProfileManager create/activate/delete/validation/order, counter deltas/resets/sessions, latency history, connection deltas and attribution, engine JSON parsing, stats.db upsert/retention/clear and its privacy schema.

**Verified live on the dev PC (2026-11-23, WiFi = Primary, phone USB tether = Ethernet):**
- Deploying migrated the real config.json to schema 2 (backup `config.json.v1.bak`); pins, priority and the empty rule lists were kept as Default.
- Profile **Phone** created on the Profiles page with the trigger "Ethernet is plugged in and connected" while the tether was plugged in: the page showed "Switching to Phone in 4 s (Ethernet is connected)", then "Active profile: Phone · Automatic: Ethernet is connected", with a notification.
- With Phone active, the rule `speed.cloudflare.com → Ethernet` added on the URLs & IPs page went into Phone only (Default still 0 rules). A 20 MB download arrived **entirely on Ethernet** (+20.78 MB, WiFi +0.01).
- Stats: the Ethernet card's totals moved by **exactly** the adapter counter's delta for a 20 MB download (+20.79 MB in Stats and in `Get-NetAdapterStatistics`). The Apps table showed `curl.exe · Ethernet` with its live rate and totals; Live connections showed `curl.exe → speed.cloudflare.com:443 · TCP → Ethernet → speed.cloudflare.com and its subdomains`, other apps "No rule (Primary)" on WiFi.
- **Unplug/replug:** tether unplugged 10:35:39 (the engine re-pointed Phone's rule to Primary right away) → **10:35:59 automatic switch to Default** (the 20 s hold), notification, engine stopped (Default has no rules). Tether back with internet 10:36:18 → **10:36:29 switch to Phone** (notification), engine started with the rule. A short dropout at 10:36:54 didn't flip the profile. After that, a 10 MB download from the ruled domain went over Ethernet again (+10.39 MB, WiFi +0.00).
- A wording fix from the live run (reasons keep the adapter's capital letter: "Ethernet is connected") was redeployed; after the service restart Phone was still active ("Automatic: Ethernet is connected"), and today's totals on the Stats page survived the restart.
- Privacy: stats.db and its WAL contain no destination names; the service log has profile ids and trigger kinds only (the only host names in it are LEO's own probe targets).

---

## 20. M6 design: VPN coexistence (2026-11-23)

Design written before the build; **built in M6, see §21 for what was built and where the build differed** (marked "As built:" below). Refines §5.8. Built on the M3 engine (mihomo TUN, `type: direct` outbounds bound with `interface-name`, select groups re-pointed live through the engine API).

**What the dev PC shows (read-only checks, 2026-11-23, engine running):**
- **Tailscale** = adapter "Tailscale" / "Tailscale Tunnel", interface type 53 (`IF_TYPE_PROP_VIRTUAL`), classified `Vpn` (name marker and type 53). Automatic metric 5 (lower than WiFi 10 / Ethernet 30, which LEO sets). It was **not signed in**: no 100.x address (only 169.254.x), 3 IPv4 routes, no catch-all routes, no NRPT rules, no DNS servers of its own (IPv6 shows Windows' `fec0:0:0:ffff::1-3` placeholders). Service `tailscaled.exe` in `C:\Program Files\Tailscale\`.
- **LEO's engine adapter** ("LEO" / "Meta Tunnel", also type 53) is filtered out by name before the VPN check, which matters because the `Wintun` marker and type 53 would otherwise make it a VPN.
- **mihomo's `auto-route` does not add `0.0.0.0/1` + `128.0.0.0/1`** (unlike the §5.2 plan): with `route-exclude-address` it installs the *complement* of the excluded ranges as **82 IPv4 prefixes, /3 to /16** (`0.0.0.0/5`, `32.0.0.0/3`, `64.0.0.0/3`, …). Only the physical adapters have `0.0.0.0/0`. Consequence: **any VPN catch-all (`0/0`, or `0/1`+`128/1`) loses the longest-prefix match to LEO's routes while the engine runs**, whatever its metric. Only routes more specific than LEO's chunk (a VPN's server `/32`, a corporate `/24`, private ranges LEO excludes) beat the engine.

### 20.1 Underlay (which physical network carries the VPN tunnel)

**Decisions:**
- **Three kinds of VPN client**, and LEO says honestly which one it sees:
  1. **Binding clients** (WireGuard for Windows, Tailscale, clients built on them: Mullvad, NordLynx, Proton WireGuard, Cloudflare WARP): they bind their own sockets to the interface with the best `0.0.0.0/0` route (`IP_UNICAST_IF`, skipping themselves) and re-bind when routes change. Their traffic **never enters LEO's tunnel**, so an app rule can't move it. Their underlay **is LEO's Primary** (lowest metric among `0/0` routes, §5.1), and failover moves them too. LEO doesn't try to force them elsewhere.
  2. **Host-route clients** (OpenVPN with `redirect-gateway`, Windows' built-in IKEv2/SSTP/L2TP, many corporate clients): they add a `/32` route to their server via the gateway that was Primary when they connected. That route is more specific than LEO's, so this traffic also bypasses the tunnel and stays on that network until the client reconnects (after a Primary change the client typically notices its server is gone and reconnects on the new Primary).
  3. **Plain clients** (OpenVPN without redirect, TCP-mode clients, most split-tunnel VPNs): their transport enters LEO's tunnel like any app, so **an app rule pins the VPN to a network** (§5.8 "underlay"), with fallback/block as usual.
- **LEO detects the kind instead of guessing:** while a VPN adapter is connected and the engine runs, LEO checks (in memory, never logged) whether the engine carries connections from that VPN's client program. Yes → "Its own traffic: carried by Ethernet (app rule)"; no → "Its own traffic: chooses its own network (follows Primary: WiFi)".
- **VPN client presets** (`VpnClients` list in Core: display name + `PROCESS-PATH-REGEX` on file name and vendor folder; exact names verified during the build): `wireguard.exe`, `tailscaled.exe`/`tailscale-ipn.exe`, `openvpn.exe`/`openvpnserv.exe`/`OpenVPNConnect.exe`, `mullvad-daemon.exe`, `nordvpn-service.exe`/NordVPN `nordvpn.exe`, Proton `ProtonVPN.WireGuardService.exe`/`ProtonVPNService.exe`, `expressvpnd.exe`, `Surfshark.Service.exe`, `WindscribeService.exe`, `warp-svc.exe`, Cisco `vpnagent.exe`, GlobalProtect `PanGPS.exe`, Forti `FortiSSLVPNdaemon.exe`, `ZeroTier One.exe`/`zerotier-one_x64.exe`. Services are matched like any process (the engine resolves PID → path for services too). Store-app VPN plugins are matched by package family (§17). Windows' built-in VPN runs inside `svchost` (RasMan) and can't be matched by program: it is always kind 2.
- **Presets are quick picks, not automatic rules.** The Apps page gets a "VPN clients" section in Add app listing the installed ones; the user picks a network as for any app rule. LEO creates no rule by itself.
- **Loop guard (always on, independent of user rules):** whenever any VPN outbound exists in the engine, the engine config starts with one guard rule per preset (`PROCESS-PATH-REGEX,<client>,<user's underlay rule target or LEO-PRIMARY>`), so a VPN's own transport can never be sent into a VPN (its own or another's) by "unruled follows the VPN" (§20.3) or a destination rule. Saving an app rule that targets a VPN for a program in the presets is refused ("A VPN can't carry its own traffic").
- The engine's own sockets stay bound to their interface (`interface-name`), so the engine never loops through a VPN's routes. LEO's health probes keep `IP_UNICAST_IF` binding.

### 20.2 VPN as a route target

**Decisions:**
- **New property `IsRouteTarget`** next to `IsPrimaryCandidate`: `IsPrimaryCandidate` stays physical-only (metrics, priority list, failover, Primary). `IsRouteTarget` = `IsPrimaryCandidate || (Kind == Vpn && connected)`, where *connected* means the adapter is Up **and** has a usable IPv4 address (not 169.254.x). The Apps and URLs & IPs network pickers list route targets; VPNs show a "VPN" tag. A rule keeps pointing at a VPN that is currently disconnected (shown "VPN not connected").
- **A VPN is never Primary.** Reasons: Primary is defined by interface metrics, and changing a VPN adapter's metric fights the VPN client (most rewrite it); failover between a VPN and physical networks would mean LEO deciding to drop or use the VPN on its own. "Send everything through the VPN" is covered by §20.3 instead. LEO never changes a VPN adapter's metric, DNS or routes.
- **VPN outbounds:** each connected VPN becomes a `type: direct` outbound with `interface-name: <VPN alias>` (outbound name `vpn-<id>`), so it exists whether or not the VPN takes the default route. Adding/removing one reloads the engine config (outbounds are static, as with a re-plugged tether in M4); re-pointing groups stays live.
- **VPN identity:** some VPNs create a new adapter (new GUID) per connection. Rules targeting a VPN store the adapter id **plus a VPN key** (interface description, e.g. "Tailscale Tunnel" / "WireGuard Tunnel: home"); when the id is gone, the rule re-resolves to a connected VPN with the same key. Same idea as MAC fallback for tethers.
- **Health for VPNs:** the existing probe (NCSI + Cloudflare fallback) also runs **bound to each connected VPN** every 5 s. States shown: **Connected · internet** (probe Up), **Connected · private network only** (probe fails: Tailscale without an exit node, corporate split VPNs), **Not connected**. VPN health never triggers failover (VPNs aren't in the priority list).
- **When a VPN-targeted rule's VPN is usable:** whenever the VPN is *connected*, not only when the probe is Up, because a split VPN carries specific destinations without carrying "the internet". The rule card's "Now:" line adds "(this VPN reports no internet access)" when the probe fails. Not connected → the rule's "if unavailable" choice.
- **New rules that target a VPN default to "if unavailable: Block"** (people route an app through a VPN so it doesn't go out without it); physical targets keep "use Primary" (§17). The user can change it per rule.

### 20.3 Bypass: a VPN that takes the default route

**Decisions:**
- **Route watching:** `NotifyRouteChange2` (IPv4 + IPv6), debounced 250 ms, then `GetIpForwardTable2`. A pure `VpnRoutes.Analyze(routes, adapters)` decides per VPN: **takes default** = a route on that VPN for `0.0.0.0/0`, **or** both `0.0.0.0/1` and `128.0.0.0/1`, **or** prefixes covering at least half of the public IPv4 space (clients that split the space differently); else **routes some destinations** (count of public prefixes, for display) or **private only**. Same for IPv6 (`::/0`, `::/1`+`8000::/1`). The analysis ignores LEO's own adapter.
- **LEO's tunnel stays the catch-all without fighting:** because the engine's routes are /3–/16 (see dev-PC findings), a VPN's catch-all loses to them automatically; LEO never deletes or edits another product's routes. LEO checks after each route change that **its own** routes are still present; if another program removed them, LEO restarts the engine once (bounded by the M3 crash back-off) and otherwise stops it with a notification ("Another program keeps changing routes; split tunneling paused").
- Routes a VPN pushes that are **more specific** than LEO's (corporate `/24`s, the private ranges LEO excludes, Tailscale's `100.64.0.0/10`) keep working natively through the VPN. LEO shows "This VPN also routes N address ranges itself" and does not apply its rules to those ranges (same as local addresses today). The private ranges stay excluded from the tunnel (`ExcludedRanges` unchanged), so Tailscale peers and corporate `10.x` networks keep working.
- **Unruled traffic follows the VPN by default** (the recommendation): when a VPN takes the default route, the engine's `MATCH` target becomes that VPN, so turning a VPN on behaves as people expect and LEO doesn't silently undo it. Only rules aimed at a physical network bypass it ("bypass VPN" = an app or destination rule to WiFi/Ethernet). **Per-VPN option "Let unruled traffic skip this VPN"** (off by default) keeps unruled traffic on Primary; then only rules aimed at the VPN use it.
  - Engine: a new select group `LEO-UNRULED` = [`vpn-<id>`…, `LEO-PRIMARY`] and `MATCH,LEO-UNRULED`. Re-pointed live through the engine API on VPN up/down, like failover. Two VPNs taking default: the one with the lower route metric wins (what Windows would do).
  - **When a VPN starts taking the default route** and unruled traffic follows it, LEO closes the engine's open *unruled* connections that run over a physical network (engine API `DELETE /connections/{id}`), so long-lived connections reconnect through the VPN instead of staying outside it. Ruled connections are left alone.
  - Window: between the VPN's route change and LEO re-pointing (debounce + API call, well under a second) new unruled connections can still start on Primary. Documented; not worth more machinery.
- **DNS while unruled traffic follows a VPN:** Windows' DNS keeps pointing at the engine (198.18.0.2), so without a change the VPN's DNS would be skipped. So the engine's **default nameservers become the VPN adapter's own DNS servers, bound to the VPN outbound**; domains with rules to a physical network keep that network's DNS (`nameserver-policy`, §18). A VPN that sets no DNS (e.g. Tailscale without an exit node) changes nothing. **NRPT rules** (corporate split DNS, Tailscale's MagicDNS domains) are read from the registry (`…\Dnscache\Parameters\DnsPolicyConfig`, read-only) and mirrored into `nameserver-policy` bound to the VPN, so names the VPN owns resolve through the VPN even for lookups the engine makes (Windows' own queries for NRPT names already go straight to those servers). With "skip this VPN" on, only the NRPT domains go to the VPN's DNS.
- **IPv6:** the engine runs with IPv6 off (M4), so IPv6 never enters LEO's tunnel today: it follows Windows' routes (into the VPN if the VPN routes IPv6, else the physical network). M6 keeps that and says so on the VPN card ("IPv6 follows Windows' routes"). Turning IPv6 capture on is an open question (§20.7).

### 20.4 Kill-switch detection

**Decisions:**
- **Signals** (no new traffic beyond the existing probes plus one tunnel self-check):
  - The physical probes are already bound to each adapter. A WFP block at connect time fails **immediately with `WSAEACCES` (10013)**; LEO's probe classifies errors as `Denied` / `Timeout` / `Refused` / `Ok` (today: failed).
  - The VPN-bound probe (§20.2).
  - **Tunnel self-check** while the engine runs: one TCP connect to the probe target bound to LEO's own adapter (it enters the engine and goes out per rules).
- **Verdict** (pure `KillSwitchDetector`, time injected, 2 consecutive checks required):
  - **Blocks bypass**: a VPN is connected and its probe is Up, and **every** physical probe fails with `Denied` (or all physicals that were Up turned to failures within 10 s of the VPN connecting). Shown as: physical networks "Blocked by <VPN>'s firewall" (new `HealthState.BlockedByVpn`, which **does not count as failing**: failover and profile triggers freeze instead of hopping between networks the VPN blocks anyway), a banner on Apps / URLs & IPs ("<VPN> blocks traffic outside it. Rules that bypass it can't connect while it's on."), and the rule cards' "Now:" lines. Rules aimed at physical networks are **not** silently moved into the VPN.
  - **Blocks LEO**: the tunnel self-check fails while the VPN probe is Up (the VPN's firewall blocks apps from sending into LEO's adapter, as e.g. Mullvad's does). Then **LEO stands aside automatically**: it stops the engine (normal Windows routing = everything through the VPN), notifies ("Split tunneling paused: <VPN> only allows its own tunnel. It resumes when <VPN> disconnects."), and restarts the engine when that VPN disconnects. No user action can keep it on, because it would cut off every app (§5.9).
- **Naming the product:** the firewall's owner is named from read-only WFP provider enumeration (`FwpmEngineOpen0` + `FwpmProviderEnum0`, provider display names), falling back to the VPN adapter's name. Read-only, best effort.
- **LEO never disables, edits or works around another product's firewall, WFP filters or settings**, and never suggests turning a kill switch off; the message says what is blocked and that the VPN's own setting decides.

### 20.5 Safety (§5.9)

- **VPN connects while the engine runs:** route change → analysis → engine reload if a new VPN outbound is needed, `LEO-UNRULED` re-pointed, unruled physical connections closed (§20.3). Rules to physical networks keep working unless a kill switch blocks them (§20.4).
- **VPN disconnects while the engine runs:** rules targeting it switch to their "if unavailable" choice (live re-point; Block by default for VPN targets), `LEO-UNRULED` goes back to `LEO-PRIMARY`, the VPN outbound is removed on the next reload. Unruled traffic then goes to Primary, the same as without LEO; LEO is not stricter than the VPN for traffic nobody ruled.
- **Engine crash or stop with a VPN up:** the engine's adapter and routes disappear with it, so Windows' routes apply again: the VPN's catch-all takes everything (nothing leaks outside the VPN that wouldn't without LEO); bypass rules stop bypassing until the engine is back (M3 back-off). LEO never adds routes of its own outside the engine.
- **Block never leaks while the engine runs:** a VPN-targeted rule with Block whose VPN is down maps to `REJECT` (same mechanism as M3). Guard rules (§20.1) are generated before user rules. When the engine is **not** running (crash back-off, stood aside, service stopped), Block rules are not enforced today; whether LEO should add its own Windows Firewall rules for that is an open question (§20.7).
- **Health/failover safety:** `BlockedByVpn` never triggers failover; VPN adapters never get metrics, DNS or route changes; LEO's own probes stay bound and never use a VPN unless they probe that VPN.
- **Privacy:** the kind detection (§20.1) and connection closing (§20.3) use engine connection data in memory only. The log records VPN adapter ids, the analysis result ("takes default: yes"), verdicts and counts, never destinations or which app connected where. Route tables are not logged.

### 20.6 Implementation plan (for the M6 build session)

**Core (`src/LEO.Core`):**
- `Networking/AdapterKind.cs`: `IsRouteTarget(kind, connected)`; keep `IsPrimaryCandidate` physical-only.
- `Networking/VpnRoutes.cs` (new): route record, `Analyze` → per VPN `TakesDefault4/6`, public prefix count, private-only; coverage math for "half the public space".
- `Networking/VpnIdentity.cs` (new): VPN key from description/alias; re-resolve a rule's target.
- `SplitTunnel/VpnClients.cs` (new): preset list and regexes; "is a VPN client" check used by the guard and rule validation.
- `Health/KillSwitchDetector.cs` (new, pure): inputs per tick (VPN states, probe outcomes with error class, tunnel self-check), verdicts `None`/`BlocksBypass`/`BlocksLeo` with hysteresis.
- `HealthState`: add `BlockedByVpn` (`IsFailing()` false); probe result gets an error class.
- `SplitTunnel/EnginePlan.cs`: `EngineAdapter` gains `IsVpn`, `VpnKey`, `TakesDefault`, `SkipUnruled`; VPN outbounds (`vpn-<id>`); guard rules first; `LEO-UNRULED` group + `MATCH,LEO-UNRULED`; DNS default switches to the VPN's servers when unruled follows it; NRPT namespaces → `nameserver-policy`; VPN-targeted rules usable when connected.
- `SplitTunnel/SplitTunnelSettings`: `AppRule`/`DestinationRule` gain `TargetVpnKey`; machine-wide `VpnSettings { Key, SkipUnruled }` list (not per profile, like dual link). Optional fields with defaults: **no config schema bump** (stays 2).

**Service (`src/LEO.Service`):**
- `Interop/RouteWatcher.cs` (new): `NotifyRouteChange2` + `GetIpForwardTable2`, debounced event.
- `Interop/Nrpt.cs` (new): read NRPT rules from the registry.
- `Interop/Wfp.cs` (new): read-only `FwpmProviderEnum0` for display names.
- `Networking/VpnMonitor.cs` (new): owns route analysis, VPN probes, kill-switch verdicts; raises changes to NetworkManager and SplitTunnelManager.
- `Health/InterfaceProbe.cs` / `BoundSockets.cs`: error classification (10013 = Denied); probe bound to LEO's adapter for the self-check.
- `Networking/NetworkInventory.cs` / `NetworkManager.cs`: VPN adapters get connected/usable-address detection, VPN health, `IsRouteTarget` in `AdapterInfo`; failover freezes while `BlockedByVpn`.
- `SplitTunnel/SplitTunnelManager.cs`: accept VPN targets (validation uses `IsRouteTarget`, refuses VPN clients → VPN), re-resolve VPN keys, re-point `LEO-UNRULED`, close unruled physical connections, stand aside / resume, notifications.
- `SplitTunnel/MihomoEngine.cs`: `CloseConnectionAsync(id)`.
- `Profiles/ProfileManager.cs`: triggers ignore VPN adapters (unchanged), freeze while `BlockedByVpn` (no switch because "Ethernet lost internet").

**IPC (`src/LEO.Ipc`), protocol 8 → 9:**
- `AdapterInfo`: `IsRouteTarget`, `Vpn` (`VpnInfo`: connected, internet/private only, takes default 4/6, public ranges it routes itself, its own traffic carried by `<adapter>`/follows Primary, firewall verdict and owner name, DNS server count, skip-unruled).
- `HealthState.BlockedByVpn`; notification kinds `VpnFirewallDetected`, `SplitTunnelStoodAside`, `SplitTunnelResumed`, `RoutesTampered`.
- New `SetVpnSettingsAsync(key, skipUnruled)`; `GetVpnClientPresetsAsync()` (installed preset clients for Add app).
- `SplitTunnelInfo`: `StoodAside` + reason.

**UI (`src/LEO.App`):**
- Networks page: VPN cards (no priority/Primary controls): state line, "Carries: all traffic / N address ranges / private network only", "Its own traffic: …", "Let unruled traffic skip this VPN" toggle, firewall banner. Handlers only from Click, one ContentDialog at a time.
- Apps and URLs & IPs: pickers use `IsRouteTarget` (VPN tag, disconnected VPN kept on existing rules); new VPN-target rules default to Block; Add app → "VPN clients" section; banners for "blocks bypass" and "stood aside"; "Now:" lines with VPN wording. "Test a rule" says "Unruled → <VPN> (follows the VPN)".
- Tray tooltip/menu: "Through <VPN>" when unruled follows a VPN; "Split tunneling paused by <VPN>" when stood aside.
- Stats: VPN adapters get cards like other networks (Windows counters), so per-app stats show `→ Tailscale`.

### 20.7 Open questions (the user's call)

1. **Fail-closed Block rules when the engine isn't running** (engine crashed, stood aside, service stopped): should LEO add **its own** Windows Firewall rules (a "LEO" rule group, per blocked program, removed on uninstall) so a "route through VPN, else block" app can't go out over WiFi in those moments? It changes Windows Firewall state, which LEO hasn't done so far. Recommendation: yes, but as a separate step at the end of M6 (or M8 hardening), only for VPN-targeted Block rules, never touching rules LEO didn't create. **Decided: yes (the recommendation); built, §21.**
2. **IPv6 capture:** keep IPv6 outside the engine in M6 (it follows Windows' routes, VPN or not), or turn it on now (rules then cover IPv6, and a target without IPv6 blocks that rule's IPv6, §5.2)? Recommendation: keep it off in M6 and do it as its own step once an IPv6-capable network is available for testing; the phone tether may offer IPv6. **Decided: keep it off in M6 (the recommendation).**
3. **Test VPNs beyond Tailscale** (see 20.8): installing a second VPN client means a download and possibly an account. Which one, if any, may be used: WireGuard for Windows with the user's own server/peer, or a free commercial tier with a kill switch (e.g. Proton VPN Free)? And is there a Tailscale exit node (one of the user's own devices) to test "takes default"? **Decided: Tailscale only for now; another client or an exit node only after asking the user (§21).**

### 20.8 Test plan

**Unit tests (`tests/LEO.Tests`):**
- `VpnRoutes.Analyze`: `0/0` on VPN; `0/1`+`128/1`; Mullvad-style split covering ≥ half; corporate `/24`s only (routes some); private only (Tailscale `100.64/10`); LEO's own adapter ignored; IPv6 `::/0` and `::/1`+`8000::/1`; two VPNs → lower metric wins.
- Engine routes vs VPN routes: a helper computing which interface wins longest-prefix for sample addresses, given LEO's complement routes (from `ExcludedRanges`) and a VPN's catch-all; asserts the engine wins public addresses and the VPN keeps `10.x`/`100.64/10`.
- `IsRouteTarget` / connected detection (169.254.x not connected); VPN never in `PriorityPlanner` candidates.
- `VpnIdentity`: re-resolve by key after a GUID change; no match → rule's fallback.
- `EnginePlan` with VPNs: `vpn-` outbound with `interface-name`; guard rules generated first for every preset whenever a VPN outbound exists; `LEO-UNRULED` membership and selection for follow / skip / VPN down / two VPNs; VPN-targeted rule with Block → `REJECT` when not connected; DNS default = VPN's servers bound to `vpn-` when following, physical-rule domains keep their policy, NRPT namespaces in policy; and the real-engine config check (`mihomo -t`) on a config with a VPN outbound and the new group.
- Validation: rule VPN client → VPN refused; new VPN-target rule defaults to Block.
- `KillSwitchDetector`: all physicals `Denied` + VPN Up → BlocksBypass after 2 ticks; one physical OK → None; physicals time out (not Denied) long after VPN connect → None (a real outage); self-check fails + VPN Up → BlocksLeo; VPN down → clears; `BlockedByVpn` doesn't trigger failover (`FailoverStateMachine` test) or profile switches.
- Probe error classification: 10013 → Denied, timeout, refused.
- Privacy: log lines from VPN monitor/analysis contain no addresses or prefixes (log-capture test as in M4).

**Live on the dev PC with Tailscale (needs it signed in):**
- Tailscale connected, no exit node: VPN card "Connected · private network only", "Carries: private network only", "Its own traffic: chooses its own network (follows Primary: WiFi)"; no takeover; unruled stays on WiFi; `100.x` peers reachable with the engine on.
- Rule `curl.exe → Tailscale` without exit node: "Now:" says Tailscale with "(this VPN reports no internet access)"; disconnect Tailscale → rule blocks (default Block) and other apps keep working; reconnect → back.
- Primary switch WiFi → Ethernet: `tailscaled` re-binds to Ethernet (adapter counters while Tailscale is busy), card follows.
- With a **Tailscale exit node** (if the user has one): takeover detected (`0/0` on Tailscale); `LEO-UNRULED` → Tailscale; unruled download goes through Tailscale (counters on Tailscale + its underlay), rule `speed.cloudflare.com → Ethernet` bypasses it (Ethernet counter only); DNS: engine default nameserver becomes Tailscale's (100.100.100.100) if set; stop the engine → Windows routes send everything through the exit node; whether Tailscale's own firewall blocks bypass (detector verdict) is observed, not assumed.
- Route tamper simulation is **not** done live (it would change network settings); covered by unit tests.

**Needs another VPN (per open question 3):** a host-route client (OpenVPN / built-in IKEv2) for kind 2; a plain client for an underlay app rule actually moving the tunnel; a WFP kill switch ("Block untunneled traffic" in WireGuard for Windows, or a commercial client) for BlocksBypass and BlocksLeo + stand-aside/resume; a VPN that pushes DNS and NRPT rules.

---

## 21. M6 as built + live results (2026-11-23)

Built from §20 with the §20.7 recommendations (fail-closed firewall rules: yes; IPv6 stays outside the engine; Tailscale only for live tests). IPC protocol **9**; config schema stays **2** (new fields are optional).

**As built:**
- **Core:** `AdapterKind.IsRouteTarget(connected)` + `VpnConnection.IsConnected` (Up and a non-169.254 IPv4); `VpnRoutes` (route record, `Analyze` → takes default 4/6, public range counts, private only; `DefaultWinner`, `BestRoute`, `Complement4`, `EngineRoutesPresent`); `VpnIdentity` (`KeyOf`: description without " #N", alias appended for WireGuard/Wintun/TAP; `Resolve`); `SplitTunnel/VpnClients` (14 presets, RE2 regex by file name, `NamedBy`, `UnderlayRule`, `Installed`) + `RuleTargets` (`Check`, `DefaultOnTargetDown`) + `FailClosedRules`; `KillSwitchDetector`; `HealthState.BlockedByVpn` (neither failing nor usable) and `ProbeError` on `HealthReport` (`ProbeErrors.Classify`: 10013 Denied, 10061 Refused, 10060/timeouts Timeout). `EnginePlan`: `EngineAdapter` gains `IsVpn/TakesDefault/SkipUnruled/RouteMetric`; VPN outbounds `vpn-<id>`; `LEO-PRIMARY` stays physical-only; new `LEO-UNRULED` group (always present, = [VPN outbounds…, LEO-PRIMARY]) with `MATCH,LEO-UNRULED`; guard rules first; VPN DNS and NRPT policy; `UnruledVpnId`. `AppRule`/`DestinationRule.TargetVpnKey`; `SplitTunnelSettings.Vpns` (machine-wide, not in profiles: `ProfileOps` captures rules, DNS and pins only).
- **Service:** `Interop/RouteWatcher` (`NotifyRouteChange2` + `GetIpForwardTable2`, 250 ms debounce; metric = route + interface metric), `Interop/Nrpt` (both registry locations, read-only), `Interop/Wfp` (read-only provider names + `OwnerFor`), `Interop/FirewallRules` (COM `HNetCfg.FwPolicy2`, group "LEO (VPN only)" only). `Networking/VpnMonitor` (new hosted service): on route changes and every probe interval it analyses routes, reads NRPT rules whose servers are a connected VPN's DNS, probes each connected VPN bound to it, runs the tunnel self-check (a TCP connect bound to the "LEO" adapter) while a VPN is connected and the engine runs, detects the VPN client kind from engine connections (outbound only, in memory), feeds `KillSwitchDetector`, and publishes a `VpnPicture` to `NetworkManager` (`ApplyVpnPictureAsync`) and `SplitTunnelManager`. `NetworkManager` builds `AdapterInfo.Vpn`/`IsRouteTarget` and turns failing physical health into `BlockedByVpn` under a BlocksBypass verdict (failover freezes: that state is in the "neither" category). `SplitTunnelManager`: VPN targets allowed (refuses VPN client → VPN), `TargetVpnKey` stored on save and re-resolved on every sync, VPN-aware "Now:" lines, `SetVpnSettingsAsync`, closes unruled physical connections when a VPN starts taking unruled traffic (`MihomoEngine.CloseConnectionAsync`), stand-aside/resume, route tamper (restart once, then pause), fail-closed firewall reconcile. Probe and DNS errors keep "access denied" recognisable. Stats show VPN cards and attribute `vpn-` outbounds.
- **IPC 9:** `AdapterInfo.IsRouteTarget`, `AdapterInfo.Vpn` (`VpnInfo`), `HealthState.BlockedByVpn`, `VpnFirewall`, notification kinds `VpnFirewallDetected`/`SplitTunnelStoodAside`/`SplitTunnelResumed`/`RoutesTampered`, `SplitTunnelEngineState.StoodAside`, `SplitTunnelInfo.UnruledVia`/`VpnBanner`, `SetVpnSettingsAsync`, `GetVpnClientPresetsAsync`.
- **UI:** Networks page VPN cards (state "Connected · internet / private network only / Not connected", "Carries: …", "Its own traffic: …", firewall text, "Let unruled traffic skip this VPN" checkbox with a Click handler; no priority/Primary controls). Apps and URLs & IPs pickers list physical networks, then VPNs ("Tailscale · VPN (not connected)"); moving a rule onto a VPN, or a new destination rule aimed at one, selects "Block" (form state only until Save); Add app → **VPN clients…** (installed presets); a VPN banner on both pages; status text "Everything else goes through <VPN>"; tray tooltip "· through <VPN>" / "· split tunneling paused by a VPN".

**Where the build differs from §20:**
- **Fail-closed Block (§20.7 Q1) is built:** for every enabled **exact-path** app rule that targets a VPN with "Block" (not a VPN client, split tunneling on), LEO keeps one outbound **block** rule in its own Windows Firewall group "LEO (VPN only)": that program, remote = public addresses only (the IPv4 complement of `ExcludedRanges` + `2000::/3`), interfaces = the physical adapters. While the engine runs, the app's traffic enters LEO's adapter (or the VPN's), which the rule doesn't cover; with the engine down the app can't go out directly. Folder and pattern rules can't be expressed as firewall programs and get none. Reconciled when the wanted set changes and every 5 minutes; rules outside LEO's group are never read back or touched; removed when the LEO rule goes, when split tunneling is switched off, and on uninstall (`LEO.Service.exe uninstall`, which `dev-service.ps1 deploy` also runs; the restarted service recreates them).
- **Profile triggers** needed no freeze: they use connection status, not health, so `BlockedByVpn` can't flip them.
- **Complement size:** LEO's own complement of `ExcludedRanges` is 77 prefixes; mihomo installed 82 on the dev PC (it splits more finely). Same coverage; the unit test checks coverage and longest-prefix outcomes, not the count.
- **`LEO-UNRULED` always exists** (just `[LEO-PRIMARY]` without VPNs), so a VPN taking or dropping the default route is a live selection change, not a reload. The two M3 tests that pinned `MATCH,LEO-PRIMARY` and "one group" were updated for that.
- VPN probes live in `VpnMonitor`, not in `NetworkManager`'s probe loop. A VPN without DNS of its own is probed with the probe's fallback resolvers (1.1.1.1/8.8.8.8) through the VPN, so Tailscale without an exit node shows "private network only".

**Tests:** 51 new (224 total, all green): route analysis (0/0, halves, ≥ half split, corporate /24s, Tailscale private, LEO adapter ignored, IPv6, two VPNs, skip), engine vs VPN longest prefix with the complement routes, identity keys and re-resolution, connected / route target / never in the priority list, presets and regex safety, VPN client → VPN refused, Block default, engine plan (VPN outbound, not in Primary, Block → REJECT when down, usable while private-only, guards first and following the underlay rule, `LEO-UNRULED` follow/skip/down/two VPNs, live vs reload, VPN DNS + physical policies + NRPT), connection description, kill-switch detector (denied, one OK, outage vs connect window, BlocksLeo latch, private-only VPN), `BlockedByVpn` freezes failover, probe error classes, WFP owner naming, no addresses in VPN info/analysis, fail-closed planning and scope, and the real engine's `mihomo -t` on a config with two VPNs, guards, `LEO-UNRULED` and NRPT policy. Opt-in live helpers: `LiveRouteTableTests` (`LEO_LIVE_TESTS=1`) and `LiveM6Tests` (`LEO_LIVE_M6=show|add|remove|vpn-skip|vpn-follow`, `LEO_M6_TARGET=<adapter>`).

**Verified live on the dev PC (2026-11-23, WiFi = Primary, Tailscale installed but signed out):**
- The route table read by `RouteWatcher` matches `Get-NetRoute` (47 routes, 1 default).
- Deploy: service protocol 9, UI reconnected; `VpnMonitor` reported Tailscale "connected False" (169.254 address); its card data: key "Tailscale Tunnel", not a route target, health Disconnected.
- **Engine start/stop leaves Windows unchanged:** with a temporary rule `curl.exe → WiFi` the engine ran (82 routes on "LEO", `MATCH,LEO-UNRULED`, no guards or `vpn-` outbounds while no VPN is connected); after removing the rule the adapter was gone and routes, IPv4 DNS servers and interface metrics were identical to the snapshot taken before (63 entries compared).
- **VPN target + fail-closed:** rule `curl.exe → Tailscale` (Block, the VPN default) while Tailscale was signed out: "Now: Blocked (Tailscale is not connected)"; curl through the engine got no answer while PowerShell got 204. LEO's firewall rule appeared ("LEO: curl … only through its VPN", outbound block, interfaces WiFi / WiFi 2 / Bluetooth, 78 public remote ranges, `curl.exe`). curl **bound directly to WiFi** (the path it takes with the engine down) was refused in 30 ms, a renamed copy of curl not covered by the rule got 204, and the router stayed reachable (HTTP 200). Removing the LEO rule removed the firewall rule (0 left) and curl worked again. (Killing the SYSTEM-owned engine to test a real crash isn't possible from a user shell; the WiFi-bound check exercises the same path.)
- **Skipped by the user's choice (Tailscale stays signed out, 2026-11-23); still to do when it is signed in:** VPN card "Connected · private network only", route-change detection when Tailscale connects, `vpn-` outbound + guards in the engine config, rule `curl.exe → Tailscale` with "(this VPN reports no internet access)" and Block on disconnect, unruled traffic staying on WiFi, `100.x` peers with the engine on. An exit node (takeover, `LEO-UNRULED` → Tailscale, DNS 100.100.100.100, connection closing) only with the user's OK.

**Not exercised live (needs another VPN, §20.8):** host-route and plain clients (kinds 2/3, an underlay app rule moving the tunnel), a WFP kill switch (BlocksBypass, BlocksLeo stand-aside/resume, WFP owner naming), a VPN that pushes NRPT rules; route tamper (unit-tested logic only).

---

## 22. M7 design: bonding (2026-11-23)

Design written before the build; **built in M7, see §23 for what was built, what the live tests found, and where the build differs** (marked "As built:" below). Refines §6.8. Built on the M3–M6 engine: mihomo TUN, `type: direct` outbounds bound with `interface-name` (`net-<id>`, `vpn-<id>`), select groups re-pointed live through the engine API, `LEO-PRIMARY` (physical only) and `LEO-UNRULED` (`MATCH`). **No server of any kind** (§10, §15).

**What the dev PC shows (read-only checks, 2026-11-23, engine not running because no rule is active):**
- **Networks right now:** WiFi up (Qualcomm FastConnect 6900, 866.7 Mbps link, the only `0.0.0.0/0` route, metric 10); the phone USB tether is **not plugged in**; WiFi 2 is a secondary station of the same card, which can only join the *same* router (§12a M2), so it is no second path; Tailscale up but signed out (§21); Bluetooth PAN disconnected. A live bonding test needs the tether plugged in.
- **One public address:** WiFi and the tether leave through the same public IP. If the phone's own upstream is the same Wi-Fi router, bonding the two can't add bandwidth beyond that router's line; live tests on this PC can prove **distribution** (adapter byte counters, as in M3–M5), not a **speed gain**. A speed gain needs the phone on mobile data (allowed by the user, §22.8 Q3). It also means "a site sees two different addresses" (the main risk of per-connection spreading) can't be observed here.
- **Engine support (pinned mihomo v1.19.34, `artifacts\engine`):** `mihomo -t` accepts `type: load-balance` with `strategy: consistent-hashing`, `sticky-sessions` and `round-robin`, `type: url-test` (with `tolerance`) and `type: fallback`, each with `url`, `expected-status`, `interval`, `timeout`, `lazy`, `max-failed-times`, built from `type: direct` members bound with `interface-name`, and a load-balance group as a member of a select group (`[bond, REJECT]`) and of `LEO-UNRULED`. There is **no** least-loaded strategy and **no** per-member weight option.
- **Engine API (mihomo run briefly on loopback only: no TUN, no DNS, no health-check URL; stopped afterwards):** a load-balance group **keeps repeated members** (`"all": ["net-a","net-a","net-a","net-b"]`), which is how weights can be expressed (the actual split is to be verified in the build); `PUT /proxies/<load-balance group>` is refused (`"Must be a Selector"`), so **a bond's membership can't be changed live**, only by a config reload; a group without `url` reports `testUrl: https://www.gstatic.com/generate_204` (Google), so **LEO must always set the health-check URL itself** (§8: LEO's only own calls are updates and its configured probes).

### 22.1 Bond groups

**Decisions:**
- **A bond** = a name, **2 to 8 member networks** in the user's order, a balancing mode (§22.2), a weight per member (1–5, used by the Weighted mode only; *as built: also by Every connection, §23*) and "if every network in it is unavailable" (use Primary / Block, default **use Primary**). It is a Route Target (`Group:<id>`, §3) that app rules, destination rules and combos can pick, and that unruled traffic can use (§22.4).
- **Members are physical networks only** (`IsPrimaryCandidate`: Wi-Fi, Ethernet, USB tether, cellular, Bluetooth), referenced like rule targets (adapter id, MAC fallback for re-plugged tethers). **VPNs can't be members:** a VPN rides on one of the same physical networks, so bonding it with its own underlay adds no bandwidth, and mixing a VPN with direct networks would send some of an app's connections outside the VPN. A rule aimed at a VPN stays a single-target rule (§20.2).
- **A VPN client can't be routed into a bond** (refused on save: "A VPN's tunnel is one connection; bonding can't speed it up"). Its tunnel is one long flow that would stay on one member anyway and be torn down if that member left. The M6 loop guard therefore never points at a bond.
- **Same-router members** (e.g. a DBS card's two stations on one router, or two Ethernet ports into one switch) are allowed, but the bond card says "WiFi and WiFi 2 use the same router: bonding them adds no internet speed" when LEO sees the same gateway MAC on two members.
- **Metered members** (Windows' own metered flag, which LEO only reads, §5.6) are allowed without any limit or cap; the member just shows a "metered" tag, as Windows does. LEO never changes Windows' metered setting (user's decision, §22.8 Q3).
- **Members that aren't connected** are left out of the engine group (the engine's outbounds only exist for connected adapters, as today; plugging in or unplugging already reloads the engine). A bond with **one** usable member keeps working on that member ("Bonding 1 of 2 networks: Phone (USB) is unavailable"). With **none**, the bond's rules get their "if unavailable" choice (§22.3).
- **Bonds are machine-wide** (user's decision, §22.8 Q2), like nicknames and VPN settings: stored in `splitTunnel.bonds`, **not** copied by `ProfileOps`, so a profile switch never changes them. Rules in any profile can point at any bond. Optional field, empty by default: **no config schema bump** (stays 2).

### 22.2 Balancing modes and sticky sessions

**Decisions** (§6.8 named weighted, round-robin, least-latency and least-loaded; this maps them onto what the engine really has):

| LEO mode (UI name) | Engine group | What it does | When to use |
|---|---|---|---|
| **By site** (default) | `load-balance`, `strategy: consistent-hashing` | Every connection to the same site (the engine hashes the registrable domain, e.g. `example.co.uk`, or the address when there's no name) goes to the same network; different sites spread over the members. Stateless: survives engine reloads and service restarts. *As built: survives config reloads, but not a restart of the engine process (the engine seeds its hash per process), §23.* When a member leaves, only *its* sites move. | Everyday browsing and apps; logins never see the address change |
| **Weighted** | same, each member listed `weight` times | Like By site, but shares follow the weights (3 : 1 ≈ 75 % / 25 % of *sites*, not of bytes) | A slower member that should take less |
| **Every connection** | `load-balance`, `strategy: round-robin` | Each new connection goes to the next member, even to the same site. Gets the most out of many-connection downloads from **one** server (download managers, launcher CDNs, `aria2 -x`). A site sees the PC's connections arriving from several addresses when the members have different public IPs; sites that tie a login to the address may sign the user out. | Big downloads; the card says this |
| **Fastest** | `url-test`, `tolerance: 50` (ms) | Not spreading: all new connections use the member with the lowest health-check latency, switching only when another is faster by more than the tolerance | A bond used as "always the quickest network" |

- **Least-loaded is not offered:** the engine has no such strategy, and emulating it would mean LEO re-pointing select groups on every throughput sample (connections can't move once open, so it would only steer new ones, seconds late). Recorded as dropped from §6.8; it can come back if the engine gains it.
- **Sticky sessions = the By site mode.** §6.8's "flows to the same destination stay on the same member for a set time" is met by consistent hashing *without* a time limit: a site stays on its member as long as that member is usable. mihomo's `sticky-sessions` strategy was considered and **not used**: it keys on *source* address + destination and remembers the choice in an in-engine cache for a fixed time that LEO can't configure; on one PC every flow comes from the same source (the TUN address), so it reduces to "by site, forgotten after a while", and it loses its memory on every config reload. Consistent hashing gives the same stickiness, deterministically. (`sticky-sessions` would only matter for traffic from several source addresses, i.e. LAN sharing, which LEO doesn't do: `allow-lan: false`.)
- **No per-bond sticky time** is shown, because By site has none; the card explains "Each site stays on one network while that network works".
- **UDP / QUIC:** handled by the same group; a QUIC connection is one flow and stays on one member. DNS is never spread (§22.5).

### 22.3 Health checks and member loss

**Decisions:**
- **The engine checks members itself** (needed because membership can't be changed live, see the dev-PC findings): every bond group gets `url` = the first probe target from Settings that expects an HTTP status (default `http://cp.cloudflare.com/generate_204`, `expected-status: 204`; already one of LEO's probe targets, §5.7), `interval: 10`, `timeout: 3000` (*as built: 5000, §23*), `max-failed-times: 2` (failed dials trigger an early check), `lazy: true` (no checks while the bond carries nothing). A test asserts that **every** generated load-balance / url-test group has an explicit `url`, so the engine's Google default is never used. Each check goes out bound to that member (its `direct` outbound), so a captive portal (redirect instead of 204), a phone with no upstream, or a VPN kill switch blocking that network (§20.4) all make the member "not alive" and the engine skips it.
- **LEO's own health stays in charge** of the decisions LEO already makes: when LEO's probe moves a member into or out of a failing category (or `BlockedByVpn`), LEO asks the engine to re-check that bond at once (engine API group delay test with the same URL; exact endpoint verified in the build), so the engine's view catches up in about a second instead of up to 10 s. `Degraded` members stay in (they work, just slower; Fastest avoids them on its own).
- **No usable member:** every bond-targeted rule goes through a select group `to-bond<id>-fb` = [`bond-<id>`, `LEO-PRIMARY`] or `to-bond<id>-block` = [`bond-<id>`, `REJECT`], **switched live** by LEO (same mechanism as M3's `to-<adapter>-…` groups) when LEO's health says no member is usable. The engine's own "all members dead" behaviour (`emptyFallback`) is therefore never relied on; what it does is still checked in the build.
- **Connections on a lost member** can't move to another network (no remote end, §15). LEO closes the engine's open connections whose chain went through that member *and* a bond (engine API `DELETE /connections/{id}`, as in §20.3, in memory only), so apps reconnect at once over the remaining members instead of waiting for TCP time-outs. Connections on members that keep working are left alone. *As built: only when the member is really gone (disconnected, or demoted by failover after failing for 6 s), not on one failed probe, §23.*
- **A member coming back** rejoins on the next reload (adapter up) or the next health check (it stayed connected). In By site, about 1/N of the sites move back to it for **new** connections; open ones stay where they are.

### 22.4 How bonds fit with failover, profiles, rules, VPNs and `LEO-UNRULED`

**Decisions:**
- **Failover and Primary are unchanged.** `LEO-PRIMARY` stays a select group of physical networks, failover re-points it, interface metrics never change for bonding. Primary still decides: traffic that bypasses the engine (local ranges, binding VPN clients, IPv6), the default DNS, LEO's own probes, and where a bond's rules go when the whole bond is down ("use Primary").
- **Rules:** a bond is one more Route Target in the Apps and URLs & IPs pickers ("Fast downloads · bond"). Precedence is unchanged (App + destination > destination > app > Primary, §18); a rule's target being a bond changes nothing about which rule wins. "Test a rule" says "Uses: bond 'Fast downloads' (By site: WiFi + Phone (USB))"; the network of one connection is only known once it exists (connections view).
- **Everything else through a bond:** a new machine-wide setting **"Everything else uses: Primary | <bond>"** (default Primary). `LEO-UNRULED` becomes [`vpn-<id>`…, `bond-<id>`…, `LEO-PRIMARY`] and stays live-switched. What wins for unruled traffic: **a VPN that takes the default route (not skipped, §20.3) > the chosen bond > Primary.** The VPN wins because the user turned it on; with "Let unruled traffic skip this VPN" the bond gets unruled traffic again. When the bond has no usable member, `LEO-UNRULED` points at `LEO-PRIMARY`. With this setting the engine runs even with no rules (today it runs only while a rule is active).
- **VPNs:** rules aimed at a bond bypass a VPN that takes the default route (they are physical-network rules, §20.3). Under a kill switch that blocks bypass (§20.4) the bond's members fail their engine health check and the rules fall to their "if unavailable" choice; the M6 banner already explains why. The M6 fail-closed firewall rules (VPN-targeted Block rules only) don't apply to bonds.
- **Profiles:** bonds and "Everything else uses" are machine-wide and stay as they are across profile switches; only the rules that point at bonds switch (§19). A bond referenced by no active rule and not chosen for unruled traffic is **not written into the engine config** (no idle health checks).
- **Stats and connections view:** engine chains for bonded connections look like `net-<id> ← bond-<id> ← to-bond<id>-fb`; `StatsMath` keeps attributing bytes to the leaf outbound (the network that really carried them), so per-app per-network stats stay correct. The connections view shows "→ Phone (USB) via bond 'Fast downloads'". The bond card shows each member's live share of the bond's current traffic, computed from engine connections in memory (not stored; stats.db keeps daily totals per network as today, §19).

### 22.5 DNS, IPv6, safety and privacy

- **DNS for bond-targeted domains:** resolved through **one** member, deterministically: the Primary if it is a usable member, else the first usable member in the bond's order (`nameserver-policy` bound to that member's outbound, §18). DNS is not spread: a CDN answer then fits at least that member; with members on different ISPs the others may get a less ideal CDN node (documented). Unruled-through-bond keeps the Primary's DNS (or the VPN's, §20.3).
- **IPv6:** the engine runs with IPv6 off (§18, §20.7), so bonding is **IPv4 only**; IPv6 follows Windows' routes (Primary). Shown on the bond card while a member has IPv6.
- **Safety (§5.9):** bonding is engine config only. No routes, metrics, DNS settings or firewall rules are added for it. Engine stop or crash → plain Windows networking on Primary, as before. Bond edits, member up/down and profile switches that change rules reload the engine config (the reload path used since M4); member health, "no usable member" and `LEO-UNRULED` changes are live.
- **Privacy:** the service log records bond ids, member adapter ids, mode, counts and verdicts ("bond b1: 1 of 2 usable"), never destinations, the sites that were hashed, or which app used which member. Member shares and connection closing use engine connection data in memory only. The engine stays at warning level and per-connection lines are dropped (§17).

### 22.6 What bonding can't do: one connection = one network

- Bonding spreads **connections**. A **single TCP connection** (one large file over one HTTP connection, a video call, a game session, a VPN tunnel) always runs over **one** member at that member's speed. Splitting one connection across networks needs a **remote end** that reassembles it and talks to the website with one address. That is the future idea in §15 (the user's own second PC, never a hosted server) and is **not part of M7**.
- What gains: browsers loading many sites, torrents (many peers), download managers and launchers with parallel connections (Every connection mode for one server), several apps at once.
- No seamless handover either: a connection on a member that drops is lost and reconnected by the app (§22.3); only §15's remote end could keep it alive.
- The Bonding page says this in one sentence under the mode picker, and neither "Test a rule" nor the bond card ever promises a combined speed for one download.

### 22.7 Implementation plan (for the M7 build session)

**Core (`src/LEO.Core`):**
- `SplitTunnel/Bond.cs` (new): `Bond { Id, Name, Members: [{ AdapterId, Mac, Weight }], Mode (BySite | Weighted | EveryConnection | Fastest), OnAllDown }`; validation (2–8 physical members, weights 1–5, unique name, no VPN members); `UsableMembers(bond, adapters)`; same-router detection (gateway MAC) for the card hint.
- `SplitTunnel/AppRule.cs`: `SplitTunnelSettings.Bonds` and `UnruledBondId` (optional). Rule targets: `TargetAdapterId = "bond:<id>"` (adapter ids are GUIDs, no clash) with a `RuleTarget.IsBond / BondId` helper, so existing rule code keeps one target field. `RuleTargets.Check` refuses VPN client → bond; default "if unavailable" for bond targets = use Primary.
- `SplitTunnel/EnginePlan.cs`: `EngineBond` input; one `bond-<id>` group per *referenced* bond (`load-balance` + strategy, or `url-test`; members = connected members' outbounds, repeated by weight for Weighted; health-check fields from the probe target); `to-bond<id>-fb|-block` select groups with live selection; `LEO-UNRULED` gains bond members and the selection order VPN > bond > Primary; DNS policy for bond domains (§22.5); `StructureKey` includes the bond groups. Engine run condition: active rules **or** an unruled bond.
- `SplitTunnel/DestinationMatcher.cs` (`Decide`, "test a rule"): bond targets described with mode and members.
- `Profiles/ProfileOps.cs`: unchanged for bonds (machine-wide: `Bonds` and `UnruledBondId` are not captured); a rule (in any profile) pointing at a deleted bond is shown "Not active (its bond was removed)", like combos without their app (§18).
- `Stats/StatsMath.cs`: leaf-outbound attribution already holds; `ConnectionDescriber` names the bond from the chain.

**Service (`src/LEO.Service`):**
- `SplitTunnel/SplitTunnelManager.cs`: save/delete/move bonds, set the unruled bond, validation, live `to-bond…` selections from LEO health, closing connections of a lost member, engine re-check on LEO health changes of a member, "Now:" lines ("Now: bond 'Fast downloads' · WiFi + Phone (USB)" / "Now: WiFi (Phone (USB) is unavailable)" / "Now: Primary (no network in the bond is available)").
- `SplitTunnel/MihomoEngine.cs`: `CheckGroupAsync(group, url, timeout)` (group delay test); read per-member alive state for the bond card.
- `Networking/NetworkManager.cs`: nothing new beyond exposing gateway MAC and metered state it already knows.

**IPC (`src/LEO.Ipc`), protocol 9 → 10:**
- `BondInfo` (id, name, mode, members with name / usable / alive in engine / weight / live share, all-down choice, metered tag, hints: same router, IPv6 not bonded) in `SplitTunnelInfo.Bonds`; `SplitTunnelInfo.UnruledVia` gains the bond case.
- `SaveBondAsync`, `DeleteBondAsync`, `MoveBondAsync`, `SetUnruledBondAsync(bondId | null)`; notification kind `BondMemberLost` (rate-limited, once per member per hour).

**UI (`src/LEO.App`):**
- New **Bonding** page (nav item after URLs & IPs): explanation line including the one-connection limit; "Everything else uses" picker (Primary / bonds); bond cards (members with state and share, mode, hints, Edit / Delete / move); **New bond** dialog (name, member checkboxes, weights shown only for Weighted, mode with one-line explanations, if unavailable), validated before it closes. Every action from Click handlers, one ContentDialog at a time.
- Apps and URLs & IPs: pickers list networks, then VPNs (M6), then bonds ("Fast downloads · bond"). Status text "Everything else goes through bond 'X'".
- Stats: connections view wording above; no new stats page.
- Tray tooltip: "· everything else through bond 'X'" when set.

### 22.8 Open questions (decided by the user, 2026-11-23)

1. **Default mode for a new bond:** By site (logins safe; one site's download stays on one network) or Every connection (fastest for single-server downloads; sites may see two addresses)? **Decided: By site**, so a site keeps seeing the same address and accounts don't get flagged. Every connection stays one click away with its warning.
2. **Bonds per profile or machine-wide?** **Decided: machine-wide** (§22.1): bonds and "Everything else uses" don't change on a profile switch; only rules are per profile.
3. **Metered networks and the live speed test:** **Decided: no LEO limits.** LEO doesn't cap or restrict metered networks; it only reads Windows' metered flag and leaves what it means to Windows (Windows uses it for its own background downloads such as updates, not to block apps). The live speed test may use the phone's mobile data, no cap.
4. **Standby members** (a network inside a bond used only while the others are down): **Decided: not built.** Without it, a member that drops is still handled automatically: the bond keeps going on the remaining members and Primary failover works as before (§22.3); nobody has to switch by hand.
5. **Engine health checks** (one small HTTP request per member every 10 s while a bond is in use, `lazy`): **Decided: accepted.**

### 22.9 Test plan

**Unit tests (`tests/LEO.Tests`):**
- Validation: 2–8 members, physical only (VPN refused), weights 1–5, unique names; VPN client → bond refused; bond target default "if unavailable" = use Primary.
- `EnginePlan` with bonds: one `bond-<id>` per *referenced* bond only; mode → type/strategy (By site / Weighted = consistent-hashing, Every connection = round-robin, Fastest = url-test with tolerance); Weighted repeats members by weight; only connected members listed; **every load-balance / url-test group has an explicit `url`** and `expected-status` from Settings' probe target (never the engine default); `to-bond…-fb/-block` groups and their live selection (0 usable → `LEO-PRIMARY` / `REJECT`); `LEO-UNRULED` members and selection for: no bond, bond, bond + VPN taking default (VPN wins), VPN skipped (bond wins), bond with no usable member (Primary); `StructureKey` changes on bond edits and member up/down but not on health; engine runs with no rules when an unruled bond is set; DNS policy for bond domains (Primary member first, else first usable).
- Real engine config check (`mihomo -t`, skipped without `artifacts\engine`) on a config with all four modes, weights, wrappers, `LEO-UNRULED` with a VPN and a bond, guards and destination rules.
- Profiles: a switch leaves bonds and the unruled choice unchanged; a rule in an inactive profile pointing at a deleted bond → "Not active" after switching to it.
- Stats / connections: chain `net-x ← bond-y ← to-bondy-fb` attributes bytes to `net-x`; the describer names the bond; member shares from connection deltas.
- Card hint: same router (same gateway MAC); metered members only tagged, never limited.
- Privacy: log lines from bond handling contain ids and counts only (log-capture test as in M4/M6).

**Live on the dev PC (WiFi = Primary, phone tether plugged in; distribution measured with adapter byte counters as in M3–M5; small downloads):**
- **By site:** rule `curl.exe → bond (WiFi + Ethernet)`; 12 downloads from 12 different hosts → both counters move; repeating the same 12 → each host lands on the same network as before (stickiness).
- **Every connection:** 8 parallel 5 MB downloads from one host (proof.ovh.net) → split over both networks (roughly 4 / 4).
- **Weighted 3 : 1:** 20 distinct hosts → roughly 3 : 1 by host count (checks that repeated members really weight the hash).
- **Fastest:** all new connections on the lower-latency member; the card agrees with the engine.
- **Single stream:** one 100 MB download → entirely on one member (documents the §22.6 limit).
- **Member loss:** unplug the tether during the Every-connection run → open tether connections closed by LEO, curl's next connections all on WiFi within about 1–10 s, card "Bonding 1 of 2"; replug → both used again. Whole bond down (WiFi off as well) **only with the user's OK**, since it removes the PC's main connection: rules fall to Primary / Block.
- **Everything else through the bond:** unruled downloads from several hosts spread; the engine starts with no rules; set back to Primary → the engine stops when no rules remain.
- **Profiles:** switching profiles keeps the bond and "Everything else uses"; a profile whose rules use the bond and one whose rules don't → the bond's traffic follows the rules.
- **Engine stop** with a bond active → routes, DNS and metrics identical to the snapshot taken before (as in §21).
- Privacy: service log has bond ids only; no per-connection engine lines.
- **Speed gain:** with the phone on mobile data (allowed, no cap): many-connection downloads (Every connection, one server) vs the same over WiFi alone.
- **Not possible on this PC without extra setup:** a site seeing two public addresses (both networks share one); Tailscale cases (signed out, §21).

---

## 23. M7 as built + live results (2026-11-24)

Built from §22 with the user's decisions (§22.8: By site default, bonds machine-wide, no limits on metered networks, no standby members, engine health checks). IPC protocol **10**; config schema stays **2** (new fields are optional).

**As built:**
- **Core:** `SplitTunnel/Bond.cs`: `Bond` (id, name, members with adapter id + MAC + weight 1–5, mode, if all down), `BondMode` (BySite, Weighted, EveryConnection, Fastest), `BondTargets` (`bond:<id>` in a rule's `TargetAdapterId`), `BondRules` (validation: name, 2–8 physical members, no VPNs, weights clamped, MAC remembered; member resolution by id then MAC; usable members; same-router hint by network key; DNS member). `SplitTunnelSettings.Bonds` + `UnruledBondId` (machine-wide; `ProfileOps` doesn't touch them). `RuleTargets.CheckBond` (VPN client → bond refused). `EnginePlan`: `bonds`, `unruledBondId`, `bondCheck` inputs; one `bond-<id>` group per *referenced* bond with connected members (repeated by weight for Weighted and Every connection); wrapper groups `to-b<id>-fb|-block` switched live; `LEO-UNRULED` = [VPNs…, bond, `LEO-PRIMARY`] with the order VPN > bond > Primary; DNS for bond domains through one member; `BondHealthCheck.From(probe targets)`. `ConnectionDescriber` names bond members ("Ethernet via bond 'X'", never "fallback") and "No rule (everything else uses the bond)".
- **Service:** `SplitTunnelManager`: `SaveBondAsync` / `DeleteBondAsync` (also clears "Everything else") / `MoveBondAsync` / `SetUnruledBondAsync` / `GetBondsLiveAsync`; bond targets validated on rule save; rules pointing at a deleted bond are left out of the engine ("Not active (its bond was removed)"); the engine also runs for "Everything else uses a bond" with no rules; bond "Now:" lines; `WatchBondMembersAsync` (engine re-check when LEO's health of a member changes; close connections + `BondMemberLost` notification only on a real loss). `MihomoEngine.CheckGroupAsync`, `GetAliveAsync`/`ParseAlive`. Stats connections view gets bond names.
- **IPC 10:** `BondInfo`, `BondMemberInfo`, `BondLive`/`BondLiveMember`/`BondsLiveInfo`; `SplitTunnelInfo.Bonds`, `UnruledBondId`, `UnruledBondNow`; `SaveBondAsync`, `DeleteBondAsync`, `MoveBondAsync`, `SetUnruledBondAsync`, `GetBondsLiveAsync`; notification kind `BondMemberLost`.
- **UI:** **Bonding** page (nav after URLs & IPs): explanation incl. the one-connection limit; New bond; "Everything else uses: Primary / bond" menu; bond cards (mode + explanation, each network with weight/metered/state, "Now:", live open connections and bytes per network polled every 2 s while the page is open, rule usage, hints; Edit / up / down / Delete with a confirm dialog). New/Edit dialog (name, mode with explanation, network checkboxes incl. members not plugged in, weights shown for Weighted and Every connection, if no network works) saves through the service while open, so problems show in the dialog. Apps and URLs & IPs pickers list bonds after networks and VPNs ("Fast · bond"); status lines and the tray tooltip say "Everything else goes through bond 'X'". All actions from Click handlers, one ContentDialog at a time.
- **Tests:** 20 new (244 total incl. the opt-in live helper, all green): validation, VPN client refused, MAC resolution and hints, DNS member, mode → engine group type/strategy, explicit `url`/`expected-status`/`timeout: 5000` on every bond group (never the Google default), health-check URL from Settings, weights (Weighted and Every connection, By site ignores them), only referenced bonds, wrapper live switching (one member down, all down, Block, nothing connected) and reload vs live, unruled order (none / bond / VPN wins / VPN skipped / bond down / bond deleted), VPNs never members, bond DNS policy, bonded connection attribution, engine alive parsing, bonds machine-wide across profile switches and old configs, and the real engine's `mihomo -t` on configs with Weighted/Every connection + Fastest bonds, `LEO-UNRULED` with a VPN and a bond, guards and a bond destination rule. Opt-in live helper: `LiveM7Tests` (`LEO_LIVE_M7=show|add|unruled|unruled-off|restart-engine|conns|remove`, `LEO_M7_MEMBERS`, `LEO_M7_MODE`, `LEO_M7_WEIGHTS`).

**Where the build differs from §22 (all found in the live tests below):**
- **Engine check timeout 5 s, not 3 s.** While the phone tether was busy downloading, the engine's checks of it took up to ~2 s and one failed; one failed check makes the engine skip that member until the next check (10 s), so new sites went to the other network. With 3 s, 3 of 10 sites changed network between two passes; with 5 s, 0 of 11.
- **Closing connections only on a real loss.** As first built, one failed LEO probe of the busy tether (`NoInternet` for ~5 s) counted as "lost" and closed 6 working downloads. Now connections are closed (and the notification sent) only when a member disconnects or failover demotes it (failing for 6 s). The engine re-check on any health change stays.
- **Every connection uses the weights too** (round-robin over the repeated list). With a fast and a slow network, an even split makes downloads wait on the slow one (speed results below).
- **By site is sticky while the engine process runs, not across engine restarts.** The engine seeds its site hash per process: across a config reload 17 of 17 sites kept their network, across an engine restart (split tunneling off/on) only 7 of 16 (chance). Restarts happen on service restart, reboot, split tunneling off/on, and when the engine stops because nothing needs it. A site can then come back on the other network, like after any network change. Documented, not worked around.
- **Fastest's "Now:"** says "the fastest of WiFi and Ethernet" instead of "WiFi + Ethernet".

**Verified live on the dev PC (2026-11-24, WiFi = Primary on "Kickstart Alpha", phone USB tether = Ethernet on mobile data; adapter byte counters; test bond "M7 live" = WiFi + Ethernet, rule `curl.exe` → bond, in the active Phone profile):**
- **Public addresses:** the two networks left from different public IPs (WiFi via Cloudflare SIN, the phone via PMO), so this time the phone was on mobile data (unlike §22's note).
- **Engine config:** `bond-<id>` as `load-balance` / `consistent-hashing` with `url: 'http://cp.cloudflare.com/generate_204'`, `expected-status: '204'`, `lazy: true`, wrapper `to-b<id>-fb`, `MATCH,LEO-UNRULED`; the engine's own check reported both members alive. Smoke test (WiFi + disconnected WiFi 2): 3 curl downloads = 3 open connections on WiFi via the bond.
- **By site:** 12 sites × 3 MB, two passes: 10–11 completed sites spread about half and half (e.g. 4 Ethernet / 7 WiFi), and with the 5 s timeout **every site stayed on the same network** in both passes. The engine knew the site names from DNS (`dnsMode normal`, host set), so hashing uses names, not CDN addresses.
- **Every connection:** 8 parallel 5 MB downloads from **one** server: WiFi 21.1 MB, Ethernet 21.0 MB.
- **Weighted 3 : 1:** 21 of 27 sites on WiFi (78 %, expected 75 %), counted from the engine's own connection list over ~45 sites.
- **Fastest:** all 19 sites on WiFi (engine checks 500 ms vs 648 ms for the tether); the engine's own pick agreed.
- **One connection:** a 30 MB download over one connection in Every connection mode: WiFi 31.1 MB, Ethernet 0.0 (the §22.6 limit, as designed).
- **Everything else through the bond:** unruled downloads (a renamed curl no rule covers) from 8 sites: before, all on WiFi (Primary); with "Everything else uses: bond", WiFi 7.0 MB / Ethernet 13.1 MB; "Test a rule" said "No rule → bond 'M7 live' (everything else uses it)".
- **Member loss (happened by itself, twice):** WiFi dropped at 11:46 and 12:03 (LEO's Wi-Fi pin reconnected it to "Kickstart Alpha" within ~2 s); the bond kept going on Ethernet with the notification "WiFi stopped working. The bond keeps going on Ethernet." A sustained tether failure (12:04:25 → demoted 12:04:41) was the only tether "loss" after the fix; single slow probes no longer closed anything. Why WiFi dropped twice under heavy load on that network isn't known (radio level, not routing).
- **Speed (honest result):** 40 × 3 MB from Google's CDN, 8 at a time: **WiFi alone 22.6 Mbit/s; bond Every connection 4 : 1 8.9 Mbit/s** (WiFi 66.9 MB + mobile 16.4 MB in 75 s); an earlier 8 × 15 MB run: WiFi alone 17.5, bond 1 : 1 6.9 Mbit/s. The mobile link managed only ~2–4 Mbit/s (about a tenth of WiFi), so even a 20 % share left the downloads waiting on it. **Bonding pays off when the networks are of similar speed; a much slower network should get weight 1 against 5, or stay out of the bond.** The cards and the mode text say "give a slower network a lower weight". A real speed gain was not shown on this PC with these two links.
- **UI:** the Bonding page showed the bond card (mode text, members with weight and state, "Now:", open connections per network, "1 rule(s) use it", up/down disabled for a single bond); the Edit dialog opened with name, mode + explanation, three networks (WiFi 2 "not connected") with weights, Save/Cancel. (The dialog popup isn't in the UI Automation tree; checked by screenshot.)
- **Privacy:** the service log has bond ids, modes and counts; none of the ~60 test sites appear in it.
- **Afterwards:** test bond and rule removed (`LEO_LIVE_M7=remove`): no bonds, "Everything else" = Primary, the Phone profile's own rules untouched.

**Not exercised live:** "if no network works" with every member down at once (would need WiFi off as well); a same-router hint on real hardware (no two members on one router here); a bond member that is metered per Windows (the tether wasn't flagged metered).

**Found on the way (not M7):** the engine's `config.yaml` in `%ProgramData%\UNTERSTELLEN\LEO\engine\` is readable by `BUILTIN\Users` and contains the engine API secret, so any local user could read connections or switch the engine's routes through its loopback API (since M3). Worth tightening (ACL the engine folder like config.json, §8) as an M8 hardening item.
