# Code Review 0026: Duplicate Tool Call ID Fix

Date: 2026-05-08

## Issue Observed

A Claude/OpenRouter routed backend returned:

```text
messages.3.content.1: `tool_use` ids must be unique
```

The model called the same tool twice and reused the same provider tool-call ID. When Omicron replayed the transcript, the Anthropic-compatible backend rejected duplicate `tool_use` IDs.

## Fix

Updated `Omicron.Core/Sessions/AgentSession.cs`:

- tracks tool-call IDs used in the current session;
- normalizes duplicate provider tool-call IDs before appending assistant/tool-result messages;
- keeps assistant `ToolCalls` and matching `ToolResult` IDs aligned;
- clears the used-ID set on `Reset()`.

Example:

```text
provider emits: dup_call
provider emits: dup_call
stored as:      dup_call, dup_call_2
```

This avoids provider replay failures while preserving the first provider ID unchanged.

## Test Added

Updated `Omicron.Core.Tests/AgentSessionTests.cs`:

- `AgentSession_DeduplicatesRepeatedProviderToolCallIds`

The test verifies duplicate provider IDs become unique in both assistant tool calls and tool-result messages.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 188

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Notes

This is a defensive runtime fix. Ideally providers should emit unique tool-call IDs, but routed providers can violate that assumption. The session now guarantees replay-safe IDs for the local transcript.
