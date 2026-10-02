# Unreal Commander
Desktop tooling for running local Unreal Engine automation workflows.

## Formatting

This repository uses `.editorconfig` as the source of truth for C# formatting rules and `dotnet format` as the standard formatter.

From the repository root, run the formatter across the solution:

```powershell
dotnet format UnrealCommander.sln
```

Verify that the current tree already matches the configured format rules:

```powershell
dotnet format UnrealCommander.sln --verify-no-changes
```
