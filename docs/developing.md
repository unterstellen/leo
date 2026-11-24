# Building and testing LEO yourself

This guide is for working on LEO in an IDE, the way you would build and run an app in Android Studio. It uses **JetBrains Rider** (the .NET sibling of Android Studio; free for non-commercial use, check JetBrains' licence page). Visual Studio 2022 Community works too; the commands at the end work with no IDE at all.

## 1. What you need (once)

| What | Why | Check |
|---|---|---|
| Windows 11 x64 | LEO only runs there | |
| **.NET SDK 10.0.404 or newer** | compiles LEO (version pinned in `global.json`) | `dotnet --version` |
| **Rider** (a recent version) | the IDE; must be new enough to open `LEO.slnx` | |
| Git | to get and update the code | `git --version` |
| Admin rights | the LEO service is installed into Program Files | |
| **Smart App Control off** | it blocks unsigned programs, and builds made on your PC aren't signed. Windows Security → App & browser control → Smart App Control. Note: on many Windows versions it can't be turned back on without resetting Windows, so do this only on a PC you develop on | |

No Visual Studio, no Windows SDK installer and no Windows App SDK installer are needed: everything else comes from NuGet when the project is first built.

## 2. Open the project

1. Start Rider → **Open** → pick `LEO.slnx` in the repository folder.
2. Wait until Rider has finished restoring packages (status bar at the bottom). This is LEO's version of a Gradle sync.
3. Top toolbar: pick the **Debug** configuration. The service and the tests are x64-only; that's already set in the solution.
4. The run-configuration menu next to the ▶ button already lists LEO's configurations (they come from the `.run` folder in the repository):

| Configuration | What it does | Admin prompt |
|---|---|---|
| **LEO (UI)** | builds and starts the LEO window and tray icon. ▶ runs it, 🐞 runs it with the debugger | no |
| **Deploy service** | builds the service and installs it into `C:\Program Files\UNTERSTELLEN\LEO\Service`, then starts it. Also fetches the split-tunneling engine the first time (a pinned, hash-checked download from github.com) | yes, one |
| **Service logs** | shows the last 40 lines of today's service log | no |
| **Restart service** | restarts the installed service | yes |
| **Uninstall service** | stops the service (Windows networking is handed back) and removes it; your settings stay | yes |
| **Tests (all)** | runs every unit test from the command line, same as CI | no |

## 3. How LEO is put together (the one big difference from Android)

LEO is **two programs**:

- **LEO.App** (`src/LEO.App`, file `LEO.exe`): the window and tray icon. Runs as you, without admin rights. Like an Android app's activities.
- **LEO.Service** (`src/LEO.Service`): a Windows service running as SYSTEM. It does the actual networking (adapters, failover, Wi-Fi, the split-tunneling engine). Like a foreground service, except it runs even when nobody is logged in and is installed separately.

They talk over a local named pipe. The app shows "LEO service is not running" when the service isn't installed or running.

`LEO.Core` and `LEO.Ipc` are shared libraries used by both. `tests/LEO.Tests` holds the unit tests.

## 4. The everyday loop

**First time:** run **Deploy service**, accept the admin prompt, then run **LEO (UI)**.

Then, depending on what you changed:

| You changed… | Do this |
|---|---|
| only the app (`src/LEO.App`: pages, XAML, view models) | ▶ **LEO (UI)** |
| the service (`src/LEO.Service`) or shared code (`src/LEO.Core`, `src/LEO.Ipc`) | **Deploy service**, then ▶ **LEO (UI)** |
| tests only | run them from the gutter icons in the test file, or **Tests (all)** |

**Before pressing ▶:** quit the LEO that is already running (tray icon → right-click → Quit). LEO allows only one copy at a time: a second start just brings the first window forward and exits, and while `LEO.exe` runs, the build can't replace it ("file is being used by another process").

**Changing XAML:** there's no visual designer for WinUI 3 in Rider. Edit the `.xaml` file, then stop and run again to see the result.

## 5. Seeing what happens (Logcat equivalents)

- **App:** Rider's Run/Debug window shows the app's debug output. Connection problems and unexpected errors also go to `%LocalAppData%\UNTERSTELLEN\LEO\logs\`.
- **Service:** **Service logs**, or open `%ProgramData%\UNTERSTELLEN\LEO\logs\service-<date>.log`. One file per day, kept 7 days.
- LEO never logs which app connected where, or which websites were visited. Don't add log lines that do.

## 6. Debugging

- **The app:** 🐞 **LEO (UI)**. Breakpoints work as usual.
- **The service**, two ways:
  1. **Attach:** start Rider **as administrator** (right-click → Run as administrator), then Run → Attach to Process → `LEO.Service.exe`. The installed copy is a Debug build, so breakpoints in service code are hit.
  2. **Console mode:** stop the service (`services.msc` → LEO → Stop, or **Uninstall service**), then in an administrator Rider run `LEO.Service.exe` without arguments; it then runs in the console instead of as a service. Some things only SYSTEM may do can behave differently, so the installed service is the real test.

## 7. Tests

- **Unit tests** (`tests/LEO.Tests`, about 240) run anywhere and touch nothing on the PC: Rider's Unit Tests window, the gutter icons, or **Tests (all)**. GitHub runs the same tests on every push.
- One test runs the real engine's config check; it is skipped until the engine has been fetched (**Deploy service** does that, or `.\tools\get-engine.ps1`).
- **Live tests** check the installed service on this PC and are off by default. They are switched on with environment variables (`LEO_LIVE_TESTS=1`, `LEO_LIVE_M6=…`, `LEO_LIVE_M7=…`, see `LEO.md` §12a, §21, §23). Some of them change rules on the running service. Read the test first.

## 8. Things to know

- **Your real network is the test bench.** The deployed service really changes interface priorities and, with split tunneling on, routing. If the internet acts up: **Uninstall service** hands everything back to Windows.
- A dev build of the app adds itself to "start at login" (HKCU) the first time it runs (pointing at your Debug build). Turn it off in LEO's Settings if you don't want that. An MSI install uses a machine-wide entry instead (section 10).
- The project rules for UI code: actions only from Click handlers, one dialog at a time, nothing that silently ignores a click (show it disabled instead), no em dashes in UI text. Cards and list rows are laid out top to bottom: the main information first (name, details, "Now:"), checkboxes on their own row below it, buttons on the last row.
- `bin\`, `obj\`, `artifacts\` and Rider's `.idea\` folder are never committed.

## 9. The same without an IDE

```powershell
dotnet build LEO.slnx                        # build everything
dotnet test  LEO.slnx                        # all unit tests
.\tools\dev-service.ps1 deploy               # install/update the service (one admin prompt)
src\LEO.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\LEO.exe   # start the app
.\tools\dev-service.ps1 logs                 # service log
.\tools\dev-service.ps1 uninstall            # remove the service
```

## 10. Building the installer and releases (M8)

**Local test MSI (unsigned):**

```powershell
.\tools\get-engine.ps1          # once: the pinned engine (artifacts\engine\mihomo.exe)
.\tools\build-release.ps1       # artifacts\release\LEO-<version>-x64.msi + payload-signatures.txt
```

In Rider: pick **Build MSI** in the run configurations list (next to *Deploy service*) and press Run. It runs the same script; the MSI lands in `artifacts\release\`.

It publishes the app and the service self-contained (.NET included, ReadyToRun only for LEO's own files, English only) into one install folder, stops if the two publish the same file with different content, lists every exe/dll with its signature status, and builds the MSI with WiX v5 (from NuGet). It never signs. Smart App Control blocks unsigned builds, so test the MSI on a PC or VM where it's off, **not** on top of your dev setup: the MSI removes a dev-deployed service (`tools\dev-service.ps1 deploy`), and `dev-service.ps1` refuses to deploy over an MSI install.

What an installed LEO does differently from a dev build:

- **Start at login:** the MSI writes one machine-wide `HKLM\...\Run\LEO` entry; each user's Settings switch is stored in `ui.json`. The installed app removes an old HKCU entry (the dev one) the first time it runs.
- **Official builds** (release workflow, signed or not): the updater is on and checks LEO's update signature (`LEO.md` §26).
- **Code-signed builds only** (once a CA certificate exists): pipe clients other than LEO's own signed `LEO.exe` from the install folder get read-only access, the engine starts only if it's signed by LEO, and updates must also carry the code signature. Dev builds and unsigned Preview releases have no certificate, so these checks are off. On a signed install an admin can set `HKLM\SOFTWARE\UNTERSTELLEN\LEO\AllowUnsignedClients = 1` (DWORD) for the opt-in live tests.
- The app checks that the pipe's server is LEO's service in session 0 from the install folder; a dev app only logs a mismatch.

**Uninstall cleanup** is `LEO.Service.exe cleanup` (run by the MSI as SYSTEM, full uninstall only). Don't run it on your dev PC: it deletes all of LEO's settings for every user.

**Releases** (`.github/workflows/release.yml`): you push a tag `vX.Y.Z` (or `vX.Y.Z-canary.N`). GitHub checks the tag (`tools/check-tag.ps1`), builds mihomo from source (`tools/build-engine.ps1`), runs the tests with it, publishes an **official** build (`build-release.ps1 -Official`), then waits in the environment `release` for your approval, builds the MSI, writes LEO's update signature `LEO-x.y.z-x64.msi.sig`, and creates a **draft** release that you publish by hand. *Actions → Release → Run workflow* is a dry run that builds an unsigned MSI artifact (the workflow must be on `main` for that button to appear).

**LEO's update key** (`LEO.md` §26). Until LEO has a code-signing certificate, releases are "unsigned Preview" and the updater trusts only LEO's own ECDSA P-256 key:

```powershell
dotnet run tools/update-key.cs -- new        # once: prints the public and the private key, writes nothing
dotnet run tools/update-key.cs -- verify artifacts\release\LEO-0.1.0-x64.msi   # checks a downloaded MSI against its .sig
```

The public key goes into `src/LEO.Core/Updates/UpdateKeys.cs` (committed). The private key goes into your password manager and the GitHub secret `LEO_UPDATE_KEY` in the environment `release`, nowhere else. Lose it and installed LEOs can only be updated by reinstalling by hand. To rotate: add the new public key, ship one release signed with the old key, then switch the secret. Only builds made with `-Official` (the release workflow) update themselves; local and Rider builds never do.

**Code signing later:** when a CA certificate is bought, replace the two `PLACEHOLDER` steps in `release.yml` with the CA's signing step and set the variable `AUTHENTICODE = true` on the `release` environment. Releases are then code-signed as well, and the update signature stays.

Version numbers: `0.MINOR.PATCH` while LEO is in Preview; every tag's x.y.z must be higher than every earlier one (Windows Installer compares only those three numbers). Release notes come from `CHANGELOG.md`.
