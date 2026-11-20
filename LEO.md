# LEO — Multi-Network Manager & Split Tunneling for Windows

| | |
|---|---|
| **App name** | LEO |
| **Publisher (brand)** | UNTERSTELLEN — unterstellen.org |
| **Company** | INFERSTELLEN — inferstellen.com |
| **Code-signing identity** | INFERSTELLEN |
| **Repository** | https://github.com/unterstellen/leo-windows (public) |
| **Releases / updates** | https://github.com/unterstellen/leo-windows/releases |
| **Platform** | Windows 11, x64 only |
| **Language** | English only (permanently) |
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
| Code signing | An EV/OV certificate issued to **INFERSTELLEN** will be bought. Every binary and the installer are signed |
| Branding | No custom brand colors: follow Windows 11 (system accent, light/dark). **Logo**: a yellow circle face wearing dark glasses, with no eyes, nose or mouth (`assets/leo.svg`) |
| Language | English only, permanently. No localization layer |
| Failover | **Auto-failover by priority**, based on real internet health checks. Switches back when the network recovers |
| Pinned network down | **Per-rule choice**: `Block` (kill switch) or `Fall back to Primary` |
| Extras | Live per-network stats, Profiles with auto-switch, Per-network DNS, **Bandwidth bonding** |
| VPN | LEO is the **underlay** (the VPN rides on top of the network LEO picks) **and** can **bypass** the VPN for chosen apps/URLs |
| Distribution | Signed **MSI installer** published on **GitHub Releases**, with auto-update |
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
- **No telemetry. No accounts.** LEO's only network calls of its own are the update check to `api.github.com` (unterstellen/leo-windows releases) and the internet health probes (§5.7).

---

## 9. Distribution
- **MSI** built with the WiX Toolset. MSIX isn't used because it can't install the Wintun driver adapter or a LocalSystem service cleanly.
- Installer: installs the service, Wintun, the UI and Start menu shortcuts, with the option "Start LEO at login".
- Everything signed with the **INFERSTELLEN** code-signing certificate (Authenticode + timestamp).
- **Hosting: GitHub Releases** at https://github.com/unterstellen/leo-windows/releases (public repo). Each release has the signed `LEO-<version>-x64.msi` attached. Tags look like `v0.2.0`.
- **Auto-update**: the service calls `GET https://api.github.com/repos/unterstellen/leo-windows/releases/latest` (anonymous; GitHub allows 60 requests/hour per IP, so it checks at most every 6 h), compares the tag (`v0.2.0`) with its own version, downloads the MSI asset, **verifies the Authenticode signature is INFERSTELLEN and valid**, then installs silently and restarts. The user can defer it. The Canary channel reads pre-releases instead of `latest`.
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

## 12a. Development

**Requirements:** .NET SDK 10.0.404+ (pinned in `global.json`). No Visual Studio needed: Windows App SDK / WinUI 3 come in through NuGet. Rust is only needed from M3.

```
dotnet build LEO.slnx                      # build everything
dotnet test  tests\LEO.Tests               # unit + in-process IPC/host tests
.\tools\dev-service.ps1 deploy             # build service, copy to Program Files\UNTERSTELLEN\LEO\Service, (re)install + start (1 UAC)
.\tools\dev-service.ps1 logs               # tail today's service log
src\LEO.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\LEO.exe   # run the UI
.\tools\dev-service.ps1 uninstall          # stop (metrics handed back to Windows) + remove
```

- The service runs from Program Files (admin-only writable), never from `bin\`, so rebuilding never collides with the running service.
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
- **Card layout:** actions sit in their own row under the name and details (in a small custom `WrapPanel`, wrapping onto more lines in narrow windows), so details use the full card width and buttons never get clipped. Wi-Fi networks and Rename have text labels.
- Probes: an adapter whose IP is changing (network switch, DHCP) is now reported **Unknown** instead of NoInternet, so a network switch can't cause a false failover.
- **Not yet exercised live: an actual failover/failback** (the phone tether is a second internet path), **and a toast on screen.**

---

## 13. Resolved Questions (2026-11-20)
1. **Signing identity**: INFERSTELLEN. Used for UAC/SmartScreen publisher, the pipe-client check (§4.2) and the update check (§9).
2. **Branding**: follow Windows 11, with no custom colors. Logo = yellow face + dark glasses, no other features. Source `assets/leo.svg`, rendered by `tools/make-icon.ps1` into `src/LEO.App/Assets/leo.ico` (16–256 px; a simplified, bolder variant at ≤ 24 px for the tray). One icon works on both light and dark taskbars.
3. **Update hosting**: GitHub Releases on the public repo https://github.com/unterstellen/leo-windows.
4. **Health probe**: Microsoft NCSI + Cloudflare fallback, configurable (§5.7).
5. **Localization**: English only, permanently.

## 14. Open Questions
None right now.
