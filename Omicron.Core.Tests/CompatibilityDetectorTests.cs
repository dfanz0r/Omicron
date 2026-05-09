using Omicron.Core.Models;
using Omicron.Core.Providers;
using Xunit;

namespace Omicron.Core.Tests;

public class CompatibilityDetectorTests
{
    [Theory]
    [InlineData("openai", "gpt-4o", ApiType.OpenAiChat)]
    [InlineData("openai", "gpt-4o-mini", ApiType.OpenAiChat)]
    [InlineData("openai", "gpt-5.5", ApiType.OpenAiResponses)]
    [InlineData("openai", "gpt-5", ApiType.OpenAiResponses)]
    [InlineData("openai", "o1", ApiType.OpenAiResponses)]
    [InlineData("openai", "o3-mini", ApiType.OpenAiResponses)]
    [InlineData("openai", "o4-codex", ApiType.OpenAiResponses)]
    [InlineData("anthropic", "claude-sonnet-4", ApiType.AnthropicMessages)]
    [InlineData("anthropic", "claude-opus-4", ApiType.AnthropicMessages)]
    [InlineData("opencode", "claude-sonnet-4", ApiType.AnthropicMessages)]
    [InlineData("opencode", "gpt-4o", ApiType.OpenAiChat)]
    [InlineData("opencode", "gpt-5.5", ApiType.OpenAiResponses)]
    [InlineData("opencode", "gemini-2.0-flash", ApiType.GoogleGenAi)]
    [InlineData("opencode-go", "gpt-4o", ApiType.OpenAiChat)]
    [InlineData("opencode-go", "claude-sonnet-4", ApiType.OpenAiChat)]
    [InlineData("openrouter", "gpt-4o", ApiType.OpenAiChat)]
    [InlineData("openrouter", "gpt-5.5", ApiType.OpenAiResponses)]
    [InlineData("openrouter", "openai/gpt-5.4-mini", ApiType.OpenAiResponses)]
    [InlineData("openrouter", "o3-mini", ApiType.OpenAiResponses)]
    [InlineData("unknown", "some-model", ApiType.OpenAiChat)]
    public void CompatibilityDetector_ResolveApiType(string provider, string modelId, ApiType expected)
    {
        var result = CompatibilityDetector.ResolveApiType(provider, modelId);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void CompatibilityDetector_ResolveApiType_WithResponsesBaseUrl()
    {
        var result = CompatibilityDetector.ResolveApiType("openai", "custom-model",
            "https://api.openai.com/v1/responses");
        Assert.Equal(ApiType.OpenAiResponses, result);
    }

    [Fact]
    public void CompatibilityDetector_ResolveCompatibility_OpenAiChat()
    {
        var compat = CompatibilityDetector.ResolveCompatibility(ApiType.OpenAiChat, "openai");
        Assert.True(compat.SupportsStreamingUsage);
        Assert.False(compat.SupportsStore);
    }

    [Fact]
    public void CompatibilityDetector_ResolveCompatibility_OpenAiResponses()
    {
        var compat = CompatibilityDetector.ResolveCompatibility(ApiType.OpenAiResponses, "openai");
        Assert.True(compat.SupportsStore);
    }

    [Fact]
    public void CompatibilityDetector_ResolveCompatibility_OpenAiResponses_NonOpenAi()
    {
        // Non-OpenAI providers with Responses API may not support store or previous_response_id
        var compat = CompatibilityDetector.ResolveCompatibility(ApiType.OpenAiResponses, "other");
        Assert.False(compat.SupportsStore);
        Assert.False(compat.SupportsPreviousResponseId);
    }

    [Fact]
    public void CompatibilityDetector_ResolveCompatibility_OpenRouterResponses_IsStatelessByDefault()
    {
        var compat = CompatibilityDetector.ResolveCompatibility(ApiType.OpenAiResponses, "openrouter");
        Assert.False(compat.SupportsStore);
        Assert.False(compat.SupportsPreviousResponseId);
    }

    [Fact]
    public void CompatibilityDetector_ResolveCompatibility_Anthropic()
    {
        var compat = CompatibilityDetector.ResolveCompatibility(ApiType.AnthropicMessages, "anthropic");
        Assert.Equal(ToolCallIdFormat.Anthropic, compat.ToolCallIdFormat);
    }

    [Fact]
    public void CompatibilityDetector_ResolveStoragePolicy_Responses()
    {
        var policy = CompatibilityDetector.ResolveStoragePolicy(ApiType.OpenAiResponses, "openai");
        Assert.Equal(ProviderStoragePolicy.AllowProviderStateNoStore, policy);
    }

    [Fact]
    public void CompatibilityDetector_ResolveStoragePolicy_OpenRouterResponses_PreferStateless()
    {
        var policy = CompatibilityDetector.ResolveStoragePolicy(ApiType.OpenAiResponses, "openrouter");
        Assert.Equal(ProviderStoragePolicy.PreferStateless, policy);
    }

    [Fact]
    public void CompatibilityDetector_ResolveStoragePolicy_Chat()
    {
        var policy = CompatibilityDetector.ResolveStoragePolicy(ApiType.OpenAiChat, "openai");
        Assert.Equal(ProviderStoragePolicy.PreferStateless, policy);
    }

    [Theory]
    [InlineData(ApiType.OpenAiResponses, ProviderStoragePolicy.AllowProviderStateNoStore, true)]
    [InlineData(ApiType.OpenAiResponses, ProviderStoragePolicy.AllowProviderStoredState, true)]
    [InlineData(ApiType.OpenAiResponses, ProviderStoragePolicy.PreferStateless, false)]
    [InlineData(ApiType.OpenAiChat, ProviderStoragePolicy.AllowProviderStateNoStore, false)]
    [InlineData(ApiType.AnthropicMessages, ProviderStoragePolicy.AllowProviderStateNoStore, false)]
    public void CompatibilityDetector_SupportsStatefulContinuation(ApiType apiType, ProviderStoragePolicy policy, bool expected)
    {
        var result = CompatibilityDetector.SupportsStatefulContinuation(apiType, policy);
        Assert.Equal(expected, result);
    }
}
