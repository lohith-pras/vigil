# Vigil: working notes for Claude

Windows WPF tray app (.NET 10). Runs only on Windows: on macOS, `dotnet build src/Vigil` is the local check; the real check is the CI self-test (`ci-reader` agent reads it). Spec: `docs/SPEC.md`, design: `docs/DESIGN.md`. Performance budget (≈10 MB idle, <1 % CPU, nothing sampled unless shown) applies to every change.

## Model and effort routing (save usage)

Default session model is Opus 5.5 (`.claude/settings.json`). You can't switch your own model, so:

- **At the start of a task that belongs to Sonnet** (list below) while running on Opus: say so in one line and suggest `/model sonnet`, then carry on either way.
- **At the start of a task that belongs to Opus** while running on Sonnet: say so in one line and suggest `/model opus`, then continue only if the user says so.
- **Effort:** start at `medium`. Suggest `/effort high` only after a step failed its check (build, self-test), not up front.
- **Switch at breaks:** before a model change suggest `/compact` or a fresh session that starts from the plan file, not mid-task.

| Work | Who |
|---|---|
| Docs (README, PRIVACY, CODE-SIGNING), CI YAML, SignPath/winget steps, small fixes from a written plan | Main session, Sonnet |
| New features, design/motion, security review + fixes, anything without a written plan | Main session, **Opus** |
| Finding code, call sites, "where is X" | `Explore` agent (Haiku) |
| CI results, failing logs, self-test report/screenshots | `ci-reader` agent (Haiku) |
| Independent security review before a signed release | `security-reviewer` agent (Fable), only when the user asks |

- Delegate reading, never edits: code edits and judgment calls stay in the main session.
- Don't spawn other agents (general-purpose, Plan, workflows) unless asked; they default to Sonnet.
- Keep Haiku inputs small (`gh run view --log-failed`, excerpts): Haiku costs 5× above 100K tokens.
