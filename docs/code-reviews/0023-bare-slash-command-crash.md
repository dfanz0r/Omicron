# Code Review 0023: Bare Slash Command Crash

Date: 2026-05-08

## Issue

Typing a bare slash in chat crashed the CLI:

```text
You: /
Unhandled exception. System.IndexOutOfRangeException
```

## Root Cause

`SlashCommandDispatcher.Execute()` stripped the leading slash, split the remaining text, then accessed `parts[0]` without checking if any command text remained.

For `/`, `cmdText` was empty and `parts` was empty.

## Fix

Updated `Omicron.CLI/SlashCommandDispatcher.cs`:

```csharp
var cmdText = trimmed.StartsWith('/') ? trimmed[1..] : trimmed;
if (string.IsNullOrWhiteSpace(cmdText))
    return ShowHelp();
```

A bare `/` now shows help instead of crashing.

## Validation

```text
dotnet build Omicron.slnx --nologo
0 warnings, 0 errors

dotnet test Omicron.slnx --nologo
Passed: 187
```
