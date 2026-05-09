# Code Review 0021: OpenRouter Responses Input Schema Fix

Date: 2026-05-08

## Issue Observed

OpenRouter `/responses` rejected a stateless full-context request with:

```text
Invalid Responses API request
path: ["input"]
```

The error showed that one historical assistant item was invalid. The request builder was emitting prior assistant text as:

```json
{
  "role": "assistant",
  "content": [
    { "type": "output_text", "text": "..." }
  ]
}
```

For Responses input replay, assistant output messages need to be typed output items, e.g. `type: "message"`. The builder also replayed MVP reasoning blocks inside assistant message content using an ad-hoc shape that OpenRouter rejected.

## Fix

Updated `Omicron.Core/Providers/ApiShape.cs` in `OpenAiResponsesShape.BuildInputItems(...)`:

- assistant text replay now emits:

```json
{
  "type": "message",
  "role": "assistant",
  "content": [
    { "type": "output_text", "text": "..." }
  ]
}
```

- assistant function-call replay now includes both `id` and `call_id`:

```json
{
  "type": "function_call",
  "id": "call_...",
  "call_id": "call_...",
  "name": "...",
  "arguments": "{...}"
}
```

- MVP `Message.Reasoning` is no longer replayed as an ad-hoc Responses reasoning item. Proper reasoning item replay should wait for a canonical reasoning representation/fixture.

## Tests

Updated `Omicron.Core.Tests/OpenAiResponsesShapeTests.cs`:

- assistant function calls assert `id` and `call_id` are both present;
- added `BuildRequestBody_AssistantTextUsesResponsesMessageItemAndOmitsReasoning`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 187

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Expected Behavior

OpenRouter stateless Responses replay should no longer fail because of assistant text items being sent as untyped assistant-role objects or because MVP reasoning was embedded in an invalid location.
