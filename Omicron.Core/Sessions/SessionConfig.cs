using Omicron.Core.Models;

namespace Omicron.Core.Sessions;

/// <summary>
/// Immutable configuration for an AgentSession.
/// Passed at construction and cannot be mutated afterward.
/// </summary>
public sealed record SessionConfig(
    Model Model,
    string? SystemPrompt,
    string? ApiKey,
    int? MaxTokens,
    double? Temperature,
    string? ReasoningEffort,
    int MaxIterations)
{
    /// <summary>
    /// Create a default SessionConfig from a model and optional overrides.
    /// </summary>
    public static SessionConfig Create(
        Model model,
        string? systemPrompt = null,
        string? apiKey = null,
        int? maxTokens = null,
        double? temperature = null,
        string? reasoningEffort = null,
        int maxIterations = 100)
        => new(model, systemPrompt, apiKey, maxTokens, temperature, reasoningEffort, maxIterations);
}
