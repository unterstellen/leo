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
- The app adds itself to "start at login" the first time it runs (pointing at your Debug build). Turn it off in LEO's Settings if you don't want that.
- The project rules for UI code: actions only from Click handlers, one dialog at a time, nothing that silently ignores a click (show it disabled instead), no em dashes in UI text.
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

Coming with M8 (`LEO.md` §24): a command that builds the MSI installer locally (unsigned, for testing) and the release workflow on GitHub that builds and signs real releases.
