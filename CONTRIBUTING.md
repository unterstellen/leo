# Contributing to LEO

Thanks for helping. Issues, hardware reports and pull requests are welcome; for anything large, open an issue first so we can agree on the approach.

## Build and test

- Requirements, the IDE setup (Rider run configurations) and the dev service: [docs/developing.md](docs/developing.md).
- `dotnet build LEO.slnx` and `dotnet test LEO.slnx` must pass. CI runs the same on GitHub-hosted Windows.
- `.\tools\build-release.ps1` builds a local, **unsigned** test MSI. Releases are built and signed only by the release workflow in GitHub Actions, never on a developer PC.

## Rules the UI follows (please keep them)

- Every action comes from a Click handler (or a switch the user flips), never from a two-way binding changing.
- One ContentDialog at a time.
- No hidden time limits: anything that can't be clicked right now is shown disabled.
- No em dashes in UI text.
- Cards and list rows: main information on top, checkboxes on their own row below it, buttons on the last row. Toggles in Settings have no On/Off text.

## Rules the service follows

- Never brick the network: whatever LEO changes, stopping the service hands back to Windows.
- Never log per-connection data (which app talked to which address). Logs hold state changes, counts and ids.
- No telemetry, no accounts, no servers of our own. LEO's own network traffic is listed in [PRIVACY.md](PRIVACY.md); a change to that list needs a very good reason.

## Licence of contributions

LEO is GPL-3.0-or-later. By contributing you agree that your contribution is licensed under the same terms. (Whether LEO will ask for a DCO sign-off or a CLA is still open, LEO.md §14.)

## Reporting bugs and hardware results

Use the issue forms. Settings → About → *Copy diagnostics* gives you a report to paste; it leaves out network names and addresses unless you tick the box. Security problems: see [SECURITY.md](SECURITY.md).
