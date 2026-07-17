# .agent — Agent context

Read this file first each session. These docs are the source of truth for cfGodotEngine.Generator context.

## Must read (every session)

1. [`project.md`](project.md) — what this generator is, stack, domain

Root pointer: [`AGENTS.md`](../AGENTS.md) in the tool root.

## Task routing

| If you are... | Read |
|---|---|
| Changing generated code | Source code and this `project.md` |
| Changing how CatSweeper uses generated code | CatSweeper `.agent/systems/` and this `project.md` |
| Open questions or blockers | [`pending.md`](pending.md) |

## Rules

- Keep docs in sync with code changes.
- Append one line to [`CHANGELOG.md`](CHANGELOG.md) per significant change if the file exists.
