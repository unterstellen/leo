# LEO — Multi-Network Manager & Split Tunneling for Windows

| | |
|---|---|
| **App name** | LEO |
| **Publisher (brand)** | UNTERSTELLEN — unterstellen.org |
| **Company** | INFERSTELLEN — inferstellen.com |
| **Code signing** | **None for now (user's decision, 2026-11-24): Preview releases ship unsigned**, and updates are protected by LEO's own update key (§26). A CA certificate (Certum Open Source, or an OV certificate with a cloud key) can be added later without redesign: optional Authenticode steps in the release workflow (§26). SignPath ruled out 2026-11-22; Azure Artifact Signing not available (individuals in the USA/Canada only) |
| **Repository** | https://github.com/unterstellen/leo (public) |
| **Releases / updates** | https://github.com/unterstellen/leo/releases |
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
| Branding | No custom brand colors: follow Windows 11 (system accent, light/dark). **Logo** (new, 2026-11-24): a gold-to-orange circle with a dark curved band across its upper left (`assets/leo.svg`, the user's design); it replaced the earlier yellow face with dark glasses |
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
│   • Starts at login via HKLM Run from the MSI (per-user switch in ui.json, §24.2)    │
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
- The service checks the client's PID → image path (inside LEO's install folder) → Authenticode signature (same signer as the service itself, compared by subject + CA, §24.3). Clients that fail get **read-only** access, not a closed connection. Off when the service is unsigned (dev builds) or with `HKLM\SOFTWARE\UNTERSTELLEN\LEO\AllowUnsignedClients=1` (built in M8, §25).
- **Honest limit (M8):** the signature check stops other programs from *pretending* to be LEO. It doesn't stop a program running as the same user from tampering with a running `LEO.exe` (same integrity level). The real boundary stays the pipe ACL plus the per-user access level (admins change, standard users read).
- The UI checks the pipe's server (session 0, `LEO.Service.exe` in the install folder) before trusting it, and the service creates its first instance with `FirstPipeInstance` (M8).
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
- **No telemetry. No accounts.** LEO's only network calls of its own are the update check to **github.com** (the `releases/latest` redirect and `releases.atom`, no `api.github.com`, §24.4), the internet health probes (§5.7) and the engine's health checks for bonds (§22). `PRIVACY.md` lists them for users.

---

## 9. Distribution
- **MSI** built with the WiX Toolset. MSIX isn't used because it can't install the Wintun driver adapter or a LocalSystem service cleanly.
- Installer: installs the service, Wintun, the UI and Start menu shortcuts, with the option "Start LEO at login".
- Everything Authenticode-signed (with timestamp) using the certificate chosen at M8 (§16.1), in the GitHub Actions release workflow. Local dev builds stay unsigned (Smart App Control off on the dev PC). **Changed 2026-11-24 (§26):** until a certificate is bought, releases ship unsigned and the updater checks LEO's own update signature instead.
- **Hosting: GitHub Releases** at https://github.com/unterstellen/leo/releases (public repo). Each release has the signed `LEO-<version>-x64.msi` attached. Tags look like `v0.2.0`.
- **Auto-update** (built in M8, §24.4, §25): once a day the service reads `https://github.com/unterstellen/leo/releases/latest` **without following the redirect** (the Location names the tag; not the rate-limited REST API), compares it with its own version, downloads `LEO-<x.y.z>-x64.msi` into `ProgramData\…\updates\` (admins only), **verifies the Authenticode signature, the signer (same publisher as the running service), the MSI's UpgradeCode/ProductName/ProductVersion and SHA256SUMS**, then runs `msiexec /qn` on its own (silent automatic updates, user's decision). Settings → Updates can switch automatic installing or checking off. The Canary channel reads `releases.atom`, which lists pre-releases.
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
leo/
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
| **M8** | Ship (**built**, §24, §25; unsigned Preview + own update key, §26) | Self-contained builds (bundled .NET runtime, no prerequisites), WiX MSI, code signing in GitHub Actions (§16.1), GitHub Releases auto-update, uninstall cleanup, public beta on more hardware (§16), hardening, docs |
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
- **CI:** `.github/workflows/build.yml` builds the solution (Release) and runs the tests on GitHub-hosted `windows-latest` for every push to canary (since 2026-11-24 only canary: preview and main get canary's code through pull requests, which used to repeat the same build up to four times; Run workflow builds any branch by hand). Tests run through `LEO.slnx` so the x64 platform mapping (`bin\x64\Release`) matches the build. Live tests stay opt-in (`LEO_LIVE_TESTS`) and never run in CI.
- `LEO.Service.exe` with no arguments runs as a console app when started from a terminal. Logs: `%ProgramData%\UNTERSTELLEN\LEO\logs\` (service), `%LocalAppData%\UNTERSTELLEN\LEO\logs\` (UI connection errors).
- Optional test switches: `LEO_LIVE_TESTS=1` runs read-only checks against the installed service; `LEO_DUMP=<file.dmp>` prints managed stacks and faulted tasks from a service dump (ClrMD, dev-only).

### UI rules (the user's, don't regress these)
- Every action comes from a Click handler (never from a two-way binding changing); one ContentDialog at a time; no hidden time limits (anything that can't be clicked right now is shown disabled); no em dashes in UI text.
- **Card and list-row layout (2026-11-24):** main information on top (name, details, "Now:" line), **checkboxes on their own row below it**, **buttons on the last row**. Applied to the Apps, URLs & IPs and Networks cards and the Wi-Fi networks dialog rows. Toggles in Settings have no On/Off text and sit at the card's right padding.

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
2. **Branding**: follow Windows 11, with no custom colors. Logo (replaced 2026-11-24 with the user's design) = a gold-to-orange circle (radial gradient) with a dark curved band across its upper left. Source `assets/leo.svg`; `tools/make-icon.ps1` reads that file directly (circle, band path and both gradients) and renders `src/LEO.App/Assets/leo.ico` (16–256 px) plus `leo-32/64/256.png`, so a changed SVG only needs the script re-run. The same image at every size (the shape stays legible at 16 px). One icon works on both light and dark taskbars.
3. **Update hosting**: GitHub Releases on the public repo https://github.com/unterstellen/leo.
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
| Signing | A trusted certificate signs MSI + all binaries in the release workflow (option chosen at M8, §16.1). Unsigned builds are **blocked by Smart App Control** (on by default on many fresh Windows 11 PCs) and warned about by SmartScreen | Unsigned Preview for now, own update key (§26) |
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

Rules regardless of option: release builds are made and signed in GitHub Actions (never on a dev PC); LEO's own binaries are signed, plus third-party libraries that arrive unsigned (decided 2026-11-24, §24.3); Wintun, Microsoft runtime files and other already-signed files keep their upstream signatures; the installer announces system changes (service; from M3 a virtual network adapter) and a real uninstaller exists.

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

---

## 24. M8 design: ship (2026-11-24)

Design written before the build (no code yet). Covers §12's M8 row and the open items of §16: self-contained builds, the WiX MSI, code signing in GitHub Actions (§16.1), GitHub Releases + the in-app updater (§9), uninstall cleanup, hardening, Preview releases tested on more hardware, and docs. Rules carried over: **no hosted servers, no telemetry, no accounts**; release builds are signed in GitHub Actions only, never on a dev PC; the user commits, pushes and tags.

**What the dev PC shows (read-only checks, 2026-11-24, service running, engine running for the Phone profile's rules):**
- **Builds are framework-dependent.** `src\LEO.App\bin\{Debug,Release}\…\win-x64` (406 files, ~164 MB) already carries the **Windows App SDK self-contained** (`WindowsAppSDKSelfContained=true`, `WindowsPackageType=None`: `Microsoft.WindowsAppRuntime.dll` is in the folder) but **no .NET runtime** (no `coreclr.dll`; `LEO.runtimeconfig.json` asks for `Microsoft.NETCore.App` + `Microsoft.WindowsDesktop.App` 10.0.0). The service build is the same: no runtime in the folder, and its `runtimeconfig.json` also asks for **`Microsoft.WindowsDesktop.App`**, which a service shouldn't need (cause to be found in the build: no `UseWPF`/`UseWindowsForms` in `LEO.Service.csproj`). The dev PC has .NET 8/9/10 runtimes installed, so this never showed.
- **No publish step, no installer project, no release workflow.** `.github/workflows/build.yml` only builds (Release) and tests on `windows-latest` for pushes to canary/main/preview and PRs (`contents: read`). `Directory.Build.props` has `Version 0.1.0`, `RuntimeIdentifier win-x64`, `SupportedOSPlatformVersion 10.0.22000.0`. No `installer/` folder yet (§11 plans one).
- **Installed service layout (from `tools\dev-service.ps1 deploy`):** `C:\Program Files\UNTERSTELLEN\LEO\Service\` = robocopy `/MIR` of the Debug build (51 files, 90.8 MB, of which `engine\mihomo.exe` 58.4 MB and `Microsoft.Windows.SDK.NET.dll` 25.1 MB) plus **13 satellite-resource folders** (`cs`, `de`, … `zh-Hant`) although LEO is English only. Service `LEO` ("LEO Network Engine"), `LocalSystem`, `Auto`, binPath quoted, recovery restart 5 s / 10 s / 30 s with a 1-day reset, registered by `LEO.Service.exe install --start` (`sc.exe create/config/description/failure/failureflag`). Service SDDL grants interactive users query/start-level rights only (`IU: CCLCSWLOCRRC`).
- **Signatures:** `LEO.Service.exe` and `engine\mihomo.exe` are **not signed** (dev build; mihomo is the upstream release binary from `tools\get-engine.ps1`, v1.19.34, zip SHA-256 `CD6738D4…AB443A79`, also in `artifacts\engine\` with `version.txt`). Smart App Control is off on this PC (§12a).
- **What `tools\dev-service.ps1` does today:** `deploy` = fetch the pinned engine (no-op when present) → `dotnet build` LEO.Service (Debug, as the user) → one elevated step: stop the service, robocopy the build to Program Files, copy `mihomo.exe` to `Service\engine\`, `LEO.Service.exe install --start`. `uninstall` = `LEO.Service.exe uninstall`: stop (metrics handed back, §5.9), `sc delete`, then remove LEO's own firewall group **"LEO (VPN only)"** (§21); files and ProgramData stay. `restart`, `logs`. The script says "The MSI (M8) replaces this".
- **ACLs:** Program Files folders inherit the normal Program Files ACL (TrustedInstaller/SYSTEM/Administrators full, `BUILTIN\Users` read+execute). `C:\ProgramData\UNTERSTELLEN\LEO` has a **protected** ACL set by `MachineDataSecurity.Apply()` (SYSTEM + Administrators full, Users read+execute, inherited by everything below). Therefore **`engine\config.yaml` (2 KB, contains `secret: …` for the engine API on a random 127.0.0.1 port), `engine\cache.db`, `engine\rules\`, `config.json`, `stats.db` and `logs\` are all readable by `BUILTIN\Users`** (the M7 finding, §23). `MachineDataSecurity` only sets the DACL; it doesn't check the folder's **owner**, and `C:\ProgramData` lets standard users create folders, so a user could pre-create `C:\ProgramData\UNTERSTELLEN` before LEO is installed.
- **Pipe:** `\\.\pipe\LEO.Control` ACL = Network deny, SYSTEM + Administrators full, Interactive read/write (+ the hosting account in console runs). `ControlPipeServer.ServeAsync` identifies the client from its process token and logs pid/user/image, and carries `// TODO(M8): reject clients whose image is not Authenticode-signed` (deferred in M0, §4.2). The UI doesn't check who owns the pipe server.
- **Start at login:** `HKCU\…\Run\LEO` = `"C:\UNTERSTELLEN\Projects\leo-windows\src\LEO.App\bin\Debug\…\LEO.exe" --tray`, written by `StartupManager` in the app (§7).
- **Runtime state an uninstall must cover:** the engine adapter **"LEO" (Meta Tunnel) is Up** right now (the engine creates and removes it; mihomo embeds Wintun, §17); no rules in "LEO (VPN only)" at the moment; config at schema 2 plus `config.json.v1.bak`; daily logs since 2026-11-20.
- **Updater:** not built. `LeoInfo.LatestReleaseApi` still points at `https://api.github.com/repos/unterstellen/leo/releases/latest`, which §17 found rate-limited for anonymous callers on this network.

### 24.1 Self-contained builds

**Decisions:**
- **`dotnet publish -c Release -r win-x64 --self-contained true`** for `LEO.App` and `LEO.Service`; Windows App SDK stays self-contained (already set). Nothing to install first: no .NET runtime, no Windows App Runtime package, no VC++ redistributable (mihomo is a static Go binary; SQLite is Windows' own `winsqlite3.dll`, §19). x64 only (§2).
- **No trimming, no single-file, no Native AOT:** WinUI 3 XAML, StreamJsonRpc and System.Text.Json reflection make trimming risky, and single-file extraction would put code under a temp folder. **ReadyToRun on** for both (faster tray start at login); measured in the build, dropped if it doesn't help.
- **English only on disk too:** `SatelliteResourceLanguages=en` in `Directory.Build.props` (removes the 13 language folders).
- **One install folder:** `C:\Program Files\UNTERSTELLEN\LEO\` holds `LEO.exe` (UI), `LEO.Service.exe` and `engine\mihomo.exe`, publishing both projects into the same folder so the .NET runtime (~70 MB) is shipped once. A CI check fails the build if the two publishes produce the **same file name with different content** (version clash); the fallback is two folders (`App\`, `Service\`) with the runtime twice. The dev layout (`…\LEO\Service\`) is replaced; the MSI removes it (§24.2).
- **Find and drop the service's `Microsoft.WindowsDesktop.App` dependency** if nothing needs it (saves size and a framework the service never uses). If it's needed (e.g. by a Windows SDK projection), it is simply bundled.
- **Version from the tag:** CI passes `-p:Version=<x.y.z>` (from `v<x.y.z>` or `v<x.y.z>-canary.N`); `Directory.Build.props` keeps `0.1.0` for local builds. `LeoInfo.Version` already reads the informational version.
- **Expected size** (to be measured): payload ~250–300 MB uncompressed (runtime ~70, Windows App SDK ~90, mihomo ~58, LEO + libraries ~30); MSI with LZX cabinets roughly 90–120 MB.

### 24.2 The WiX MSI

**Decisions:**
- **WiX Toolset v5 or later, SDK-style project** `installer/LEO.Installer.wixproj` (`WixToolset.Sdk` from NuGet, built with `dotnet build`, so no Visual Studio and nothing installed on the machine). Per-machine, x64, `Program Files\UNTERSTELLEN\LEO`. One fixed **UpgradeCode** forever; a new ProductCode per release. Licence check of the toolset before use (open question 7).
- **One admin prompt:** a per-machine MSI elevates once when the install starts (UAC from Windows Installer); every custom action runs deferred inside that elevated transaction (`Impersonate="no"`), so nothing asks again. A non-admin who can't elevate gets Windows' own message.
- **Launch conditions:** Windows 11 (build ≥ 22000) and x64, else a clear message. A **dev-deployed service** (service `LEO` present but not installed by this MSI, i.e. `…\LEO\Service\LEO.Service.exe`) → the MSI removes it the same way `LEO.Service.exe uninstall` does (stop, delete, firewall group), then installs; config in ProgramData is kept and used.
- **Service:** `ServiceInstall` (name `LEO`, display "LEO Network Engine", description from `LeoInfo`, `LocalSystem`, auto-start, `ErrorControl normal`) + `ServiceControl` (start on install, stop and remove on uninstall, `Wait="yes"`) + recovery actions as today (restart 5 / 10 / 30 s, reset 1 day, also on non-crash failures). The `install`/`uninstall` verbs of `LEO.Service.exe` stay for `dev-service.ps1` only. If the new service doesn't start, Windows Installer rolls the whole install back (old version stays).
- **The engine:** `engine\mihomo.exe` is a normal file component next to the service, **built from source in the release workflow and signed with LEO's certificate** (§17, §24.3). It installs no driver and no adapter: the engine creates the "LEO" Wintun adapter at run time and removes it when it stops (§17). The MSI announces this in its welcome text: "LEO installs a background service and, while split tunneling is on, a virtual network adapter named LEO."
- **Start menu:** one shortcut "LEO" → `LEO.exe` with the app's **AppUserModelID**, so toasts and the taskbar group work for the unpackaged app (checked in the test plan). No desktop shortcut.
- **"Start LEO at login" (checkbox, on by default)** and **"Open LEO now" (checkbox on the last page, on)**. **Decided by the user (2026-11-24):** the MSI writes **`HKLM\…\Run\LEO = "<install>\LEO.exe" --tray`** (removed cleanly on uninstall, works for every user of the PC), and the app's "Start at login" toggle becomes a **per-user switch in `ui.json`** that makes `--tray` exit at once for that user. The installed app then stops writing `HKCU\…\Run` and removes an old `HKCU` `LEO` value (the dev one shown above) the first time it runs from Program Files. Silent installs take `START_AT_LOGIN=0|1`.
- **UI:** a small custom sequence (welcome with the system-change note and "LEO is free software, GPL-3.0-or-later", install folder fixed, the two checkboxes, progress, finish). No licence-acceptance page (the GPL doesn't require acceptance; `LICENSE` is installed next to the binaries).
- **Upgrades:** `MajorUpgrade` scheduled `afterInstallValidate` (old version fully removed, new one installed, in one transaction); **downgrades refused** ("A newer LEO is already installed"). Config, stats and logs in ProgramData are never touched by an upgrade. The UI is closed and restarted through **Restart Manager** (the app calls `RegisterApplicationRestart("--tray")` and closes its window and tray icon when asked), so an update doesn't leave "files in use".
- **Version numbers (Windows' rules + LEO's scheme):** Windows Installer reads `ProductVersion` as `major.minor.build` with **major ≤ 255, minor ≤ 255, build ≤ 65535**, and compares **only these three** (a 4th field is ignored, so two MSIs differing only there don't upgrade). File versions inside the exe/dll have four fields of 0–65535. Nothing else is required by Windows. LEO uses **`MAJOR.MINOR.PATCH`** (semantic versioning): while in Preview `0.MINOR.PATCH` (MINOR up for a release with new features, PATCH up for a fixes-only release), `1.0.0` when the user calls it ready. The first public release is `v0.1.0` (already in `Directory.Build.props`). Canary tags use the next free patch number with a suffix (`v0.2.1-canary.1`), and **every tag's `x.y.z` is higher than every earlier tag's**; a CI check refuses a tag that isn't.
- **File name** `LEO-<x.y.z>-x64.msi` (as §9); a canary suffix appears in the release title only.

### 24.3 Code signing (the user's choice, §16.1)

Windows (Smart App Control, SmartScreen, Defender) needs a signature from a publicly trusted certificate authority. Since 2023 every publicly trusted code-signing key must live in a hardware token or a cloud HSM, so **"signed in GitHub Actions only" means a cloud key that a GitHub-hosted runner can use without a person at a keyboard**. That is the deciding factor, more than the price.

| Option | Cost (check at purchase) | Name in the signature | Signing from GitHub Actions | Notes |
|---|---|---|---|---|
| **Certum Open Source Code Signing** (current favourite) | Low, tens of € per year | "Open Source Developer, <the user's name>" (a personal name, public in every file) | **To verify before buying.** The cloud variant (SimplySign) is built around a desktop app and a one-time code from a phone; CI use needs either a documented remote-signing interface from Certum or keeping the second factor's seed as a GitHub secret (weakens the 2FA, may break Certum's terms). The card-and-reader variant can't be used on GitHub-hosted runners at all (only a self-hosted runner, i.e. a PC of the user's, which the "never on a dev PC" rule excludes) | ID check of a person; no project-popularity rule |
| Azure Trusted Signing (Microsoft may have renamed it; check) | ~$10 per month | INFERSTELLEN or the individual, as verified | **Best:** official GitHub Action, keys in Microsoft's HSM, short-lived certificates, no secrets beyond an Entra app login (OIDC, no stored password) | Eligibility rules for companies (years in business, countries) and individuals (few countries) are strict: check INFERSTELLEN first |
| Classic OV certificate with a cloud HSM (e.g. SSL.com eSigner, DigiCert KeyLocker, Sectigo cloud) | ~€150–500 per year incl. cloud key | INFERSTELLEN | Good: vendor CLI or PKCS#11 in CI with an API credential stored as a GitHub secret | Company validation of INFERSTELLEN |
| Microsoft Store | Free | Microsoft | n/a | Ruled out for M8: MSIX can't install the service/driver path cleanly (§9) |
| SignPath Foundation | Free | SignPath | n/a | **Ruled out (2026-11-22)** |
| Unsigned | Free | none | n/a | Smart App Control blocks it: not shippable (§16) |

- **Recommendation:** buy nothing until the chosen CA confirms in writing how its key is used from a GitHub-hosted runner. If Certum can do that without weakening 2FA, take Certum (cheapest, fits an individual OSS developer). If not, Azure Trusted Signing if INFERSTELLEN (or the user) is eligible; else an OV certificate with a cloud HSM.
- **No option gives instant SmartScreen reputation** (Microsoft removed the EV fast-track in 2024): the first releases may show "Windows protected your PC" until enough downloads build reputation. The README says so, and each release is submitted to Microsoft's false-positive portal (§16).
- **What gets signed (with an RFC 3161 timestamp):** LEO's own binaries (`LEO.exe`, `LEO.dll`, `LEO.Service.exe`, `LEO.Service.dll`, `LEO.Core.dll`, `LEO.Ipc.dll`), `mihomo.exe` (built in CI), and the MSI. Microsoft runtime files keep Microsoft's signatures; Wintun (embedded in mihomo) keeps its own. **Change to §16.1 (user's decision, 2026-11-24):** a CI step lists every PE file in the payload, and **third-party library DLLs that arrive unsigned** (e.g. from NuGet) are signed with LEO's certificate too, because Smart App Control also checks DLLs and would otherwise block them.
- **Same signer everywhere:** the pipe-client check (§24.6) and the updater (§24.4) compare signers by **subject (O/CN) plus the issuing CA chain**, not by certificate thumbprint, so a yearly renewal keeps working. A change of CA or of the name in the certificate is a deliberate event: that release's notes say so, and the updater accepts it only if the new MSI is also signed by the old identity (dual signature `/as`) for one transition release.
- **Secrets** live in a GitHub **environment `release`** with the user as **required reviewer**, so the signing job waits for the user's click in GitHub even after a tag push. Forks and PR builds never see them.

### 24.4 GitHub Releases and the in-app updater (§9)

**Decisions:**
- **Release workflow** `.github/workflows/release.yml`, triggered only by a pushed tag `v*` (the user tags; nobody else can push to the repo):
  1. Check the tag (`x.y.z` higher than every earlier release, §24.2).
  2. **Build the engine from source** (§17): mihomo at the pinned tag (v1.19.34 today, later bumps are their own PRs), Go version, build tags and flags copied from upstream's release workflow for the `windows-amd64-compatible` asset at that tag, plus `-trimpath`; the job records `mihomo -v` and the binary's SHA-256. Also packs the **corresponding source** (`git archive` of the tag + `go mod vendor`) as `mihomo-<ver>-source.tar.gz`, because LEO distributes a GPL binary (§5.3).
  3. Restore, test (`dotnet test`, now **with** the CI-built engine, so `EngineConfigCheckTests` (`mihomo -t`) run instead of being skipped).
  4. Publish self-contained (§24.1), payload checks (no clashing files, every PE listed).
  5. **Sign** (environment `release`, waits for the user's approval): payload binaries, then build the MSI from the signed files, then sign the MSI; `signtool verify /pa /all` on everything; fail on any unsigned PE.
  6. Create a **draft** GitHub Release (pre-release when the tag has a suffix) with `LEO-<x.y.z>-x64.msi`, `SHA256SUMS.txt`, `mihomo-<ver>-source.tar.gz`, and release notes from `CHANGELOG.md`. **The user publishes the draft by hand.**
- **Update check without the API** (the §17 finding: anonymous `api.github.com` was rate-limited here):
  - **Stable:** `GET https://github.com/unterstellen/leo/releases/latest` **without following the redirect**; the `Location` header ends in `/releases/tag/v<x.y.z>`. This is the web endpoint, not the REST API.
  - **Canary (pre-releases):** `https://github.com/unterstellen/leo/releases.atom`, the public Atom feed, which lists pre-releases too; the newest entry with a higher version wins.
  - **Download:** `https://github.com/unterstellen/leo/releases/download/v<tag>/LEO-<x.y.z>-x64.msi` (redirects to GitHub's file host). The file name is fixed by convention, so no API call is needed.
  - `LeoInfo.LatestReleaseApi` is replaced by these URLs; §8's "only network calls" line changes to "github.com (update check), plus the internet checks". User-Agent `LEO/<version>` with nothing that identifies the PC.
- **When:** once at service start (delayed a few minutes) and then **once a day** with a random offset, plus "Check now" in Settings. Updates can be switched off ("Check for updates automatically"). The engine/TUN treats the service's own traffic like any unruled traffic (Primary, or a VPN that takes the default route), which is fine for an update.
- **Who installs:** the **service** (SYSTEM) downloads, verifies and runs the MSI; the UI only shows and asks. This is what lets a standard user's UI show "Update available" without an admin prompt, while **only admins (Full access) can start an install** (same access rule as every change, §12a M1); standard users see "Ask an administrator to update LEO".
- **Silent, automatic updates (user's decision, 2026-11-24):** by default the service checks once a day, downloads, verifies and installs a new version **on its own**, with no click. Afterwards a toast says "LEO was updated to <ver>" with a link to the release notes. Settings → Updates can turn automatic installing off (then a toast with **Update now** appears instead) or turn checking off. While the update installs, the service and engine restart, so split tunneling pauses for a few seconds and open connections through the engine reconnect; the toast says so. Safety nets for an update nobody clicked: the release is published only after the user approved signing and tested the draft (§24.10 Q8), the MSI rolls back if the new service doesn't start, and only an MSI signed by the same signer as the running service is ever run.
- **Verification before running anything** (all must pass, else the file is deleted and the UI says why): the download completes into **`%ProgramData%\UNTERSTELLEN\LEO\updates\`** (SYSTEM + Administrators only, never a user's temp folder); `WinVerifyTrust` says the MSI's Authenticode signature is valid with a timestamp; the signer matches the **running service's** signer (§24.3); the MSI's `UpgradeCode` and `ProductName` are LEO's and its `ProductVersion` is higher than the installed one (read with `MsiOpenDatabase`, nothing executed). The SHA-256 from `SHA256SUMS.txt` is compared as an extra check but is not the gate (it comes from the same place as the MSI).
- **Install:** `msiexec /i <file> /qn /norestart /l*v <updates>\install-<ver>.log` started by the service **outside its engine job object** (the job kills children when the service stops, and the MSI stops the service), then the MSI's own upgrade (§24.2) replaces files, restarts the service and Restart Manager restarts the UI. A failed install rolls back to the running version and the next service start shows "Update to <ver> failed" with the log path. The service deletes the MSI after success.
- **Dev and unsigned builds never update themselves:** when the service's own image isn't signed, the updater only reports "Development build: updates are off".

### 24.5 Uninstall cleanup

Windows networking must look as if LEO had never been installed (§5.9, §16), and nothing LEO didn't create is touched.

**Decisions** (a deferred custom action `LEO.Service.exe cleanup`, run as SYSTEM after the service has stopped, on a **full** uninstall only, not on upgrades):
- **Service:** stop (the service's own stop path already stops the engine, hands interface metrics back to automatic and restores DNS, §5.9) and delete (`ServiceControl`).
- **Engine adapter:** after the stop, if an adapter named "LEO" (Meta Tunnel) is still present (left by an engine crash), remove that one device. The **Wintun driver package itself stays**: it is shared with WireGuard, Tailscale and others, and Wintun manages its own versions.
- **Metrics:** for every adapter LEO lists in config as managed, set "Automatic metric" back on if it's still off (covers a service that crashed before its stop path ran).
- **DNS:** any adapter still pointing at the engine resolver `198.18.0.2` gets that entry removed (normally gone with the adapter).
- **Firewall:** delete every rule in LEO's own group **"LEO (VPN only)"** (as `uninstall` does since M6), never another rule.
- **Wi-Fi:** dual link (secondary-station sync, §12a M2) is set back to off on adapters where **LEO** turned it on (`AdapterSettings.DualLink`). Wi-Fi profiles are the user's saved networks (joined through LEO's dialog but owned by Windows) and **stay**; pins are LEO config only.
- **Engine and metrics data:** `ProgramData\…\LEO\engine\` (config with secret, cache, rule files) and `updates\` are always deleted. Stats (`stats.db`) and logs follow the config choice below.
- **Login start:** the HKLM Run value is MSI-owned and removed automatically (machine-wide entry, user's decision, §24.10 Q4).
- **All settings are removed on uninstall (user's decision, 2026-11-24).** A full uninstall deletes `%ProgramData%\UNTERSTELLEN\LEO` completely (config, `.v1.bak`, stats, logs, engine, updates) and the `UNTERSTELLEN` folder if it is empty, plus every user profile's `%LocalAppData%\UNTERSTELLEN\LEO` (`ui.json`, UI logs; the cleanup runs as SYSTEM and walks the user profile folders). **Upgrades never delete anything** (the cleanup runs only when the product is really removed: `REMOVE=ALL AND NOT UPGRADINGPRODUCTCODE`). No `REMOVE_DATA` property and no extra button are needed; Settings → About gets an "Uninstall LEO" link that opens Windows' Installed apps page.
- **Install folder:** removed by Windows Installer; the old dev folder `…\LEO\Service\` is removed on install (§24.2).

### 24.6 Hardening

**Decisions:**
1. **Engine config secret (the M7 finding, §23).** Two layers:
   - The engine folder `ProgramData\…\LEO\engine\` gets its **own protected ACL: SYSTEM + Administrators only** (no `Users` entry), applied by `MachineDataSecurity` on every service start and by the MSI. Standard users can no longer read `config.yaml`, `cache.db` or the rule files. Admins could already do everything, so they keep access.
   - The secret **stays off disk**: the service passes it on the engine's command line (`-secret`, mihomo's flag that overrides the config; verified with `mihomo -h` in the build) and writes no `secret:` line. A SYSTEM process's command line isn't readable by standard users. If the flag doesn't behave as expected, the ACL alone closes the finding.
   - A test pins it: the generated YAML has no secret, and the folder ACL has no Users entry.
2. **Pipe-client Authenticode check (deferred from M0, §4.2).** On each connection, in signed (release) builds:
   - The client's image path must be **inside LEO's install folder** (Program Files, admin-only writable), so a copied `LEO.exe` next to planted DLLs doesn't count;
   - `WinVerifyTrust` must accept the image, and its signer must match the **service's own** signer (§24.3).
   - Clients that fail get **read-only access** (what standard users get today), not a closed connection, so diagnostics keep working; changes are refused with `AccessDenied` and the log line says "unsigned client". (User's decision 2026-11-24: "do whatever is necessary", so this recommendation.)
   - **Developers and live tests:** the check is **off when the service itself is unsigned** (every dev build; logged once), and an admin-only registry value `HKLM\SOFTWARE\UNTERSTELLEN\LEO\AllowUnsignedClients=1` turns it off on a signed install (for the opt-in `LiveM6/M7` helpers).
   - Honest limit, written into §4.2: the signature check stops other programs from **pretending** to be LEO. It doesn't stop a program running as the same user from tampering with a running `LEO.exe` (same integrity level). The real boundary stays the pipe ACL plus the per-user access level (admins change, standard users read).
3. **Pipe squatting (UI side):** before talking to `\\.\pipe\LEO.Control`, the UI checks that the **server** is in session 0 (`GetNamedPipeServerSessionId`) and its image is `LEO.Service.exe` in LEO's install folder; otherwise it shows "LEO's service isn't running" instead of trusting the pipe. The service creates its first instance with `FirstPipeInstance`, so it notices (and logs) a squatter instead of sharing the name.
4. **ProgramData owner check:** the MSI creates `ProgramData\UNTERSTELLEN\LEO` with owner Administrators and the §8 ACL. On every start the service checks that `UNTERSTELLEN` and `LEO` are owned by SYSTEM or Administrators and are not reparse points (junctions/symlinks). If not, it resets the owner and ACL (SYSTEM may), and refuses to write there if that fails. This stops a standard user who pre-created the folder from redirecting SYSTEM's writes.
5. **Engine binary check:** in signed builds the service starts `engine\mihomo.exe` only if it carries LEO's signature (same check as 2).
6. **DLL loading:** both processes call `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_APPLICATION_DIR)` at start; the install folder is admin-only writable.
7. **Updates folder** is SYSTEM + Administrators only (§24.4); downloads are never run from a user-writable path.
8. **Kept as they are, on purpose:** `config.json`, `stats.db` and `logs\` stay readable by standard users. They show the same rules and daily totals in the UI read-only, and logs never hold per-connection data (§17–§19). LocalSystem stays (§4.1); reducing the service's privileges is a later idea, not M8.
- **IPC protocol 10 → 11** (updater calls, §24.7); the client check needs no contract change.

### 24.7 Preview releases and hardware matrix (§16)

**Decisions:**
- **No separate beta (user's decision, 2026-11-24): LEO stays in "Preview" until 1.0.** Every 0.x release is a normal, signed GitHub release titled "LEO 0.x.y (Preview)", and the app's About page says Preview. They are **not** marked "pre-release" on GitHub, because `/releases/latest` (the updater's stable check, §24.4) skips pre-releases. The Canary channel (pre-releases, `releases.atom`) stays for builds the user wants to try on the user's own PCs first; it is off by default. The hardware matrix below is the checklist for the user's other computers and for anyone who reports through the issue forms.
- **Reporting without telemetry:** Settings → About → **"Copy diagnostics"** (Click handler) puts a text report on the clipboard: LEO and Windows version, adapters (kind, driver name + version, chipset), states, which features were refused and why, VPN products detected, recent service log **warnings/errors**. SSIDs, IP and MAC addresses and nicknames are **left out by default** (a checkbox can add them). The tester pastes it into a GitHub issue themselves; LEO sends nothing.
- **Issue forms** (`.github/ISSUE_TEMPLATE/`): bug report (steps, expected/actual, diagnostics), hardware report ("works on my PC": checklist per feature), feature request. **Security issues** go through GitHub's private vulnerability reporting (`SECURITY.md`), so no email address is published.
- **Hardware matrix** (each row: install, inventory, Primary switching, failover, Wi-Fi pin/join, app + destination rules, bonding, VPN coexistence, uninstall):

| Area | Covered so far (dev PC) | Target (user's other PCs, reporters) |
|---|---|---|
| Wi-Fi chipsets | Qualcomm FastConnect 6900 (DBS, secondary STA) | Intel AX2xx/BE2xx, Realtek, MediaTek, a USB Wi-Fi stick |
| Ethernet | none (laptop without a port) | onboard Intel/Realtek, USB-C dongles (Realtek RTL8153/8156), a dock |
| Phone tethering | Android RNDIS | Android NCM, **iPhone** (Apple Mobile Device Ethernet), Bluetooth PAN |
| Cellular | none | a built-in WWAN modem, if a tester has one |
| VPNs | Tailscale (signed out) | WireGuard for Windows (incl. "block untunneled"), OpenVPN, built-in IKEv2, a commercial client with a kill switch, a corporate always-on client (§20.8 gaps) |
| Security software | Defender | Defender with Smart App Control **on** (fresh PC), 2–3 common antivirus suites, third-party firewalls |
| Windows | 11 Pro 26200 | 11 Home, 11 23H2/24H2/25H2, a standard (non-admin) user account, a managed/Intune PC (expected limits documented) |

- **When to call it 1.0** (the user's call; suggested checklist): no open "breaks the network" bug; install/upgrade/uninstall clean on every tested machine; each matrix row working or its limit explained; at least two silent updates delivered without problems; Defender/SmartScreen clean (or false positives reported).

### 24.8 README and docs

- **README.md:** rewrite the status table (it still says split tunneling is "Next (M3)"; M3–M7 are built), add screenshots (Networks, Apps, URLs & IPs, Bonding, Stats, tray), **Install** (download the MSI from Releases, one admin prompt, what it installs, the possible SmartScreen notice for new releases), **Update**, **Uninstall** (what is removed, keep/remove settings), **Limits** (one connection = one network §22.6, ECH/"domain unknown" §5.4, IPv6 not captured §20.7, VPN kill switches §20.4, secondary Wi-Fi stations §12a M2, Windows 11 x64 only), **Safe mode** (Shift at start, stop the service), **Privacy**, **Build from source** (today's section).
- **New files:** `PRIVACY.md` (no telemetry/accounts/servers; the exact list of LEO's own network calls: github.com update check, the internet checks, engine health checks for bonds; what is stored where and for how long, §19), `SECURITY.md`, `CONTRIBUTING.md` (build, tests, the UI rules: Click handlers only, one dialog at a time, no hidden time limits, no em dashes in UI text; DCO vs CLA stays §14's open question), `CHANGELOG.md` (release notes source), `THIRD-PARTY-NOTICES.md` (mihomo GPL-3.0 with the source archive, Wintun licence, .NET / Windows App SDK / StreamJsonRpc / H.NotifyIcon / CommunityToolkit / Newtonsoft and other MIT/Apache notices), `docs/managed-pcs.md` (admin needed, policies that block services or drivers, silent install/uninstall properties).
- **In the app:** Settings → About shows version, channel, "Signed by <signer>" (or "Development build"), licence, links to source code at this version's tag, third-party notices and privacy (GPL: recipients can find the source).
- `LEO.md`: header "Code signing" and §9/§16 updated once the user decides; §4.2 gets the honest limit; §8's network-call line changes (§24.4).

### 24.9 Implementation plan (for the M8 build session)

**Build and packaging:**
- `Directory.Build.props`: `SatelliteResourceLanguages=en`; `PublishReadyToRun`, `SelfContained` for publish; version override from CI.
- `LEO.Service.csproj`: find and remove the `Microsoft.WindowsDesktop.App` reference if unneeded.
- `tools/build-engine.ps1` (new): builds mihomo from source at the pinned tag (used by CI; developers keep `get-engine.ps1`).
- `tools/build-release.ps1` (new): local **unsigned** publish + MSI for testing (never signs; prints "unsigned test build").
- `installer/LEO.Installer.wixproj` + `Package.wxs` (components, service, shortcut with AUMID, HKLM Run, launch conditions, MajorUpgrade, property `START_AT_LOGIN`), `Ui.wxs` (welcome with the system-change note, checkboxes, finish), `CustomActions` calling `LEO.Service.exe cleanup` (full uninstall only) / `remove-dev-install` (deferred, no impersonation).
- `.github/workflows/release.yml` (new, §24.4) with environment `release`; `build.yml` gains a packaging smoke job (publish + unsigned MSI build + payload checks) on PRs.
- `tools/dev-service.ps1`: unchanged for dev, plus a refusal to `deploy` over an MSI-installed LEO ("uninstall the MSI first").

**Core (`src/LEO.Core`):**
- `Updates/ReleaseVersion.cs` (parse `v1.2.3-canary.4`, compare, MSI three-field rule), `Updates/ReleaseFeed.cs` (redirect `Location` and Atom parsing, asset URL), `Updates/UpdatePolicy.cs` (pure: when to check, what to offer, channel, ask vs auto, standard user vs admin).
- `Security/SignerIdentity.cs` (subject + CA chain comparison, renewal tolerant), pure and testable with test certificates.
- `LeoInfo`: release URLs instead of `LatestReleaseApi`; `LeoPaths.Updates`, `LeoPaths.EngineDir`.

**Service (`src/LEO.Service`):**
- `Security/Authenticode.cs` (`WinVerifyTrust` + signer extraction), `ClientTrust` used in `ControlPipeServer.ServeAsync` (replaces the TODO), `AllowUnsignedClients` reading, `FirstPipeInstance`.
- `MachineDataSecurity`: owner/reparse-point check, engine folder SYSTEM + Admins only, updates folder.
- `SplitTunnel/MihomoEngine`: `-secret` on the command line, no secret in YAML, engine signature check before start.
- `Updates/UpdateService.cs` (hosted: schedule, check, download with resume, verify, install outside the job object, result after restart).
- `Program.cs`/`ServiceInstaller`: new verbs `cleanup` (§24.5, removes all data) and `remove-dev-install`, `SetDefaultDllDirectories`.

**IPC (`src/LEO.Ipc`), protocol 10 → 11:** `UpdateInfo` (installed version, channel, signed/signer, latest found, state: idle/checking/available/downloading/verifying/installing/failed + reason, last check time, settings); `GetUpdateInfoAsync`, `CheckForUpdatesAsync`, `InstallUpdateAsync` (Full access), `SetUpdateSettingsAsync` (auto-check, ask/auto, channel); notification kinds `UpdateAvailable`, `UpdateFailed`.

**UI (`src/LEO.App`):**
- Settings → **Updates** (channel, check automatically, install automatically (on), Check now, Update now when automatic installing is off, state text) and **About** (Preview label, signer, licences, source link, Copy diagnostics, "Uninstall LEO" link). All from Click handlers, one ContentDialog at a time, buttons shown disabled while an action runs (no hidden time limits).
- Toast "LEO <ver> is available" with **Update now** / **Later**.
- `StartupManager` per Q4 (per-user switch in `ui.json`, removal of the old HKCU value when running from Program Files).
- Pipe client: server session/image check (§24.6 item 3).
- `RegisterApplicationRestart("--tray")` and a clean close on Restart Manager's request.

**Docs:** §24.8 files; README rewrite.

### 24.10 Open questions and the user's answers (2026-11-24)

1. **Code signing: can't LEO sign itself?** Not for other people's PCs. A self-made ("self-signed") certificate is only trusted on a PC that has imported it; Smart App Control blocks it and SmartScreen treats it like no signature (§12a). Making it trusted would mean the MSI installing LEO's own root certificate into every user's Windows trust store: the installer that does it would itself be unsigned (blocked first), antivirus treats such installers as malware, and whoever holds that key could sign anything and have those PCs trust it. So trust has to come from a certificate authority that Windows already trusts, after it has checked who the publisher is. **What the user does, once:** (a) pick the CA (§24.3); (b) pass its identity check (ID document, for a company the company papers); (c) pay; (d) set up the cloud key the CA provides; (e) put the CA's API credentials into the GitHub environment `release` as secrets. From then on every tagged release is signed in GitHub Actions automatically. Still open: which CA (Certum is asked first how its key works from GitHub-hosted runners, §24.3).
   - **SourceForge:** the signature lives inside the files, so where they are downloaded from doesn't matter; the same signed MSI can be offered on GitHub and SourceForge. SourceForge can mirror a GitHub project's releases automatically (to be set up by the user on SourceForge, nothing in LEO's build). GitHub stays the source of truth.
2. **Updates: decided, silent and automatic by default** (§24.4), GitHub first. SourceForge isn't needed for updates: the updater reads GitHub (the `releases/latest` redirect, no rate-limited API). A SourceForge fallback (used only when GitHub can't be reached) is possible later through SourceForge's public project file listing and download links; it adds no risk because the updater runs only an MSI signed by LEO's own signer, wherever it came from. Not part of M8 unless the user asks.
3. **Uninstall: decided, remove all settings** (§24.5).
4. **Start at login: decided, machine-wide entry written by the installer** (§24.2).
5. **Third-party DLLs: decided (yes, 2026-11-24).** LEO ships libraries written by others. Checked on the dev PC's Release builds (230 exe/dll files): all Microsoft and most other libraries are signed by their authors, but six arrive **unsigned**: `H.NotifyIcon.dll`, `H.NotifyIcon.WinUI.dll`, `H.GeneratedIcons.System.Drawing.dll` (the tray icon library) and `MessagePack.dll`, `MessagePack.Annotations.dll`, `PolyType.dll` (pulled in by the pipe's JSON-RPC library), plus LEO's own six files. Smart App Control checks DLLs too, so an unsigned one can stop the app. Recommendation: the release workflow signs any third-party file that arrives unsigned with LEO's certificate (allowed by their open-source licences; it says "shipped by LEO", not "written by LEO") and leaves already-signed files untouched.
6. **Test machines:** the user has other computers: they cover real hardware (Wi-Fi chips, Ethernet, tethering, VPNs). For install/upgrade/uninstall and Smart App Control on a fresh Windows, Claude can drive a **Hyper-V** VM from this PC with PowerShell Direct (`Invoke-Command -VMName`), resetting it to a clean checkpoint between runs, **if the user enables Hyper-V** (a Windows feature, admin, one reboot) and **provides a Windows 11 ISO** (a download from Microsoft; the user's call). Without that, the install tests run on one of the other computers by hand, with a checklist.
7. **WiX licence:** WiX Toolset is the free, open-source tool that turns LEO's files into an MSI (MS-RL licence); only the tool is used, nothing of it ships inside LEO except Windows Installer's own standard tables. Recent versions added an "Open Source Maintenance Fee" that asks organisations earning money with it to pay the maintainers. LEO earns nothing, so it is expected not to apply; the build session reads the current terms before adding it and asks the user if anything says otherwise (fallback: WiX v5, which predates the fee).
8. **Release gate: decided (yes, keep both clicks, 2026-11-24).** Meaning: after a tag push, GitHub builds everything, then **pauses before using the signing key until the user clicks "Approve"** in GitHub, and creates the release as a **draft the user publishes** after trying the signed MSI on a test PC. Reason: with silent automatic updates, a published release reaches every LEO PC within a day without anyone clicking; the gate makes sure a person looked first, and that nobody who gets into the repo or a workflow can sign something with the user's certificate. Recommendation: keep both clicks.
9. **Unsigned clients: decided, read-only** (§24.6 item 2).
10. **Beta: decided, no separate beta; LEO stays "Preview" until 1.0** (§24.7). Version numbers: §24.2 (`0.MINOR.PATCH`, first public release `v0.1.0`, Windows' MSI limits).

### 24.11 Test plan

**Unit tests (`tests/LEO.Tests`):**
- `ReleaseVersion`: parse/compare (`v0.2.1-canary.1` < `v0.2.2` < `v1.0.0`), MSI three-field rule, tag checks the workflow uses.
- `ReleaseFeed`: redirect `Location` → version (stable), Atom feed with pre-releases (canary), malformed responses ignored, asset URL format.
- `UpdatePolicy`: once a day with offset, "Check now", ask vs auto, channel, standard user can't install, unsigned build never updates, newer-only, no repeat toast for a version the user postponed today.
- `SignerIdentity`: same subject + CA after renewal → match; other subject, other CA, self-signed, expired without timestamp → no match; transition with dual signature.
- `Authenticode` / `ClientTrust` with test files: a signed file inside the install folder → Full/ReadOnly by user as today; same file outside the folder → read-only; unsigned → read-only; check off when the service is unsigned; `AllowUnsignedClients`.
- Engine hardening: generated YAML contains no `secret`; command line carries it; engine folder ACL has no Users entry (applied to a temp folder); owner/reparse check refuses a junction.
- Cleanup planning (pure): which adapters get automatic metric back, which DNS entries are removed, only "LEO (VPN only)" rules, dual link only where LEO set it, all data folders listed for deletion on uninstall and none on upgrade.
- Privacy: update and cleanup log lines contain versions, counts and ids only.

**CI (GitHub Actions):**
- Release workflow on a **test tag in a fork or a dry-run mode** (no signing secrets): engine built from source, `mihomo -t` tests run with it, payload clash check, every PE listed, unsigned MSI produced. With secrets (after the user's approval): `signtool verify /pa /all` passes for every PE and the MSI; draft release has MSI, `SHA256SUMS.txt`, mihomo source archive.

**Installer (clean Windows 11 x64, Smart App Control on; Windows Sandbox or VM per Q6):**
- Fresh install: exactly **one UAC prompt**; service `LEO` running as LocalSystem, auto, recovery actions set; Start menu "LEO" opens the app; toasts appear (AUMID); "Start at login" on/off both work after sign-out/in, per user; no .NET or Windows App Runtime installed on the machine (`dotnet --list-runtimes` absent) and LEO still runs; **Smart App Control doesn't block** any file (signed build only).
- Silent install `msiexec /i … /qn START_AT_LOGIN=0` and silent uninstall.
- Upgrade N → N+1 (MSI and in-app updater): config, stats, rules kept; UI restarted by Restart Manager; downgrade refused; a deliberately broken N+1 (service doesn't start) rolls back to N.
- Uninstall with split tunneling active (engine adapter up, a "LEO (VPN only)" firewall rule present, dual link on, metrics pinned): afterwards no service, no "LEO" adapter, Automatic metric on, no 198.18.0.2 DNS, no LEO firewall rules, dual link off, Wi-Fi profiles still there, routes/DNS/metrics identical to a snapshot taken before install (as in §21), `ProgramData\UNTERSTELLEN\LEO` and every user's `LocalAppData\UNTERSTELLEN\LEO` gone. An upgrade in between keeps all of them.
- Standard user account: install refused without admin; the app shows read-only; "Update available" shows "Ask an administrator".

**Dev PC (WiFi = Primary, phone tether; only after the user removes the dev deploy):**
- Install the MSI over the dev deploy: the dev service is replaced, the dev `…\LEO\Service\` folder removed, the existing config (schema 2, Phone profile and its rules) kept and active, the old HKCU Run value pointing at `bin\Debug` removed (if Q4 = HKLM).
- Networking regression with the installed (signed) build: failover, one app rule, one destination rule, one bond (§22.9 smoke), engine start/stop leaves Windows unchanged.
- Hardening live: as a standard user, `engine\config.yaml` can't be read (and contains no secret); a copy of `LEO.exe` outside Program Files gets read-only access; an unsigned test client is refused changes; the UI refuses a pipe served from session 1.
- Updater live: point a test build at a **test repository's** releases (no change to the real repo): "available" → Update now → verified → installed → UI back; a tampered MSI (one byte changed, or signed by another certificate) is refused with a reason.

**Preview (§24.7):** the matrix rows checked on the user's other PCs and through the issue forms with "Copy diagnostics"; every bug fix re-tested after it arrives through the silent updater.

---

## 25. M8 as built (2026-11-24)

Built from §24 with the user's decisions (§24.10: silent automatic updates, uninstall removes all settings, machine-wide HKLM Run, unsigned clients read-only, no beta: "Preview" until 1.0, unsigned third-party DLLs get signed, release gate with two clicks). Further decisions during the build: **WiX v5** (5.0.2, MS-RL; v6+ adds the Open Source Maintenance Fee and v7 needs an EULA acceptance in every build), downloads of WiX and the Crossgen2 pack from NuGet OK'd. The repository was renamed to **github.com/unterstellen/leo** (all URLs updated). IPC protocol **11**; config schema stays **2** (`Updates` is optional). Nothing installed on the dev PC, nothing signed (the CA is still open).

**As built:**
- **Self-contained builds (§24.1):** `Directory.Build.props`: `SatelliteResourceLanguages=en`; while publishing: self-contained, ReadyToRun, no trimming/single-file. **ReadyToRun only for LEO's own assemblies** (target `LeoReadyToRunOwnAssembliesOnly`). The `Microsoft.WindowsDesktop.App` dependency came from StreamJsonRpc → Microsoft.VisualStudio.Threading.Only (its net8.0-windows asset declares a WPF framework reference), not from LEO: `DisableTransitiveFrameworkReferences` in **both** LEO.Service and LEO.App; both runtimeconfigs now ask only for `Microsoft.NETCore.App`. One install folder `Program Files\UNTERSTELLEN\LEO\`.
- **`tools/build-release.ps1`:** publishes app + service, merges them into `artifacts\release\payload` and **fails on a file name with different content** (found 4 clashes on the first run, see below), adds `engine\mihomo.exe` + `LICENSE.txt`, writes `payload-signatures.txt` (every PE with its Authenticode status), builds `LEO-<x.y.z>-x64.msi` + `SHA256SUMS.txt`. Never signs. `-SkipMsi` / `-MsiOnly` for the release workflow. Checks the Windows Installer version limits.
- **MSI (§24.2):** `installer/LEO.Installer.wixproj` (WiX SDK pinned in `global.json` "msbuild-sdks"), `Package.wxs`, `Ui.wxs`. UpgradeCode `{C17E6E01-40E8-4381-AF33-BCA6DCF81643}` (also `LeoInfo.UpgradeCode`). Per-machine x64, launch conditions (64-bit, `CurrentBuildNumber` ≥ 22000), `MajorUpgrade` afterInstallValidate with "A newer LEO is already installed." Service `LEO` (LocalSystem, auto, `Vital`, start/stop/remove with Wait → a service that doesn't start rolls the install back); recovery 5/10/30 s, 1-day reset, `failureflag 1` set by deferred `sc.exe` actions. Start menu shortcut with `System.AppUserModel.ID = UNTERSTELLEN.LEO`. `START_AT_LOGIN` (default 1, remembered in `HKLM\SOFTWARE\UNTERSTELLEN\LEO\StartAtLogin`) → `HKLM\…\Run\LEO = "<install>\LEO.exe" --tray`. ProgramData folders created with owner Administrators and the §8 SDDL, `engine\` SYSTEM + Administrators only. Deferred SYSTEM actions: `remove-dev-install` after InstallFiles, `cleanup` after DeleteServices on `REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE`. No modify/repair in Installed apps. UI: welcome (system-change note, GPL line, install folder, "Start LEO at login"), progress, finish ("Open LEO now", launched non-elevated from the UI sequence), remove page, plus error / files-in-use (Restart Manager) / cancel / user-exit / fatal dialogs. No em dashes.
- **App side of the installer:** `Program.Main` sets the AppUserModelID, calls `SetDefaultDllDirectories`, `RegisterApplicationRestart("--tray", no crash/hang/reboot)`, and exits a `--tray` start for a user who switched start-at-login off. `SessionEndWatcher` subclasses the main window: WM_ENDSESSION (Restart Manager, sign-out) → clean exit. `StartupManager` + pure `LoginStartRule`: installed copy + HKLM entry → per-user switch in ui.json and the old HKCU value removed; otherwise HKCU as before (dev builds, `START_AT_LOGIN=0`).
- **Uninstall cleanup (§24.5):** pure `Setup/CleanupPlan` (metrics back to automatic only on adapters LEO managed, from the main and every profile's priority list; `198.18.0.2` DNS removed; dual link off only where LEO turned it on; LEO's firewall group; leftover "LEO" adapter; ProgramData + every profile's LocalAppData; nothing on upgrade). `LEO.Service.exe cleanup` (`UninstallCleanup`): kills leftover engines, carries the plan out (WMI + `pnputil /remove-device` for the adapter, `netsh` for DNS, `WlanApi` for dual link), each step best effort, output = counts only (MSI log). `SafeDelete` never follows a junction or symlink and refuses a tree with a reparse point between the trusted base (%ProgramData% or the profile folder) and LEO's folder. `remove-dev-install` (`DevInstallRemover`): a service `LEO` whose exe isn't in this install folder (or is in `…\Service\`) is removed the `uninstall` way, the dev folder deleted, config kept. `tools/dev-service.ps1` refuses `deploy`/`uninstall` over an MSI install.
- **Hardening (§24.6):** `Core/Security/SignerIdentity` (CN + O, issuing CA organisation, root subject; self-signed never matches; `AnyMatches` for a dual signature) and pure `ClientTrustPolicy`. `Service/Security/Authenticode`: `WinVerifyTrust` (revocation from cache only) + signers read through crypt32 (`CryptQueryObject`/`CryptMsgGetParam`, nested signature OID 1.3.6.1.4.1.311.2.4.1), no new NuGet package. `ClientTrust` on every pipe connection: an untrusted Full client is capped to ReadOnly; log line "(unsigned client …)". `FirstPipeInstance` whenever the server has no live instance (a squatter is logged once, retried every 5 s). `MachineDataSecurity`: `UNTERSTELLEN` and `LEO` must be owned by SYSTEM/Administrators and not be reparse points (a planted link is removed, the owner reset); `engine\` and `updates\` SYSTEM + Administrators only; **the service exits (code 1) if the folders can't be made safe**. Engine: `-secret` on the command line, no `secret:` in `config.yaml` (`EnginePlan.ToConfigYaml(controller)`), signature check before start in signed builds. UI: `Ipc/PipeServerCheck` (session 0 + `LEO.Service.exe` inside the install folder). `Core/Security/DllSearch` in both processes.
- **Updater (§24.4):** pure `Core/Updates`: `ReleaseVersion` (tags, canary suffix, MSI three-field rule `UpgradesOver`, `FitsMsi`), `TagRules`, `ReleaseFeed` (redirect Location, Atom, fixed asset URLs, SHA256SUMS parsing), `UpdatePolicy` (first check 5 min after start, then daily + random 0–2 h, channel pick, install/offer/nothing, "Later" silences a version for the day), `UpdateSettings` in config (auto-check on, auto-install on, stable). Service: `Updates/UpdateService` (hosted; no-redirect client for the check, User-Agent `LEO/<version>` only; download with Range resume into `updates\`; `UpdateVerifier`: signature, signer = running service's, UpgradeCode/ProductName/ProductVersion read with `MsiProperties` (msi.dll, read-only), newer, SHA256SUMS as an extra check; `msiexec /i … /qn /norestart /l*v` started outside the engine job; `updates\state.json` keeps last check, pending install and last result, read at the next start). Dev builds: state Off, no request ever.
- **IPC 11:** `UpdateInfo`, `UpdateState`, `UpdateResult`; `GetUpdateInfoAsync`, `CheckForUpdatesAsync` (any user), `InstallUpdateAsync` (Full, runs in the background), `SetUpdateSettingsAsync` (Full), event `UpdateChanged`; notification kinds `UpdateAvailable`, `UpdateFailed`, `Updated` (the last is shown by the UI only).
- **UI (Settings):** **Updates** card (state, signer or "Development build (not signed): updates are off.", last check; switches for check automatically / install automatically / canary builds; Check now, Update now (admins; others see "Ask an administrator to update LEO."), Release notes). **About** card ("LEO 0.1.0 (Preview)", signer, licence text with the Preview line, links: source code of this version's tag, third-party notices, privacy, licence; "Include network names and addresses in diagnostics" checkbox; Copy diagnostics, Uninstall LEO → Installed apps). Toasts: "LEO x is available" with **Update now / Later**; after an update each user is told once "LEO was updated to x" (Release notes button) or "Update to x failed". `Ipc/DiagnosticsReport`: built in the app from IPC data and the last service log warnings/errors; SSIDs, nicknames, IPs and MACs left out (and masked in log lines) unless ticked.
- **CI/release:** `build.yml` gains a **package** job (pinned upstream engine, payload + unsigned MSI as an artifact). `release.yml`: tag check (`tools/check-tag.ps1`, mirrors `TagRules`, outputs the title "LEO x.y.z (Preview)") → mihomo **from source** (`tools/build-engine.ps1`: checkout of the pinned tag, Go from its `go.mod`, `GOAMD64=v1`, `with_gvisor`, `-trimpath`, stripped, version stamped; `git` tree + `go mod vendor` as `mihomo-<tag>-source.tar.gz`) → tests with that engine → payload → **sign** in environment `release` (lists LEO's own + every unsigned PE; the signing step is a **marked placeholder that fails**, so a tag push creates nothing until the CA is chosen) → MSI from the signed payload → `signtool verify /pa /all` on every PE and the MSI → **draft** release (MSI, SHA256SUMS incl. the source archive, mihomo source, notes from `CHANGELOG.md`; pre-release only for canary tags). `workflow_dispatch` = dry run up to an unsigned MSI artifact.
- **Docs:** README rewritten (status, install incl. the SmartScreen note, update, uninstall, "if something goes wrong", limits, privacy, help test LEO, build), `PRIVACY.md`, `SECURITY.md` (GitHub private vulnerability reporting), `CONTRIBUTING.md`, `CHANGELOG.md` (0.1.0 drafted), `THIRD-PARTY-NOTICES.md`, `docs/managed-pcs.md`, issue forms (bug, hardware report, feature, security link), `docs/developing.md` §10. §4.2, §8, §9 and the header above updated.
- **Tests:** 48 new (292 total, all green): signer renewal/other subject/other CA/self-signed/dual signature (test CAs built in memory), trust decisions incl. sibling-folder trick, untrusted admin → read-only through a real pipe server, Authenticode on the runtime's own signed DLL, an unsigned DLL, a tampered copy and a missing file, no secret in YAML and `-secret` on the command line, engine/updates/data folder ACLs, trusted owners, junction detection, `SafeDelete` keeps a junction's target and refuses a junction above LEO's folder, `FirstPipeInstance` refuses a squatted name, pipe server identification and refusal rules, cleanup plan (managed metrics only, DNS, dual link, every user's folder, nothing on upgrade, missing config), login-start rules, dev-service recognition, versions/tags/MSI rule, latest redirect and Atom parsing, asset names and SHA256SUMS, check schedule, channel pick, decide, "Later", old configs get automatic updates, verifier (every refusal reason), the real MSI's properties (when built), updater with a fake GitHub (dev build sends nothing, no redirect followed, User-Agent only, offer once, up to date, failed verification deletes the file and notifies, canary reads the feed), Preview label, diagnostics redaction (times aren't mistaken for addresses).

**Where the build differs from §24 (all found while building):**
- **ReadyToRun strips upstream signatures.** Crossgen rewrites every library it compiles: on the first build `Microsoft.Extensions.*`, StreamJsonRpc, the Windows App SDK projections and WinRT.Runtime all lost their publishers' signatures (93 unsigned PE files). ReadyToRun now covers LEO's own files only; the unsigned list is exactly LEO's six, the six third-party DLLs from §24.10 Q5 (`H.NotifyIcon*`, `H.GeneratedIcons.System.Drawing`, `MessagePack*`, `PolyType`) and `mihomo.exe`, all of which the release signs anyway.
- **The app had to drop the WPF framework too.** With only the service fixed, the clash check found `WindowsBase.dll` (the app's 2 MB WPF one vs. the service's 16 KB facade), `System.CodeDom.dll`, `System.Diagnostics.EventLog.dll` and `Microsoft.VisualStudio.Threading.dll`. With both projects free of WPF there are no clashes; the WPF/WinForms runtime is no longer in the payload. **The app wasn't run since** (the user runs it, see below).
- **Service recovery via `sc.exe`**, not WiX's `ServiceConfigFailureActions`: WiX warns (WIX1149) that Windows Installer documents MsiServiceConfigFailureActions as unreliable, and the alternative (WixToolset.Util) is one more extension. Same values as `LEO.Service.exe install`.
- **ICE03/ICE43/ICE57 suppressed:** odd language ids in third-party version resources, and the per-user-shortcut checks that don't understand a per-machine package. `ServiceControl Remove="both"` also stops and deletes an existing `LEO` service at install; `remove-dev-install` still removes the firewall group and the dev folder.
- **Pipe-server check enforced only when the UI runs from the install folder**, so a service started from a terminal keeps working for developers (a mismatch is logged once). The engine signature check ignores `AllowUnsignedClients` (that switch is for clients).
- **Update result toast from the UI**, not the service: the hub only reaches UIs connected at that moment, and after an update the UI connects later. `UpdateInfo.LastResult` + `ui.json` `LastUpdateResultShown` make it once per user.
- **Diagnostics in the app**, from IPC data plus the service log (readable by users on purpose, §24.6 item 8), with no new IPC call. The report lists driver descriptions, not driver versions.
- **Settings switches** use `Toggled` with a guard (only a switch value different from the service's counts as the user's action), like a Click handler; the existing start-at-login/failover/notification switches are unchanged.
- **No Shift-at-start "safe mode"** (§24.8 mentioned one; it doesn't exist): README and `docs/managed-pcs.md` document `Stop-Service LEO`.
- **Sizes:** payload 580 files, **302 MB** (WPF gone, WinAppSDK AI components still in: ONNX Runtime, DirectML, …); MSI **91.8 MB**.

**Verified on the dev PC (build level only, nothing installed or run):**
- `dotnet test LEO.slnx`: 291 passed. App and service build; the service's runtimeconfig lists only `Microsoft.NETCore.App`.
- `tools/build-release.ps1`: payload without clashes, unsigned list as above, MSI built with WiX 5.0.2. Read back from the MSI (read-only): service `LEO` LocalSystem auto, ServiceControl events, shortcut with the AUMID, custom actions and their sequence numbers (cleanup 2001 after DeleteServices 2000, before RemoveFiles 3500; remove-dev-install 4001 after InstallFiles, before InstallServices 5800; recovery 5801/5802 before StartServices 5900), `MsiProperties.Read` gets ProductName/UpgradeCode/ProductVersion.
- `mihomo -h` (v1.19.34) has `-secret` ("override secret for RESTful API").
- `tools/check-tag.ps1`: v0.1.0 accepted with the title "LEO 0.1.0 (Preview)", `v0.1` refused. All scripts parse.

**Verified live on the dev PC (2026-11-24, after the user asked; dev service redeployed, app restarted):**
- **Service:** `tools\dev-service.ps1 deploy` (one UAC): the new service started, logged "Development build (unsigned): updates are off", handed metrics back on stop and re-applied them on start (WiFi = Primary, healthy within 6 s). The engine started once the phone tether was back.
- **Folders:** `engine\` and `updates\` give the user's normal (non-elevated) token "Access is denied" even for reading their ACL; elevated `icacls` shows only SYSTEM + Administrators. `LEO\` keeps SYSTEM/Admins full, Users read; `UNTERSTELLEN` and `LEO` are owned by Administrators.
- **Engine secret:** `config.yaml` (rewritten by the new service) has no `secret:` line; the engine's loopback API answers **401 Unauthorized** to a request without the secret, so `-secret` is in effect. (Reading mihomo's command line through WMI didn't show it; the 401 is the proof.)
- **App:** connected, Full control, service 0.1.0. Settings → Updates: "Development build (not signed): updates are off.", switches disabled, Check now disabled. About: "LEO 0.1.0 (Preview)", signer line, Preview text, links, checkbox, Copy diagnostics and Uninstall LEO. **Copy diagnostics** (box unticked): 5 adapters with kind, driver, state, speed, health, Primary, dual link, secondary station, VPN; split tunneling summary; "none" for warnings. **No SSID, nickname, IP or MAC in it.**
- `PipeServerCheck.ServiceView` (non-elevated) returns the running service's PID and `…\LEO\Service\LEO.Service.exe` (live test `Scm_Reports_The_Installed_Services_Process_And_Exe`).

**Three bugs found only by running it, fixed the same day:**
- **`SetDefaultDllDirectories` breaks WinUI 3** in the unpackaged app: XamlParseException "Cannot locate resource from 'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'" at start. The app now calls `SetDllDirectory("")` (`DllSearch.RemoveCurrentDirectory`: the current directory leaves the DLL search); the service keeps `SetDefaultDllDirectories` (works there).
- **A standard user can't read a SYSTEM service's image path**, so the UI's pipe-server check ("couldn't be read") would have refused every connection on an MSI install. Now, when the image isn't readable, the server's PID must equal the PID the Service Control Manager reports for service `LEO` (any user may query it), and the service's registered exe must be `LEO.Service.exe` in the install folder. Tests added.
- **A Settings refresh saved "Check for updates automatically" as off.** One-way bound switches raise `Toggled` too; while two values changed, one switch showed the new and one the old value, and the guard took that for the user's action. Now only the switch that fired counts, against that one setting, and a disabled switch never saves. The wrongly saved `autoCheck: false` on the dev PC was put back to the defaults through the service (a one-off call over the pipe, removed again). Also: the dev-build line no longer shows twice in the Updates card.

**Not exercised (the user's steps):**
- **Dev PC:** done (above), except the tray menu and toasts after the changes (not looked at).
- **MSI on a test VM or spare PC** (Smart App Control off, never this dev PC): install with one UAC prompt; service running, Start menu "LEO" opens the app, toasts appear; sign out/in → tray app starts, switch off in Settings → next sign-in it doesn't (another user still gets it); `msiexec /i … /qn START_AT_LOGIN=0`; reinstall the same MSI after building a higher version (upgrade keeps settings); install an older one (refused); uninstall with split tunneling active → no service, no "LEO" adapter, automatic metrics, no LEO firewall rules, `ProgramData\UNTERSTELLEN\LEO` and every user's `LocalAppData\UNTERSTELLEN\LEO` gone.
- **Release workflow:** a dry run (Actions → Release → Run workflow) first: checks the mihomo source build (Go version and flags were taken from the design and the upstream asset name, **not yet compared with upstream's workflow at v1.19.34**), the tests with that engine, and the unsigned MSI. The workflows weren't YAML-checked locally (no parser on this PC). Create the environment `release` with the user as required reviewer before the first tag.
- **Updater against real releases:** only with a signed build. Plan in §24.11 (a test repository's releases, tampered MSI refused).
- **Signing:** placeholder until the CA is chosen (§24.3); then replace the two marked steps.

**Open for the user:**
- **Windows App SDK licence and the GPL:** the WinAppSDK runtime files are under Microsoft's own licence terms, not an open-source licence. GPLv3's system-library exception may not cover them. Since UNTERSTELLEN/INFERSTELLEN hold LEO's copyright, the usual fix is an **additional permission** (GPLv3 §7) in LICENSE/README allowing LEO to be combined with the Windows App SDK and .NET; worth deciding before v0.1.0 (mihomo is a separate GPL program and needs nothing).
- **Trim the Windows App SDK payload** (AI components LEO doesn't use) to shrink the MSI and the licence list.
- `CHANGELOG.md` 0.1.0 is a draft to edit before tagging.

---

## 26. Unsigned Preview + LEO's own update key (2026-11-24)

**Decision (user, 2026-11-24):** no code-signing certificate for now. Certum Open Source (about $77 a year, a personal name in the certificate, signing from GitHub only by storing the SimplySign one-time-code secret as a GitHub secret) was looked at and dropped; the OV options (SSL.com eSigner about $310 a year, DigiCert KeyLocker more; GlobalSign and Sectigo need an own HSM) were judged too costly for now; Azure Artifact Signing takes individuals from the USA/Canada only. LEO ships as an **unsigned Preview**. A certificate can be added later without redesign.

**What unsigned means for users (README):** SmartScreen shows "Windows protected your PC" / *Unknown publisher* (More info → Run anyway); UAC shows *Publisher: Unknown*; **Smart App Control blocks LEO** where it's on (no override; turning it off can't be undone without a reinstall); some antivirus may flag it. With no Authenticode, the pipe-client and engine signature checks (§24.6 items 2 and 5) stay off as in dev builds; the pipe ACL and the per-user access level (admins change, standard users read) still apply.

**LEO's own update key (keeps the §24.4 rule "only LEO's publisher can push an update"):**
- **ECDSA P-256 / SHA-256** (built into .NET, no new package). `Core/Updates/UpdateSignature`: the release workflow writes `LEO-x.y.z-x64.msi.sig` = one line `leo-update-signature-v1 <base64 r|s>` over the MSI's bytes. The MSI's ProductVersion is inside the signed bytes, so an old signed MSI can't be passed off as a newer one (the verifier also checks the version).
- **Public keys** in `Core/Updates/UpdateKeys.cs` (a list, so a key can be rotated: add the new key, ship one release signed with the old key, then switch). **Private key** only in the user's password manager and the GitHub secret `LEO_UPDATE_KEY` in the environment `release` (required reviewer = the user). Losing it means installed LEOs can only be updated by reinstalling by hand.
- `tools/update-key.cs` (one-file .NET app, `dotnet run tools/update-key.cs -- new | sign <msi> | verify <msi> [sig] | selftest`): `new` prints both keys and writes nothing; `sign` (CI) refuses a secret whose public key isn't in `UpdateKeys.cs`.
- **Official builds:** `build-release.ps1 -Official` (release workflow only) adds `-p:LeoOfficialBuild=true` → assembly metadata → `LeoInfo.IsOfficialBuild`. Updates are on for **official or code-signed** builds; local and Rider builds stay "Development build: updates are off".
- **`UpdateVerifier`** (in this order): updates on → `UpdateKeys` not empty → `.sig` downloaded (`ReleaseFeed.SigUrl`) and valid for one of the keys → **if the service is Authenticode-signed**, the §24.4 signer checks too (both signatures needed then) → UpgradeCode / ProductName / version newer → SHA256SUMS as an extra check.
- **UI:** Settings → Updates/About say "Preview build, not code-signed. Updates are checked with LEO's own update key."; buttons follow `UpdateState != Off` instead of `Signed`; diagnostics say "official build, not code-signed". No IPC change (protocol stays 11).

**Release workflow:** the build job publishes with `-Official` and runs the key tool's selftest; the `sign` job (environment `release`, waits for approval) builds the MSI and writes the `.sig`; the Authenticode steps (list files, sign payload, sign MSI, `signtool verify`) run only when the variable `AUTHENTICODE` is `true`, and the two signing steps are still placeholders for the CA's tool. The draft release carries the MSI, the `.sig`, `SHA256SUMS.txt` and the mihomo source; its notes say "not code-signed yet" unless `AUTHENTICODE` is true.

**Tests (297, all green):** unsigned official build installs only with a valid update signature (missing, garbage, other key, one byte changed, no keys, dev build); code-signed builds need both signatures; format, trailing newline, key rotation, P-384 refused; shipped keys are P-256; local builds aren't official; the service asks for the `.sig` and refuses without it.

**Not verified:** the workflow changes haven't run on GitHub (no YAML parser on the dev PC); no official build or `.sig` has been made yet (needs the key).

**Open for the user:** run `dotnet run tools/update-key.cs -- new`, paste the public key into `UpdateKeys.cs`, store the private key (password manager + secret `LEO_UPDATE_KEY` in environment `release`). Later, optionally: a CA certificate (fill the two placeholder steps, set `AUTHENTICODE = true`), winget.
