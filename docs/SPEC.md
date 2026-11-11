# Vigil — spec

Small, always-running Windows tray app. Exactly three features. Anything else is v2 and gets discussed first.

Target machine: ASUS TUF Gaming A15 (FA506IV), Ryzen 7 4800H, RTX 2060, Windows x64. C: is nearly full: no caches or logs on disk beyond one tiny settings file.

## 1. Tray icon + popup panel

- Lives in the notification area. No main window, no taskbar button.
- Left-click: compact panel just above the tray, aligned to the icon (like the Wi-Fi/volume flyouts).
- Closes on click outside or Esc.
- Right-click menu: Start with Windows (toggle), Keep awake (toggle), Check for updates, Quit.
- Positions correctly with taskbar on any edge, scaling 100/125/150 %, multiple monitors.
- Dark by default; follows the Windows light/dark setting.

## 2. Live readouts

Refresh 1 s while the panel is open, 2–3 s in background (tray icon/tooltip).

- **CPU**: total %, sparkline of last 60 s.
- **RAM**: used / total GB and %.
- **Disk**: free / total per fixed drive (C:, D:, E:). Red above 85 % used. Refresh every 30 s.
- **Network**: down/up speed, auto-scaled KB/s or MB/s, summed over active adapters, excluding loopback and virtual.
- **Battery**: %, charging or not, time remaining when Windows provides it.

Tray icon shows CPU % as a number (16×16 / 32×32, readable). Tooltip: one-line summary, e.g.
`CPU 12% · RAM 9.1/16 GB · ↓ 1.2 MB/s ↑ 80 KB/s · 🔋 76%`.

## 3. Keep awake

- Toggle in panel and right-click menu. Two options: prevent sleep; also keep display on.
- `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED [| ES_DISPLAY_REQUIRED])` from the UI thread.
- Timer presets: indefinitely, 30 min, 1 h, 2 h. Show remaining time.
- Tray icon shows an indicator (dot / colour) while active.
- Must reset (`ES_CONTINUOUS` only) when switched off, on quit, and on crash/kill where possible. (When the process dies, Windows drops its execution state automatically.)

## Non-goals

- No CPU/GPU temperatures (needs a kernel driver that Defender flags).
- No volume mixer, clipboard history, window snapping, app switcher, uninstaller, launcher.
- No telemetry, no accounts. No background network calls (see update exception below).
- No Electron.

## Performance

- Idle RAM under ~50 MB; CPU under 1 % averaged with panel closed.
- No admin rights. Starts in under 1 s.

## Known pitfalls to handle

- Per-monitor DPI awareness (PerMonitorV2) for popup position.
- Panel closing on focus loss (and not instantly reopening when the tray icon itself is clicked).
- Dispose old tray icons + `DestroyIcon` on each redraw.
- Discard the first CPU sample.
- `notifyIcon.Dispose()` on quit (no ghost icon).
- Keep-awake reset on exit.

## Deliverables

1. Source in this repo, README with build / run / start-with-Windows.
2. Release: framework-dependent single exe (needs .NET 10 Desktop Runtime), plus self-contained if feasible.
3. A note listing spec changes and why (below).

## Changes from the original spec

| Original | Now | Why |
|---|---|---|
| .NET 8 | .NET 10 | .NET 8 support ended 10 Nov 2026; .NET 10 is the current LTS. |
| No network calls, no auto-updater | One exception: **Check for updates** button in the right-click menu. Only runs when clicked. Queries GitHub Releases, downloads the new exe, swaps it in, restarts. | So a friend can update without re-downloading by hand. Nothing runs in the background. |
| WinForms | **Proposed:** WPF (Fluent theme, Mica) for the panel; tray via `NotifyIcon`. Fall back to WinForms if idle RAM breaks 50 MB. | Closer to the Vorssaint look (glass, rounded cards). Not decided yet. |

## Design reference: Vorssaint (observed from its shipped binary, v3.4.4)

Type names in the app show a SwiftUI panel (`MenuPanelView` + `MenuPanelHeader`) on a glass background (`NSPanel` + `NSVisualEffectView`), anchored under the menu-bar item (`AppDelegate.PanelAnchor`). Content is stacked collapsible cards (`DiskSection`, `NetworkSection`, `KeepAwakeCard`) with history graphs (`MetricHistory`) and usage bars (`DiskUsageBar`). The keep-awake card has duration chips, a countdown, a display-sleep option and an active-state menu-bar icon. Menu-bar metrics are small label-over-value tiles ("CPU / 12%").

Vigil copies that structure: four cards (CPU+RAM, Disk, Network+Battery, Keep awake) on a Mica panel.

## Build order

1. Tray icon + right-click menu + Quit (appears and disappears cleanly; measure idle RAM here).
2. Panel opens/closes correctly.
3. Readouts, CPU and RAM first.
4. CPU % in tray icon.
5. Keep awake with timer.
6. Start with Windows + settings file (`%APPDATA%\Vigil\settings.json`).
7. Check for updates + GitHub Actions release workflow.
8. Polish: theme, sparkline, red drive highlight.
