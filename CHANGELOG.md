# Changelog

Release notes for LEO. The release workflow copies the section of a version into its GitHub release.
LEO is in **Preview** until 1.0: versions are `0.MINOR.PATCH` (MINOR for new features, PATCH for fixes only).

## [Unreleased]

## [0.1.0]

The first public release (Preview).

- **Networks:** every Wi-Fi, Ethernet, phone tethering, Bluetooth and VPN adapter with live health and latency; one-click Primary and a priority order; nicknames.
- **Failover:** LEO checks each network for real internet every few seconds and moves traffic when one fails, then switches back.
- **Wi-Fi:** keep an adapter on chosen networks, join networks, dual-station cards.
- **Split tunneling by app** and **by website, domain or IP**, with per-network DNS.
- **Profiles** with automatic switching; **statistics** per network and app (daily totals only).
- **VPN coexistence:** choose the network a VPN uses, send apps into or around a VPN, kill-switch detection.
- **Bonding:** spread connections across several networks.
- **Installer:** one admin prompt, self-contained (nothing to install first), start at login for every user, uninstall puts Windows networking back and removes all settings.
- **Updates:** silent automatic updates from GitHub Releases, checked against LEO's own update signature; can be switched off.
- **Not code-signed yet:** Windows shows *Unknown publisher*, and Smart App Control blocks LEO where it's on (see the README).
