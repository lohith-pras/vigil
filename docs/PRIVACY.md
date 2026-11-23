# Privacy

Vigil has no telemetry, no analytics, no accounts and no crash reporting. It reads your PC's sensors locally and shows them to you; nothing leaves the machine except the three requests below.

## Network requests

| When | Where | What's sent |
|---|---|---|
| **Check for updates** (click), plus a quiet check 2 minutes after start and every 6 hours unless you turn off *Settings → Check for updates automatically* | `api.github.com`, then `github.com` release downloads if you accept an update | A normal HTTPS request with the user agent `Vigil/<version>`. |
| **Run speed test** (click only) | `speed.cloudflare.com` | Test downloads and uploads of random bytes, up to about 67 MB; can be cancelled. |

GitHub and Cloudflare see your IP address, as with any web request. Vigil sends nothing else: no IDs, no hardware details, no usage data.

## What's stored on your PC

- `%APPDATA%\Vigil\settings.json`: your options (keep-awake settings, watched app names, alert switches, last announced version). A few hundred bytes.
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Vigil`: only if you turn on *Start with Windows*.
- During an update, `Vigil.exe.new` / `Vigil.exe.old` next to the exe, removed on the next start.

To remove everything: quit Vigil, turn off *Start with Windows* first (or delete that registry value), delete the exe and the `%APPDATA%\Vigil` folder.
