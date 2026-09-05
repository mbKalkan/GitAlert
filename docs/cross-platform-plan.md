# Cross-platform plan: GitAlert 2.0 on Avalonia

Status: approved on 2026-09-05. This file records the shape of the work, the order, the acceptance
bar for each phase and the decisions already taken. Small details are settled while implementing.
Each phase lands on `main` on its own with green tests; there is no big-bang branch.

## Decisions

| Topic | Decision |
|---|---|
| UI framework | Avalonia 12 (MIT). Current stable is 12.1.2; confirm at the start of phase 2. |
| Platforms | Windows 10/11 x64; macOS x64 and arm64; Linux x64 (glibc). |
| Codebase | One application project. Platform differences sit behind small interfaces, not separate apps. |
| Runtime | .NET 10 LTS (`net10.0`). .NET 9 leaves support in November 2026. |
| Version | 2.0.0 when the Avalonia build replaces WPF. Refactors before that ship as 1.x. |
| Licence | MIT, unchanged. New dependencies must be MIT, Apache-2.0 or BSD. |
| macOS signing | No Apple Developer account yet: ad-hoc `codesign`, README documents "Open Anyway". |
| Windows tray | Keep the Win32 `Shell_NotifyIcon` code. Balloons, flyout anchoring and DWM corners stay as they are. |

## Goals

- The same features on all three platforms: accounts, monitoring, flyout, diff pane, settings,
  notifications, run at login.
- No regression for Windows users: Dark Modern / GitHub palette / light themes, tray behaviour,
  flyout anchoring, keyboard access, DPI handling.
- CI builds and tests on all three operating systems. One tag produces every package in one
  GitHub Release.
- Existing Windows user data (settings, state, history, tokens) keeps working in place.

Out of scope for 2.0: auto-update, winget/Homebrew/Flatpak/rpm listings, notarization, Wayland
tray quirks beyond what Avalonia handles itself.

## Target layout

```
src/GitAlert.Core/        net10.0, no UI dependency
  Core/  GitHub/  Configuration/  Services/  ViewModels/
  Platform/               interfaces only: ITrayHost, INotifier, ISecretStore,
                          IStartupRegistrar, IPlatformTheme, IShell, IAppPaths
src/GitAlert.Windows/     net10.0-windows, no UI framework: Win32 tray, DPAPI, Registry, DWM
                          (moved, not rewritten; shared by the WPF and the Avalonia app meanwhile)
src/GitAlert/             net10.0 Avalonia app for Windows, macOS and Linux
  Views/  Themes/  Converters/
  Platform/MacOS/         Keychain, LaunchAgent, notifications
  Platform/Linux/         Secret Service, XDG autostart, DBus notifications
tests/GitAlert.Tests/     xUnit + Avalonia.Headless.XUnit; runs on every OS
tools/Render/             headless PNG renderer for the screenshot check
installer/                GitAlert.iss (Windows), macos/ (Info.plist, DMG script),
                          linux/ (AppImage recipe, .deb metadata, .desktop, icons)
```

## Platform matrix

| Concern | Windows | macOS | Linux |
|---|---|---|---|
| Tray icon | Win32 `Shell_NotifyIcon` (existing) | Avalonia `TrayIcon` (NSStatusItem) | Avalonia `TrayIcon` (StatusNotifierItem over DBus) |
| Notifications | Balloon via `NIF_INFO` (existing) | UNUserNotificationCenter; needs the .app bundle | `org.freedesktop.Notifications` over DBus |
| Flyout placement | Anchored to the icon rect (existing) | Below the menu bar, near the pointer | Near the pointer, clamped to the work area |
| Secret store | DPAPI (existing) | Keychain via Security.framework | Secret Service (libsecret) over DBus; `0600` file fallback with a visible warning |
| Run at login | HKCU `Run` key (existing) | `~/Library/LaunchAgents/*.plist` | `~/.config/autostart/*.desktop` |
| Theme signal | Avalonia `PlatformSettings`; Registry only for the taskbar theme that colours the tray icon | Avalonia `PlatformSettings` | Avalonia `PlatformSettings` |
| Data directory | `%APPDATA%\GitAlert` (unchanged) | `~/Library/Application Support/GitAlert` | `$XDG_CONFIG_HOME/GitAlert` |
| Open link / folder | ShellExecute | `open` | `xdg-open` |
| Package | Inno Setup exe + portable zip | DMG per architecture | AppImage, .deb, tar.gz |

## Phases

### Phase 1: extract the core (ships as 1.18.0)

