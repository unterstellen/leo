# Third-party notices

LEO itself is © 2026 INFERSTELLEN, published by UNTERSTELLEN, under the GNU General Public License v3.0 or later ([LICENSE](LICENSE)). LEO ships the following software written by others. Their licences are theirs; LEO's GPL doesn't change them.

Files that arrive unsigned from their authors are signed with LEO's certificate in the release workflow so Windows' Smart App Control lets them run. That signature means "shipped by LEO", not "written by LEO". Files already signed by their authors keep the authors' signatures.

## Split-tunneling engine

| Component | Licence | Notes |
|---|---|---|
| **mihomo** (github.com/MetaCubeX/mihomo), `engine\mihomo.exe` | GPL-3.0 | Runs as a separate process. Release builds compile it from source in GitHub Actions; the exact source used is attached to every release as `mihomo-<tag>-source.tar.gz` (tag plus vendored Go modules). |
| **Wintun** (wintun.net), embedded in mihomo | Prebuilt Binaries License (WireGuard LLC) | Microsoft-signed driver; redistributed unmodified as part of the engine. |
| Go standard library and the Go modules mihomo uses | BSD-3-Clause, MIT, Apache-2.0 and others | Listed with their licence files in the mihomo source archive (`vendor/`). |

## .NET and Windows

| Component | Licence |
|---|---|
| .NET runtime and libraries (`Microsoft.NETCore.App`, `Microsoft.Extensions.*`, `System.*`) | MIT |
| **Windows App SDK** (WinUI 3 and its runtime files) | Microsoft Software License Terms for the Windows App SDK (redistributable with an app; not an open-source licence) |
| Microsoft Edge WebView2 SDK (pulled in by the Windows App SDK) | BSD-3-Clause style (Microsoft) |
| C#/WinRT (`WinRT.Runtime.dll`), Windows SDK projection (`Microsoft.Windows.SDK.NET.dll`) | MIT / Microsoft Windows SDK licence |
| ONNX Runtime, DirectML and other AI components shipped inside the Windows App SDK package | MIT / Microsoft licence terms (LEO doesn't use them) |

## Libraries

| Component | Licence |
|---|---|
| StreamJsonRpc, Microsoft.VisualStudio.Threading, Microsoft.VisualStudio.Validation, Nerdbank.Streams, Nerdbank.MessagePack | MIT |
| MessagePack-CSharp (`MessagePack.dll`, `MessagePack.Annotations.dll`) | MIT |
| PolyType | MIT |
| Newtonsoft.Json | MIT |
| H.NotifyIcon (`H.NotifyIcon.dll`, `H.NotifyIcon.WinUI.dll`, `H.GeneratedIcons.System.Drawing.dll`) | MIT |
| CommunityToolkit.Mvvm | MIT |
| System.Management | MIT |

The full licence texts come with each package (NuGet) and repository. The MIT licence requires keeping the copyright notice; each library's notice is in its package and on its project page.

## Build tools (not shipped)

WiX Toolset v5 (MS-RL) builds the MSI. Nothing of it is installed except Windows Installer's standard tables.
