# Code Review 0022: CLI Model Selection via Slash Commands

Date: 2026-05-08

## Change

Removed the initial model-selection menu from CLI startup and moved model listing/switching into slash commands.

## Behavior Now

On startup, the CLI:

1. loads config/catalog;
2. selects the last-used visible model if available;
3. otherwise selects the first visible model;
4. starts chat immediately.

Inside chat:

```text
/models             list available models
/model              show current model details
/model <number>     switch to model by /models number
/model <key>        switch to model by catalog key
/help               show commands
/exit or /quit      exit app
```

Switching model creates a new `AgentSession`; it does not mutate the current session's model or carry provider continuation state across sessions.

## Files Changed

```text
Omicron.CLI/Program.cs
Omicron.CLI/SlashCommandDispatcher.cs
docs/implementation-plans/0002.5-cli-slash-commands.md
docs/rfcs/IMPLEMENTATION-BASELINE.md
```

## Validation

```text
dotnet build Omicron.slnx --nologo
0 warnings, 0 errors

dotnet test Omicron.slnx --nologo
Passed: 187
```

## Manual Test Checklist

```text
/help
/model
/models
/model 1
/model or:openai/gpt-5.4-mini
/status
/reset
/exit
```

Expected:

- app starts directly in chat;
- `/models` shows the model list;
- `/model <n|key>` switches model and starts a fresh session;
- `/exit` exits the application because there is no longer a model picker to return to.
