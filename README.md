# Vigil

A tiny Windows tray utility that does exactly three things:

1. **Tray icon + popup panel**: live CPU % drawn in the tray icon and a compact flyout panel.
2. **Live system readouts**: CPU (with 60 s sparkline), RAM, disk space per drive, network speed, battery.
3. **Keep awake**: stop the PC sleeping (and optionally the display turning off), with timer presets.

Inspired by the macOS menu-bar toolkit Vorssaint, cut down to just these three features.

> **Status:** early. Step 1 of 8 done (tray icon + Quit). Features below arrive step by step; see [docs/SPEC.md](docs/SPEC.md).

## Download

Grab `Vigil.exe` from the [latest release](https://github.com/lohith-pras/vigil/releases/latest).
It needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64), installed once.

The exe is not code-signed, so on first run Windows SmartScreen may warn you: click **More info → Run anyway**.

## Updating

Right-click the tray icon → **Check for updates**. If a newer release exists, Vigil downloads it, swaps itself and restarts.
Vigil makes no network calls unless you press that button.

No runtime install? Download `Vigil-standalone.exe` instead (bigger, runs on its own).

## Build

Windows only (WPF). Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet run --project src/Vigil                 # run from source
dotnet publish src/Vigil -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
```

No SDK? Every push to `main` is built by GitHub Actions: open the latest run under **Actions** and download the `Vigil` artifact.

## Releasing

```bash
git tag v0.1.0 && git push origin v0.1.0
```

GitHub Actions builds both exes and publishes them as a release.

## Start with Windows

Right-click the tray icon → **Start with Windows**. (Adds Vigil to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`; no admin needed.)

## License

MIT
