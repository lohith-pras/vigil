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
| WinForms | **WPF** (.NET 10; own dark/light palette following the Windows setting, Win11 rounded corners; Mica glass is a later polish) for the panel; tray via WinForms `NotifyIcon` (`UseWindowsForms` alongside WPF). Fall back to WinForms if idle RAM breaks 50 MB at build step 1. | Closer to the Vorssaint look (glass, rounded cards, smooth toggles). |
| Refresh 1 s open / 2–3 s background | Sample everything every 1 s; tray icon + tooltip redraw every 2 s (only when the value changes) | One timer instead of two; sampling is a few cheap system calls, and the 60 s sparkline always has full data. |
| CPU % in icon | Capped at 99 | Three digits don't fit legibly in a 16 px icon. |
| Keep-awake on restart | Always starts off; only "Keep display on" is remembered | A forgotten keep-awake could stop the laptop sleeping in a bag. |
| Panel positioned at the icon | Positioned at the click point, against the taskbar edge of that monitor | WinForms doesn't expose the tray icon's rectangle; the click point is the icon. |

**Build note:** WPF builds from macOS/Linux too (`EnableWindowsTargeting`), so the code can be compile-checked anywhere. Releases are built by GitHub Actions (`windows-latest`); running and debugging needs Windows.

## Design reference: Vorssaint (observed from its shipped binary, v3.4.4)

Type names in the app show a SwiftUI panel (`MenuPanelView` + `MenuPanelHeader`) on a glass background (`NSPanel` + `NSVisualEffectView`), anchored under the menu-bar item (`AppDelegate.PanelAnchor`). Content is stacked collapsible cards (`DiskSection`, `NetworkSection`, `KeepAwakeCard`) with history graphs (`MetricHistory`) and usage bars (`DiskUsageBar`). The keep-awake card has duration chips, a countdown, a display-sleep option and an active-state menu-bar icon. Menu-bar metrics are small label-over-value tiles ("CPU / 12%").

### What the open panel looks like (from screenshots)

- Dark rounded panel (~16 px corners), slightly translucent, with a small caret pointing at the menu-bar icon.
- App logo centred at the top.
- **Icon tab bar** under the logo: one icon per section in a rounded pill row; the selected tab gets a blue-tinted square.
- Section title in small uppercase grey (`SYSTEM`, `KEEP AWAKE`), with a settings-sliders icon on the right.
- Content sits in one rounded **card**, a shade lighter than the panel, thin subtle border. Sub-headings in bold ("Hardware usage", "Memory"), separated by thin dividers.
- Metric rows: label left, thin blue progress bar in the middle, bold value right-aligned (`CPU ▬▬▬── 36%`). Below it a line graph with a dashed "100%" ceiling line and a faint gradient fill.
- Status pills: rounded, tinted badge with a coloured dot (e.g. yellow "Caution").
- Footer row inside the card with a small icon ("Up for 34d 3h").
- Bottom: two equal-width rounded buttons, **Settings** and **Quit**.
- Keep-awake tab: status sentence + large toggle on the first row ("The Mac follows its normal energy rules"); a 4-column grid of duration chips (15m 30m 1h 2h / 4h 8h ∞ Until…); grey hint "Click a chip to start. Click it again to stop"; collapsible "Options"; extra toggle rows with a subtitle.

### Vigil's panel

Same layout, Windows-flavoured:

