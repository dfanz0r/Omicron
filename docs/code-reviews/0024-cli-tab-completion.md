# Code Review 0024: CLI Tab Completion

Date: 2026-05-08

## Change

Added basic tab completion to the chat line editor.

## Behavior

In chat input:

- Press `Tab` on an empty line to list slash commands.
- Type a partial slash command and press `Tab` to complete it.
- If multiple commands match, they are listed and the prompt is redrawn.
- `/model <partial>` completes visible model catalog keys.

Examples:

```text
/st<Tab>        -> /status
/pro<Tab>       -> /provider-state
/model or:o<Tab>
```

## Files Changed

```text
Omicron.CLI/LineEditor.cs
Omicron.CLI/SlashCommandDispatcher.cs
Omicron.CLI/Program.cs
```

## Validation

```text
dotnet build Omicron.slnx --nologo
0 warnings, 0 errors

dotnet test Omicron.slnx --nologo
Passed: 187
```

## Notes

This is intentionally simple completion:

- completion only applies when the cursor is at the end of the input;
- model completion is by catalog key, not display name;
- if multiple matches exist, candidates are printed.
