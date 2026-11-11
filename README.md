# Vigil

A tiny Windows tray utility that does exactly three things:

1. **Tray icon + popup panel**: live CPU % drawn in the tray icon and a compact flyout panel.
2. **Live system readouts**: CPU (with 60 s sparkline), RAM, disk space per drive, network speed, battery.
3. **Keep awake**: stop the PC sleeping (and optionally the display turning off), with timer presets.

Inspired by the macOS menu-bar toolkit Vorssaint, cut down to just these three features.

> **Status:** planning. No code yet. See [docs/SPEC.md](docs/SPEC.md).

## Download

Grab `Vigil.exe` from the [latest release](https://github.com/lohith-pras/vigil/releases/latest).
It needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64), installed once.

The exe is not code-signed, so on first run Windows SmartScreen may warn you: click **More info → Run anyway**.

## Updating

Right-click the tray icon → **Check for updates**. If a newer release exists, Vigil downloads it, swaps itself and restarts.
Vigil makes no network calls unless you press that button.

## Build

_Coming with the first code commit._

## License

MIT