Done on 2026-09-05. `GitAlert.Core` holds the model, the GitHub client, the stores, the monitor and
every view model; the platform analyzer's CA1416 is an error there, so a Windows API cannot creep
back in. The seams that exist so far are the ones the view models needed: `ISecretStore` over an
`ITokenProtector` (DPAPI stays in the app), `IStartupRegistrar`, and `UiThread` in place of the
WPF dispatcher. `ITrayHost`, `INotifier`, `IPlatformTheme` and `IAppPaths` wait for the phase that
brings their first non-Windows consumer. The renders matched the README screenshots pixel for pixel.

- Create `GitAlert.Core`. Move everything without a `System.Windows` dependency (about 5,800
  lines: Core, GitHub, Configuration, MonitorService, AlertStore, MonitorState, most view models).
- Define the platform interfaces in Core. The WPF app implements them with its current code.
- Remove the last WPF types from shared view models: `FlyoutViewModel` gets a status enum instead
  of `Brush` and a dispatcher seam instead of `Dispatcher`; `AlertGlyphs` exposes path data
  strings instead of `Geometry`.
- Move to .NET 10: install the SDK on this PC (`winget install Microsoft.DotNet.SDK.10`), add a
  `global.json`, update both workflows and `build.ps1`.
- Tests: portable tests keep passing unchanged; the STA-bound tests stay for phase 2.

Acceptance: no visible change; the whole test suite green (333 cases today); `build.ps1 -Installer` upgrades the current
install in place with settings, history and tokens intact.

### Phase 2: Avalonia UI, Windows first (merges to `main`; the Windows release ships it from 1.21.0)

In progress since 2026-09-05. From 1.21.0 the installer and the portable archive carry the Avalonia
build as `GitAlert.exe`, so the daily-use trial runs on the installed app rather than a side
build; the WPF project stays in the solution, unshipped, until phase 4 deletes it. `src/GitAlert.Desktop` builds on Avalonia 12.1 with the flyout and the
settings window ported, the three palettes swapped at runtime, the Win32 tray and DPAPI reused from
`GitAlert.Windows` behind `IPlatform`, and a named pipe for the second-launch signal. Headless
renders of both windows match the WPF screenshots to the eye in all three palettes; a live run on
this PC put the icon in the tray and brought the flyout up in front. Both test projects run on
xunit v3 through Microsoft.Testing.Platform, and `tests/GitAlert.Desktop.Tests` drives the real
windows headlessly, including a check that no binding misses its target. Still open: a keyboard
and pointer pass on the real app (tray menu, drag to reorder, splitter), scrollbar styling, and
the daily-use trial.

