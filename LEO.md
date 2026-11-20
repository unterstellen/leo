# LEO — Multi-Network Manager & Split Tunneling for Windows

| | |
|---|---|
| **App name** | LEO |
| **Publisher (brand)** | UNTERSTELLEN — unterstellen.org |
| **Company** | INFERSTELLEN — inferstellen.com |
| **Platform** | Windows 11, x64 only |
| **Price** | Free. No accounts, no telemetry |
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
| Code signing | An EV/OV certificate will be bought. Every binary and the installer are signed |
| Failover | **Auto-failover by priority**, based on real internet health checks. Switches back when the network recovers |
| Pinned network down | **Per-rule choice**: `Block` (kill switch) or `Fall back to Primary` |
| Extras | Live per-network stats, Profiles with auto-switch, Per-network DNS, **Bandwidth bonding** |
| VPN | LEO is the **underlay** (the VPN rides on top of the network LEO picks) **and** can **bypass** the VPN for chosen apps/URLs |
| Distribution | Signed **MSI installer** from the website, with auto-update |
| UI style | **Windows 11 Fluent** (Mica, system theme and accent) |
| Min OS | **Windows 11 only** |
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
- The service checks the client's PID → image path → Authenticode signature (must be signed by INFERSTELLEN/UNTERSTELLEN) before it accepts commands.
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
- **Licensing constraint**: LEO is free but **closed-source**, so **no GPL code** (e.g. sing-box, Clash cores). Use MIT/Apache/BSD only. Wintun ships as Microsoft-signed `wintun.dll` under its redistribution license.

### 5.4 DNS
- LEO runs a DNS resolver on the TUN interface. Windows' DNS points at it.
- **Fake-IP mode** (default): domains get answers from `198.18.0.0/15` (and an IPv6 ULA range). LEO keeps the fake IP → domain map, so domain rules match exactly. The real lookup happens **through the target network** using that network's DNS.
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
- A probe runs on **each interface** every N seconds (default 5): HTTP `generate_204`-style check (endpoint configurable), DNS lookup, and TCP connect latency. Detects captive portals.
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
- Works **per connection**: new flows are spread across members. Many-connection downloads (browsers, Steam, torrents, download managers) get roughly the combined bandwidth. A **single TCP stream cannot go faster than one link** (that would need a remote aggregation server, which is out of scope; see §10).
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
- **No telemetry. No accounts.** The only network call LEO makes for itself is the update check to unterstellen.org.

---

## 9. Distribution
- **MSI** built with the WiX Toolset. MSIX isn't used because it can't install the Wintun driver adapter or a LocalSystem service cleanly.
- Installer: installs the service, Wintun, the UI and Start menu shortcuts, with the option "Start LEO at login".
- Everything signed with the INFERSTELLEN/UNTERSTELLEN code-signing certificate (Authenticode + timestamp).
- **Auto-update**: the service checks a signed JSON manifest on unterstellen.org, downloads the MSI, verifies the signature, installs silently and restarts. The user can defer it.
- Channels: `canary` (git branch `canary`) → `main` (stable).

---

## 10. Out of Scope (v1)
- ARM64, Windows 10, macOS/Linux.
- Single-stream bonding via a remote aggregation server ("LEO Bond Server"), a possible v2.
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
| **M3** | TUN engine + App rules | Wintun up; all traffic goes through the engine; per-app routing works for TCP/UDP; safe restore if it crashes |
| **M4** | Destination rules + DNS | Fake-IP DNS, SNI sniffing, domain/CIDR/port rules, per-network DNS, precedence, "test a rule" |
| **M5** | Profiles + Stats | Profiles with triggers; per-adapter/per-app stats; connections view |
| **M6** | VPN coexistence | VPN as underlay + as target + bypass; kill-switch detection |
| **M7** | Bonding | Bond groups, balancing modes, sticky sessions |
| **M8** | Ship | WiX MSI, signing, auto-update, uninstall cleanup, hardening, docs |

---

## 13. Open Questions
1. **Signing identity**: which legal entity is the certificate issued to (INFERSTELLEN or UNTERSTELLEN)? This sets the "Publisher" shown in UAC/SmartScreen and the signature check in §4.2.
2. **Branding assets**: LEO logo/icon (tray needs light and dark variants), accent color.
3. **Update hosting**: exact URL for the update manifest and downloads.
4. **Health probe default endpoint**: an LEO-owned endpoint (e.g. `https://unterstellen.org/generate_204`) or Microsoft's NCSI endpoint?
5. **Localization**: English only for v1, or other languages too?
