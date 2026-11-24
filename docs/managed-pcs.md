# LEO on managed PCs

For IT administrators and anyone installing LEO on a company or school PC.

## What the installer does

- Installs to `C:\Program Files\UNTERSTELLEN\LEO\` (per-machine, x64, Windows 11 build 22000 or later).
- Registers the Windows service **LEO** ("LEO Network Engine"), LocalSystem, automatic start, restart on failure.
- While split tunneling is on, the service's engine creates a virtual network adapter named **LEO** (Wintun) and removes it when it stops. No driver is installed by the MSI itself.
- Adds a Start menu shortcut and, unless turned off, one machine-wide `HKLM\Software\Microsoft\Windows\CurrentVersion\Run\LEO` entry so the tray app starts at sign-in for every user. Each user can switch it off in LEO's Settings.
- Creates `C:\ProgramData\UNTERSTELLEN\LEO\` (owned by Administrators).

Installing needs **administrator rights** (one UAC prompt). Standard users can use LEO afterwards but only view settings; changes need an administrator.

## Silent install and uninstall

```powershell
msiexec /i LEO-0.1.0-x64.msi /qn START_AT_LOGIN=1
msiexec /i LEO-0.1.0-x64.msi /qn START_AT_LOGIN=0      # no tray app at sign-in
msiexec /x LEO-0.1.0-x64.msi /qn                       # or: msiexec /x {ProductCode} /qn
```

- `START_AT_LOGIN` (`1` default, `0` off) is remembered across upgrades.
- Add `/l*v leo-install.log` for a log.
- A newer LEO replaces an older one in place (MajorUpgrade); settings, rules and statistics are kept. Installing an older version over a newer one is refused.
- **Uninstalling removes all of LEO's settings, statistics and logs** for every user, and puts Windows networking back (metrics, DNS, LEO's firewall rules, dual-link Wi-Fi). Upgrades never delete anything.

## Automatic updates

By default the LEO service checks GitHub once a day and installs signed updates by itself (see [PRIVACY.md](../PRIVACY.md) for the exact requests). On a managed PC you may prefer your own deployment: switch off *Check for updates automatically* in LEO's Settings → Updates (machine-wide, needs an administrator), then deploy new MSIs yourself.

## Policies that can get in the way

- **Smart App Control / WDAC / AppLocker:** LEO's release files are Authenticode-signed; allow the publisher if your policy only allows known publishers. Unsigned development builds are blocked by Smart App Control.
- **Blocking services or network drivers:** LEO's split tunneling needs its engine's virtual adapter. Without it, network priority, failover and Wi-Fi management still work.
- **VPN clients with a kill switch or "always on" corporate VPNs:** LEO detects them and steps aside where they only allow their own tunnel ([LEO.md](../LEO.md) §20).
- **Firewall management:** LEO only creates and removes rules in its own group "LEO (VPN only)".
- **Group Policy for network metrics or DNS:** LEO sets interface metrics to enforce your priority order; a policy that resets them will fight with it.

## If LEO causes network problems

Stop the service: `Stop-Service LEO` (or Services → LEO Network Engine → Stop). Everything LEO changed is handed back to Windows immediately. Uninstall from Settings → Apps → Installed apps → LEO.