- Mica/acrylic panel with Win11 rounded corners; no caret (Windows flyouts don't have one).
- Tab bar with 4 tabs: **Keep awake · System (CPU + RAM) · Network + Battery · Disk**.
- System tab: CPU row with bar + 60 s graph (dashed 100 % line); RAM row with bar, "9.1 / 16 GB" and %.
- Disk tab: one bar row per fixed drive, free / total, bar and value turn red above 85 % used.
- Network + Battery tab: ↓ / ↑ speeds with a small graph; battery %, a "Charging" / "On battery" pill, time remaining.
- Keep awake tab: status sentence + toggle, chips **30m · 1h · 2h · ∞** (spec presets), countdown while active, "Keep display on" toggle row.
- Bottom buttons: **Settings** (Start with Windows, Check for updates) and **Quit**.
- Not copied: temperatures (non-goal), lid-closed option (changes a Windows power setting, out of scope), uptime row (not in spec; trivial to add in v2).

## Build order

1. Tray icon + right-click menu + Quit (appears and disappears cleanly; measure idle RAM here).
2. Panel opens/closes correctly.
3. Readouts, CPU and RAM first.
4. CPU % in tray icon.
5. Keep awake with timer.
6. Start with Windows + settings file (`%APPDATA%\Vigil\settings.json`).
7. Check for updates + GitHub Actions release workflow.
8. Polish: theme, sparkline, red drive highlight.

## v0.2 (agreed 2026-11-11)

Built one feature per branch (`feature/…`), each verified by the CI self-test on Windows, then merged into `main`. No tag / release until explicitly asked.

Panel grows to 6 tabs: **Keep awake · Displays · System · Processes · Network & battery · Disks**.

**Status (v0.2.0):** keep-awake upgrades, Bluetooth batteries, Processes, the tray app icon and Check for updates shipped; **Displays postponed** to a later release (5 tabs for now).

### 1. Keep-awake upgrades (`feature/keep-awake`)

- More chips: **15m · 30m · 1h · 2h · 4h · 8h · ∞ · Until…** (Until… = pick a clock time; earlier than now means tomorrow).
- Collapsible **Options**:
  - Keep display on (existing).
  - **Only when plugged in**: on battery the request is paused, resumes when plugged back in.
  - **Pause when locked**: paused while the PC is locked (Win+L), resumes on unlock.
  - **Turn on when Vigil starts**: indefinite keep-awake at launch.
  - **While these apps are open**: list of apps (picked from running apps); while any runs, keep awake applies automatically, even if the manual toggle is off.
- Status line says why it's on or paused ("because OBS is open", "paused: on battery").
- Tray dot shows only while the PC is actually being held awake.
- All options saved in `settings.json`.

### 2. Bluetooth device batteries (`feature/bt-batteries`)

- In Network & battery tab, under Battery: **Devices** section, one row per Bluetooth device that reports a battery level to Windows (name, bar, %).
- Source: the Windows device property Settings uses (`{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2`) via SetupAPI. No admin, no driver, no network.
- Refreshed every 60 s and when the panel opens. Devices that report nothing are hidden; if none: "No Bluetooth devices report battery".
- Limits: only what Windows itself shows; value can lag until the device reports again.

### 3. Processes + End task (`feature/processes`)

- **Processes** tab: top apps by CPU (toggle: by RAM), grouped by name with instance count, CPU % and RAM.
- **End task**: two-step (click → "End?" for 3 s → click again). Ends every process of that name. Shows an error if Windows refuses (e.g. elevated or system processes).
- Sampled every 2 s **only while this tab is open** (keeps background CPU under 1 %).

### 4. Displays — moved to v0.3 (see below)

- **Displays** tab, one card per screen:
  - Built-in screen: brightness slider (WMI `WmiMonitorBrightness`; needs the `System.Management` package).
  - External monitors: brightness, contrast, speaker volume via **DDC/CI** (`dxva2.dll`). Monitors that don't answer show "This monitor doesn't support control". Calls run off the UI thread.
  - Refresh rate buttons for the screen's current resolution (e.g. **60 Hz · 144 Hz**).
- Below the cards: display mode **Extend · Duplicate · Laptop only · External only** (`SetDisplayConfig`, same as Win+P), and **Turn screens off** (until mouse/keyboard).
- Not doing: input switching (can blank the screen), dimming below minimum, night light, resolution changes.


### Added during v0.2 (from 0.1.3 feedback)

- **Tray shows the app icon** instead of the CPU number (the number wasn't understood). Keep-awake adds a green dot. "Show CPU % in tray icon" (Settings / right-click menu) brings the number back.
- **Check for updates** (Settings / right-click menu): only network call, only on click. Downloads the same flavour that's running, verifies size + SHA-256 from the release, swaps the exe, restarts.

## v0.3 (agreed 2026-11-13)

Built in parallel: each feature on its own `feature/…` branch, CI self-test green, merged into `main` by the coordinating session. No tag / release until asked. A **design pass** (darker black, acrylic glass, smoother) runs at the same time on `feature/design`.

To keep merges small while the design pass rewrites `PanelWindow.xaml`: each new tab/section lives in **its own UserControl file** (e.g. `DisplaysPage.xaml`), uses the existing `DynamicResource` brush keys and named styles, and touches `PanelWindow` only to add a tab + host it.

### 1. Displays (`feature/displays`)

As specified in v0.2 §4 above: per-screen cards (built-in brightness via WMI; external brightness / contrast / volume via DDC/CI), refresh-rate buttons, display mode (Extend · Duplicate · PC screen only · Second screen only via `SetDisplayConfig`), Turn screens off.

### 2. Battery health (`feature/battery-alerts-speedtest`)

- In the battery section: **health %** = full-charge capacity ÷ design capacity, both in mWh, plus **charge cycles** if the battery reports them.
- Source: battery driver queries (`IOCTL_BATTERY_QUERY_INFORMATION` on the battery device, no admin) or WMI `root\WMI` battery classes. Read on panel open, not every second.
- Hidden on PCs without a battery.

### 3. Alerts (`feature/battery-alerts-speedtest`)

Windows notifications from the tray icon, each with an on/off switch in Settings:
- Battery low (≤ 20 %, again at ≤ 10 %) while on battery.
- Battery charged (100 %, or a chosen limit like 80 %) while plugged in.
- Drive almost full (> 90 % used).
- CPU or RAM above 90 % for 2 minutes.

Each alert fires once, then only again after the condition has cleared. No network.

### 4. Network speed test (`feature/battery-alerts-speedtest`)

- **Run speed test** button in Network & battery: download speed, upload speed, latency (ping).
- Uses Cloudflare's public speed-test endpoints (`speed.cloudflare.com`), no account or key. **Second network exception** (after updates): only runs when clicked, shows how much data it will use (~30–100 MB) and can be cancelled.

### Dropped

GPU usage, per-core CPU grid, disk activity, memory-pressure pill, uptime: removed from the plan.
