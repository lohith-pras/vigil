# Vigil

A tiny Windows tray utility, inspired by the macOS menu-bar toolkit Vorssaint.

- **Tray icon + popup panel**: click the eye icon in the tray for a compact flyout panel. Hover for a one-line summary.
- **Live readouts**: CPU and RAM (60 s graphs), disk space per drive, network speed, battery (with health % and charge cycles), Bluetooth device batteries.
- **Speed test**: download, upload and ping on click (Cloudflare's public speed test).
- **Alerts**: Windows notifications for battery low / charged, drive almost full, CPU or RAM busy; each one switchable (Network & battery tab → Alerts).
- **Processes**: top apps by CPU or RAM, with End task.
- **Keep awake**: stop the PC sleeping (optionally the display too) for 15m–8h, until a time, or indefinitely; only when plugged in, pause when locked, or automatically while chosen apps are open.

> **Status:** v0.2. Next: a Displays tab (brightness, refresh rate, display mode). See [docs/SPEC.md](docs/SPEC.md).

## Download

Grab it from the [latest release](https://github.com/lohith-pras/vigil/releases/latest):

- `Vigil-standalone.exe`: runs with nothing installed (≈ 148 MB on disk).
- `Vigil.exe` (recommended): 0.4 MB, same memory use, smaller updates; needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64), installed once.

The exe is not code-signed, so on first run Windows SmartScreen may warn you: click **More info → Run anyway**.

## Updating

Panel → **Settings → Check for updates…** (or right-click the tray icon). If a newer release exists, Vigil downloads it, checks it against the release's checksum, swaps itself and restarts.

Vigil makes network calls only when you press one of two buttons: **Check for updates** (GitHub) and **Run speed test** (`speed.cloudflare.com`; uses up to about 67 MB, less on slower lines, and can be cancelled). Nothing runs in the background.

## Build

Windows only to run (WPF); compiles on any OS with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet run --project src/Vigil                 # run from source
dotnet publish src/Vigil -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
```

Every push to `main` or a `feature/**` branch is built by GitHub Actions, which also runs `Vigil.exe --selftest` on Windows (opens the panel, clicks through everything, saves screenshots). Download the `Vigil` and `selftest` artifacts from the run.

## Releasing

```bash
git tag v0.2.0 && git push origin v0.2.0
```

GitHub Actions builds both exes, runs the self-test, and only then publishes the release.

## Start with Windows

Panel → **Settings → Start with Windows** (or right-click the tray icon). Adds Vigil to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`; no admin needed.

## License

MIT
