# Privacy

**No telemetry. No accounts. No LEO servers.** LEO is published by UNTERSTELLEN and never sends anything about you or your PC to UNTERSTELLEN, INFERSTELLEN or anyone else.

## LEO's own network traffic (the complete list)

1. **Update check: github.com.** Once a day (and when you click *Check now*), the LEO service reads
   `https://github.com/unterstellen/leo/releases/latest` (a redirect that names the newest version) and, for the canary channel, `https://github.com/unterstellen/leo/releases.atom`. When there is a new version it downloads `LEO-<version>-x64.msi` and `SHA256SUMS.txt` from the same release. The requests carry only `User-Agent: LEO/<version>`: no PC name, no user, no ID. GitHub sees your public IP address like for any web request. You can switch checking off in Settings → Updates.
2. **Internet checks.** Every few seconds LEO checks each network for real internet by fetching a small test address *through that network*: Microsoft's connectivity test (`www.msftconnecttest.com`) and Cloudflare's (`cp.cloudflare.com`) as a fallback. You can change both in Settings → Advanced.
3. **Bond health checks.** While a bond (Bonding page) is in use, the split-tunneling engine checks each of its networks with the same test address.

Everything else LEO does on the network is *your* traffic, routed where your rules say.

## What LEO stores, and where

| What | Where | How long |
|---|---|---|
| Settings and rules | `C:\ProgramData\UNTERSTELLEN\LEO\config.json` | until you uninstall |
| Statistics: **daily byte totals** per network and per app | `C:\ProgramData\UNTERSTELLEN\LEO\stats.db` | until you clear them (Stats page) or uninstall |
| Service log (state changes, warnings, errors) | `C:\ProgramData\UNTERSTELLEN\LEO\logs\` | 7 days |
| Your UI preferences, UI log | `%LocalAppData%\UNTERSTELLEN\LEO\` | until you uninstall |
| Split-tunneling engine work files, downloaded updates | `C:\ProgramData\UNTERSTELLEN\LEO\engine\`, `…\updates\` (administrators only) | rewritten as needed; deleted on uninstall |

**Never stored or logged:** which websites or addresses you connect to, per-connection data of any kind, Wi-Fi passwords (handed straight to Windows, which keeps them encrypted). The live connections view (administrators only) is kept in memory while you look at it and is never written anywhere.

Uninstalling LEO deletes all of the above, for every user of the PC.

## Diagnostics

Settings → About → *Copy diagnostics* puts a text report on **your clipboard**; LEO doesn't send it. Network names, nicknames, IP and MAC addresses are left out unless you tick the box to include them. You decide whether to paste it into a GitHub issue.