Lessons from the port, for the pieces still to come: a value set on an element outranks every
style, so anything a class may change has to start life in a style; `$parent[Type;n]` counts
ancestors of that type and `$parent[StackPanel]` from inside a StackPanel is the panel itself;
`Avalonia.Headless.XUnit` pins the xunit.v3 line it was built against (3.2.x for 12.1); the new
`dotnet test` forwards unknown flags such as `--nologo` to the test app, which then prints its
help and reports zero tests. From the first hands-on pass: a `Button` handles its own press and
release before any handler attached to it runs, so a drag that starts on a button is watched from
an ancestor with a tunnelling handler; a nested button's `Click` bubbles through the outer
button's `Click`, so the outer handler checks `e.Source`; and controls inside an element marked
`WindowDecorationProperties.ElementRole="TitleBar"` need `ElementRole="User"` or the platform
takes every press on them as a window move. The headless mouse (`MouseDown`, `MouseMove` with
`RawInputModifiers.LeftMouseButton`, `MouseUp`) drives all of this in tests. The trial also showed
that two front ends sharing `%APPDATA%\GitAlert` poll the same repositories twice and overwrite
each other's settings, so the Avalonia build now holds the WPF build's mutex and answers its named
event: one GitAlert per session, whichever was launched first, and the other one wakes it and exits.
A six-part code review after the cutover (1.22.0) added three more lessons: a window that cancels
`Closing` to hide itself must let `ApplicationShutdown`/`OSShutdown` through, or Windows reports
the app as blocking the sign-out (WPF ignored the cancel; Avalonia's lifetime honours it); the
lifetime closes every window before `Exit`, and a closed window reports its `Position` as the
origin, so the shell must save a placement remembered while the window was on screen; and
`Show()` marks the window visible before its first layout pass, so a `SizeChanged` handler runs
during `Show()` and must not be mistaken for the user resizing.
The first feature built on the Avalonia side alone (1.23.0) was sections in the list, with
"Expand all" and "Collapse all" above it: the list became a flat `Rows` collection of two row
kinds picked by `Window.DataTemplates`, the total project order is laid out area by area after
every edit so the arrows can walk a project across a section edge, and the collections are
synchronised in place rather than cleared, so a poll no longer rebuilds every row container. Two
Avalonia lessons from it: a `Button`'s `Command` fires only from the button's own click, so a
header with tool buttons inside can bind its command directly, while a `Click` handler would also
see the tools' clicks bubbling through; and a control born visible never raises an `IsVisible`
change, so anything that waits for one must also listen for `AttachedToVisualTree`.

- Add the Avalonia app project next to the WPF one, both on Core.
- Port `FlyoutWindow`, `SettingsWindow`, `Controls.xaml` and the theme dictionaries. Themes become
  `ThemeVariant` resource dictionaries; `DataTemplate.Triggers` become style selectors and
  pseudoclasses; `PasswordBox` becomes a masked `TextBox` with the same clipboard rules.
- `GitAlert.Windows` carries the Win32 tray, DPAPI, Registry, DWM and positioning code with no
  UI framework attached, so the WPF app and the Avalonia app run the very same tray code.
- Build the headless render tool and compare PNGs with `docs/screenshots` in both themes.
- Replace `OnStaThread` tests with `Avalonia.Headless.XUnit` tests.

Acceptance: side-by-side screenshots match the WPF build within an agreed tolerance; every action
is reachable by keyboard with visible focus; the Avalonia build runs as the daily tray app on this
PC for at least a week without a regression report.

### Phase 3: macOS and Linux

- Implement the platform matrix for both systems, including per-OS `IAppPaths`.
- CI: `ci.yml` becomes a matrix over `windows-latest`, `macos-latest`, `ubuntu-latest`;
  headless tests need no display.
- Release: one build job per OS uploads its packages; a final `publish` job downloads everything
  and creates the GitHub Release. Actions stay SHA-pinned; new ones are added only after checking
  the SHA.
- Packaging: `.app` + DMG for `osx-x64` and `osx-arm64` with an ad-hoc signature; AppImage, `.deb`
  and `tar.gz` for `linux-x64`; Windows unchanged.
- Verification: Linux visually through WSLg (install an Ubuntu distribution; only
  `docker-desktop` exists today). macOS through CI headless tests plus a run on a real Mac.

Acceptance: green CI on three operating systems; the DMG opens after "Open Anyway" and the tray
icon appears; the AppImage runs on Ubuntu with the AppIndicator extension and on KDE; tokens
survive an app restart on each platform.

### Phase 4: cutover (ships as 2.0.0)

- Delete the WPF project and the RenderTargetBitmap tooling.
- README: platform list, install steps per OS, Gatekeeper and GNOME tray notes, screenshots from
  all three systems. Update the private rule file for Avalonia idioms and per-OS install steps.
- Tag `v2.0.0`; the release carries all seven assets.

Acceptance: a Windows 1.x install upgrades in place; a fresh install works on each platform.

## Release assets

```
GitAlert-Setup-X.Y.Z-x64.exe          GitAlert-X.Y.Z-win-x64-portable.zip
GitAlert-X.Y.Z-osx-arm64.dmg          GitAlert-X.Y.Z-osx-x64.dmg
GitAlert-X.Y.Z-linux-x64.AppImage     gitalert_X.Y.Z_amd64.deb
GitAlert-X.Y.Z-linux-x64.tar.gz
```

## Risks and how we hold them

- **Windows visual fidelity.** The Fluent look is the product's value on Windows. The screenshot
  comparison in phase 2 is the gate; the WPF build keeps shipping until it passes.
- **Gatekeeper on macOS.** Without notarization, macOS 15 shows no "Open" shortcut on right-click.
  README documents System Settings > Privacy & Security > Open Anyway. Revisit an Apple Developer
  account before 2.0.0.
- **GNOME has no tray.** Stock GNOME needs the AppIndicator extension. README says so; the app
  still shows the flyout when launched again while running.
- **Secret storage on Linux without a Secret Service.** The file fallback is a real downgrade, so
  the settings window says which store is in use.
- **Verification without a Mac.** CI proves it builds and the headless tests pass. The visual and
  tray checks in phase 3 need a person with a Mac before 2.0.0 ships.
- **Package size.** Self-contained publishes are roughly 70 to 80 MB per platform; acceptable,
  trimming stays off as today.

## Open decisions

- Apple Developer account for notarization: decide before the 2.0.0 tag.
- Minimum macOS version: take it from Avalonia 12's support matrix in phase 3.
- Windows installer scope: today's machine has an all-users install under Program Files while the
  script declares per-user; settle it when the installer is touched in phase 3.
- rpm and Flatpak: after 2.0, on request.
