# Security

## Reporting a vulnerability

Please report it privately: **[Report a vulnerability](https://github.com/lohith-pras/vigil/security/advisories/new)** (GitHub private vulnerability reporting). Don't open a public issue. You'll get a reply within a week; a fix ships as a new release, and you're credited in its notes unless you'd rather not be.

## Supported versions

Only the latest release gets fixes. Vigil updates itself (Settings → Check for updates), so staying current is one click.

## What Vigil defends against

| Attacker | Way in | Defence |
|---|---|---|
| Network (bad Wi-Fi, proxy) | Update check and download, speed test | HTTPS only. The updater installs only an asset under `https://github.com/lohith-pras/vigil/releases/download/`, with the expected size, whose SHA-256 matches the digest GitHub publishes for it; a release without a digest is refused. Never downgrades. |
| A malicious DLL next to the exe (e.g. in Downloads) | DLL planting at start-up | Native DLLs load from System32 only (`DefaultDllImportSearchPaths`); the CI self-test checks the loaded paths. |
| Another program running as you | Editing `%APPDATA%\Vigil\settings.json` | Corrupt files fall back to defaults; out-of-range values are clamped. Nothing in the file is executed or used as a path. |
| Elevation | — | Vigil runs `asInvoker` and never asks for admin. End task can't touch elevated or core Windows processes. |
| A compromised GitHub account or CI | A malicious release | Releases are built only from `v*` tags, after the self-test, CodeQL, gitleaks and zizmor pass, and need manual approval in the `release` environment. Actions are pinned to commit SHAs. |

Out of scope: an attacker who already has admin or kernel access to the PC.

## Accepted risks

- **Same-user file swap during an update.** Between verifying `Vigil.exe.new` and moving it into place, another program running as you could replace it. That program could already replace `Vigil.exe` itself, so this adds nothing new.
- **Single-instance mutex squatting.** Another program in your session can hold `Local\Vigil.SingleInstance` and stop Vigil from starting (denial of service only).
- **Unsigned builds.** Until code signing is in place (see [docs/CODE-SIGNING.md](docs/CODE-SIGNING.md)), Windows SmartScreen warns on first run and the updater trusts the GitHub release plus its SHA-256 digest.

## Automated checks (every push)

CodeQL (C#), .NET security analyzers as errors, vulnerable NuGet package check, gitleaks over the full history, zizmor on the workflows, Dependabot for actions and NuGet, and the Windows self-test.
