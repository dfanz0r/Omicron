using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Omicron.Core.Models;
using Omicron.Core.Providers;

namespace Omicron.Core;

/// <summary>
/// Events emitted during agent execution for UI updates.
/// </summary>
public enum AgentEventType
{
    /// <summary>Agent started processing.</summary>
    Start,
    /// <summary>A text delta from the LLM.</summary>
    TextDelta,
    /// <summary>A tool call started.</summary>
    ToolCallStart,
    /// <summary>A tool call completed with its result.</summary>
    ToolCallEnd,
    /// <summary>The final response (the assistant message).</summary>
    Response,
    /// <summary>An error occurred.</summary>
    Error
}

/// <summary>
/// A single agent lifecycle event.
/// </summary>
public class AgentEvent
{
    public AgentEventType Type { get; init; }
    public string? Text { get; init; }
    public string? ToolName { get; init; }
    public string? ToolResult { get; init; }
    public string? ErrorMessage { get; init; }
    public UsageInfo? Usage { get; init; }
}

/// <summary>
/// A stateful agent that manages a conversation with an LLM,
/// supports tool calling, and emits streaming events.
/// </summary>
public class Agent
{
    private readonly List<Message> _messages = [];
    private readonly List<Tool> _tools = [];

    /// <summary>The LLM model in use.</summary>
    public Model Model { get; set; }

    /// <summary>The system prompt.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Max tokens for each LLM call.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Temperature for generation.</summary>
    public double? Temperature { get; set; }

    /// <summary>Reasoning effort (e.g., "low", "medium", "high").</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Maximum tool-call loop iterations before giving up (default 100).</summary>
    public int MaxIterations { get; set; } = 100;

    /// <summary>API key passed to the provider for each LLM call.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Conversation transcript (read-only from outside).</summary>
    public IReadOnlyList<Message> Messages => _messages.AsReadOnly();

    /// <summary>Registered tools.</summary>
    public IReadOnlyList<Tool> Tools => _tools.AsReadOnly();

    public Agent(Model model, string? systemPrompt = null)
    {
        Model = model;
        SystemPrompt = systemPrompt;
    }

    /// <summary>
    /// Register a tool the LLM can call.
    /// </summary>
    public void AddTool(Tool tool)
    {
        var existing = _tools.FindIndex(t => t.Name == tool.Name);
        if (existing >= 0)
            _tools[existing] = tool;
        else
            _tools.Add(tool);
    }

    /// <summary>
    /// Remove all tools.
    /// </summary>
    public void ClearTools() => _tools.Clear();

    /// <summary>
    /// Add a user message to the transcript.
    /// </summary>
    public void AddUserMessage(string text, List<ImageContent>? images = null)
    {
        _messages.Add(Message.UserMessage(text, images));
    }

    /// <summary>
    /// Add a user message and stream the LLM response.
    /// Returns when the full response (including any tool call loop) completes.
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> PromptAsync(
        string text,
        List<ImageContent>? images = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        AddUserMessage(text, images);
        yield return new AgentEvent { Type = AgentEventType.Start };

        await foreach (var evt in RunLoopAsync(ct))
            yield return evt;
    }

    /// <summary>
    /// Continue the conversation from the current transcript.
    /// The last message must be a user or tool result.
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> ContinueAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new AgentEvent { Type = AgentEventType.Start };

