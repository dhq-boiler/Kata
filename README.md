# Kata

AI-collaborative UML class diagram editor for real codebases.

Kata reads a `.sln` (or `.slnx`) directly, renders it as an interactive class
diagram, detects code smells, and lets you drive whole refactorings — Fowler
catalog + your own AI-assisted intents — from the diagram surface. Changes
flow **back** into the source (Roslyn workspace / C++/CLI project files) so
the diagram stays a live view of the code, not a static picture.

**Supported inputs**

- C# projects (via Roslyn) — full symbol semantics, cross-project refs
- C++/CLI projects (via a custom Roslyn-flavoured layer in `Kata.Cpp`) — inline
  members, macros, hybrid symbol resolution across the C# ↔ C++/CLI boundary
- Mixed C# + C++/CLI solutions (the main dogfooding target)

**Highlights**

- **Ctrl+Click navigation** on any type or member, cross-language
- **Fowler refactoring catalog** — Extract Method / Extract Interface / Rename /
  Move Method / etc., surfaced on the diagram and also callable from an MCP
  tool so external AI agents can drive them
- **Code smell analyzer** — 24 smells, badged as 💩 on nodes and members
- **AI diff apply** — right-click a smell → "Ask AI (Claude / Codex)" → the
  agent produces a unified diff → preview → apply
- **Live graph** — sln reload isn't required after refactors; the diagram
  adapter updates incrementally

## Status

Kata is under active solo development by [@dhq-boiler](https://github.com/dhq-boiler).
It works well on the codebases it was built against, but the surface is wide
and the edges are still rough. Bug reports and reproducible examples are the
most useful form of contribution right now.

## Community vs Pro

Kata ships in two editions:

|                    | **Community** (this repo) | **Pro** ([kata.dhq-boiler.dev/pro](https://kata.dhq-boiler.dev/pro)) |
| :----------------- | :------------------------ | :------------------------------------------------------------------- |
| License            | PolyForm NC (source-available, free for noncommercial use) | Commercial (proprietary plugin) |
| Editor / diagram   | ✅ full                   | ✅ same base                                                         |
| Refactorings       | ✅ Fowler catalog         | ✅ + Team Collab features (planned)                                  |
| Code smell badges  | ✅ 24 smells              | ✅ same                                                              |
| AI diff apply      | ✅ **10 uses / month**    | ✅ unlimited                                                         |
| License upgrade    | —                          | Drop `Kata.App.Pro.dll` next to `Kata.App.exe` + enter key           |
| Pricing            | Free                       | $49 buyout / $150 seat·yr / $290 Business seat·yr                    |

Community is the same binary customers install; Pro is a small plugin DLL that
the Community loader picks up when a valid license key is present. There is
no separate installer. AI cost is on the user's own Claude / Codex subscription
either way — the Community monthly cap is a *value* gate, not a cost gate.

## Building

Requires .NET 10 SDK and Windows (WPF).

```powershell
dotnet build Kata.slnx
dotnet test  tests\Kata.Tests\Kata.Tests.csproj
dotnet run   --project src\Kata.App\Kata.App.csproj
```

The `Kata.App.Pro/` folder does **not** live in this tree — it's the private
plugin repo. Community builds fine without it; the ProLoader silently falls
back to `NoOpProFeatures`.

## Repo layout

```
src/
  Kata.Core/          — model, analysis, intents (language-neutral)
  Kata.Roslyn/        — C# language adapter (Roslyn)
  Kata.Cpp/           — C++/CLI parser + semantics
  Kata.App/           — WPF frontend
  Kata.App.PluginApi/ — public contract shared with the Pro plugin
  Kata.Mcp/           — MCP server exposing intents to external agents
tests/
  Kata.Tests/         — xUnit
tools/
  Generate-Strings.ps1
```

## AI integration

Kata talks to AI agents (Claude Code, Codex) either directly via CLI
invocation or through its own MCP server (`Kata.Mcp`). The MCP layer is a
Streamable HTTP + stateless server so multiple agents can connect
simultaneously alongside the App. This means you can drive Kata from a
running Claude Code session, and the changes it makes reflect immediately
in the diagram.

## Contributing

Issues welcome. Before opening a PR:

- Run `dotnet build Kata.slnx` and `dotnet test` — both must pass
- Match the existing style (no new comments unless they explain a
  non-obvious *why*, no dead code, no speculative abstractions)
- Note that this project is licensed **PolyForm NC**, not MIT — commercial
  use of contributed code requires the Pro license

## License

[PolyForm Noncommercial 1.0.0](./LICENSE). Free for personal, educational,
research, hobbyist, and other noncommercial use. For commercial use, buy a
Kata Pro license.

Copyright © 2026 dhq_boiler.
