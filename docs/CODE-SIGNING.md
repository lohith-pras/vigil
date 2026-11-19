# Code signing policy

> **Status:** not signed yet. An application to the SignPath Foundation is planned; until it's approved, releases are unsigned and Windows SmartScreen warns on first run.

Once approved: free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## What gets signed

Only `Vigil.exe` and `Vigil-standalone.exe`, built by GitHub Actions from a `v*` tag of this repository ([build.yml](../.github/workflows/build.yml)), after the self-test, CodeQL, gitleaks and zizmor checks pass. Nothing built on a personal machine is ever signed.

## Roles

| Role | Who |
|---|---|
| Author, reviewer, approver | [@lohith-pras](https://github.com/lohith-pras) |

Every signing request is approved manually in the protected `release` environment. Contributions from others are reviewed before they're merged into `main`.

## Privacy

See [PRIVACY.md](PRIVACY.md). Vigil sends no data other than the update check and the speed test you start yourself.