        await foreach (var evt in RunLoopAsync(ct))
            yield return evt;
    }

    /// <summary>
    /// Core agent loop: call LLM via streaming, handle tools, repeat until stop.
    /// </summary>
    private async IAsyncEnumerable<AgentEvent> RunLoopAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var provider = Model.Provider ?? throw new InvalidOperationException("Model has no provider set.");

        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            var options = new ChatOptions
            {
                ApiKey = ApiKey,
                Temperature = Temperature,
                MaxTokens = MaxTokens,
                ReasoningEffort = ReasoningEffort,
                CancellationToken = ct
            };

            // Stream response directly — yield TextDelta events as they arrive
            var responseText = new StringBuilder();
            var reasoningText = new StringBuilder();
            var toolCalls = new List<ToolCallContent>();
            StopReason stopReason = StopReason.Stop;
            string? errorMessage = null;
            UsageInfo? usage = null;

            await foreach (var evt in provider.StreamAsync(
                Model, _messages, SystemPrompt,
                _tools.Count > 0 ? _tools : null, options))
            {
                switch (evt.Type)
                {
                    case StreamEventType.TextDelta:
                        responseText.Append(evt.Delta);
                        if (evt.ReasoningText is not null)
                            reasoningText.Append(evt.ReasoningText);
                        yield return new AgentEvent
                        {
                            Type = AgentEventType.TextDelta,
                            Text = evt.Delta
                        };
                        break;

                    case StreamEventType.ToolCallStart:
                        // Track for later execution
                        break;

                    case StreamEventType.ToolCallEnd when evt.ToolCall is not null:
                        toolCalls.Add(evt.ToolCall);
                        break;

                    case StreamEventType.Done:
                        stopReason = evt.StopReason ?? StopReason.Stop;
                        usage = evt.Usage;
                        break;

                    case StreamEventType.Error:
                        stopReason = StopReason.Error;
                        errorMessage = evt.ErrorMessage;
                        break;
                }
            }

            // Handle errors
            if (stopReason == StopReason.Error)
            {
                var errMsg = errorMessage ?? "Unknown error";
                yield return new AgentEvent
                {
                    Type = AgentEventType.Error,
                    ErrorMessage = errMsg
                };
                _messages.Add(new Message
                {
                    Role = MessageRole.Assistant,
                    Text = $"[Error: {errMsg}]",
                    Timestamp = DateTime.UtcNow
                });
                yield break;
            }

            var fullText = responseText.ToString();
            // Always preserve reasoning content if it was present (even if empty string).
            // DeepSeek requires the field to exist in subsequent requests when in thinking mode.
            var fullReasoning = reasoningText.Length > 0 ? reasoningText.ToString() : null;
            if (reasoningText.Length == 0 && _messages.Count > 0)
            {
                // Check if the previous assistant message from this model had reasoning —
                // if so, carry it forward as empty to satisfy DeepSeek's echo requirement.
                var lastAssistant = _messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
                if (lastAssistant?.Reasoning is not null)
                    fullReasoning = "";
            }

            // Handle tool calls
            if (toolCalls.Count > 0)
            {
                // Store assistant message with tool calls so providers
                // can serialize them for the next request.
                _messages.Add(new Message
                {
                    Role = MessageRole.Assistant,
                    Text = fullText,
                    Reasoning = fullReasoning,
                    ToolCalls = [..toolCalls],  // copy list
                    Timestamp = DateTime.UtcNow
                });

                foreach (var toolCall in toolCalls)
                {
                    ct.ThrowIfCancellationRequested();

                    yield return new AgentEvent
                    {
                        Type = AgentEventType.ToolCallStart,
                        ToolName = toolCall.Name,
                        Text = $"{toolCall.Name}({JsonSerialize(toolCall.Arguments)})"
                    };

                    var tool = _tools.Find(t => t.Name == toolCall.Name);
                    string resultText;
                    bool isError = false;

                    if (tool?.ExecuteAsync is null)
                    {
                        resultText = $"Error: Tool '{toolCall.Name}' not found or has no execute handler.";
                        isError = true;
                    }
                    else
                    {
                        try
                        {
                            resultText = await tool.ExecuteAsync(toolCall.Id, toolCall.Arguments);
                        }
                        catch (Exception ex)
                        {
                            resultText = $"Error executing tool '{toolCall.Name}': {ex.Message}";
                            isError = true;
                        }
                    }

                    _messages.Add(Message.ToolResultMessage(
                        toolCall.Id, toolCall.Name, resultText, isError));

                    yield return new AgentEvent
                    {
                        Type = AgentEventType.ToolCallEnd,
                        ToolName = toolCall.Name,
                        ToolResult = resultText
                    };
                }

                continue; // loop back for next LLM call with tool results
            }

            // No tool calls — final response
            _messages.Add(new Message
            {
                Role = MessageRole.Assistant,
                Text = fullText,
                Reasoning = fullReasoning,
                Timestamp = DateTime.UtcNow
            });

            yield return new AgentEvent
            {
                Type = AgentEventType.Response,
                Text = fullText,
                Usage = usage
            };

            yield break;
        }

        yield return new AgentEvent
        {
            Type = AgentEventType.Error,
            ErrorMessage = $"Agent reached maximum iteration limit ({MaxIterations})."
        };
    }

    /// <summary>
    /// Reset the conversation (clear all messages).
    /// </summary>
    public void Reset()
    {
        _messages.Clear();
    }

    private static string JsonSerialize(Dictionary<string, object?>? args)
    {
        if (args is null) return "{}";
        try
        {
            return JsonSerializer.Serialize(args);
        }
        catch
        {
            return "{}";
        }
    }
}
