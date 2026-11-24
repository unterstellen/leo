# Security

## Reporting a vulnerability

Please report security problems **privately** through GitHub: open the repository's **Security** tab → **Report a vulnerability** (GitHub's private vulnerability reporting). Don't open a public issue for them.

You'll get an answer in the advisory thread. Fixes ship as a normal signed LEO release, which reaches installed copies through the automatic updater.

## What LEO protects, in short

- The LEO service runs as SYSTEM and only accepts commands over a local named pipe. Administrators can change settings; standard users can only look.
- In signed releases the service only gives that access to LEO's own signed `LEO.exe` from its install folder; other programs are read-only. This stops other programs from *pretending* to be LEO. It can't stop a program running as the same user from tampering with a running `LEO.exe`; the real boundary is the pipe's access list plus admin vs. standard user.
- The app checks that the pipe really belongs to LEO's service before trusting it.
- Updates are installed only after the MSI's Authenticode signature is valid and from the same publisher as the installed LEO.
- LEO's data folders are owned by SYSTEM or Administrators; the engine folder (which holds the engine's configuration) and the updates folder are administrators-only.

Details: [LEO.md](LEO.md) §4.2 and §24.6.

## Supported versions

Only the newest release gets fixes. LEO updates itself; if you turned that off, update from Settings → Updates.
