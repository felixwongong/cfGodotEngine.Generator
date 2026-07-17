# Project

## What we are building

`cfGodotEngine.Generator` is a C# source generator for `cfGodotEngine`. It emits code at build time to reduce boilerplate in CatSweeper and other consumers of the framework.

## Stack

| Layer | Choice | Notes |
|---|---|---|
| Language | C# | .NET 8 source generator |
| Consumer | cfGodotEngine, CatSweeper | Imported as Git subtree |
| Project | `cfGodotEngine.Generator.csproj` | Build with `dotnet build` |

## Domain in one paragraph

Source generators in this project inspect consumer code (e.g., `[BindingSource]` classes) and emit implementations (e.g., `IBindingSource`, `GetBindingKeys()`, `BeginUpdate()`) at compile time. This keeps hand-written code small and rename-safe.

## Non-obvious constraints

- Checked out inside CatSweeper as a Git subtree at `Modules/cfGodotEngine.Generator/`. Only the programmer/owner pushes changes back to `https://github.com/felixwongong/cfGodotEngine.Generator.git` via `Tools/subtree.ps1`.
- Any generator change must be coordinated with `dotnet build CatSweeper.sln` because generated code is compiled into the main project.

## CatSweeper usage

CatSweeper uses generated code through `cfGodotEngine`. See CatSweeper's `.agent/systems/mvvm-binding.md` for the `[BindingSource]` pattern.

See CatSweeper's `.agent/systems/subtrees.md` for the subtree workflow.
