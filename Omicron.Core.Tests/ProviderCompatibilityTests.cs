using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Providers;
using Omicron.Core.Sessions;
using Xunit;

namespace Omicron.Core.Tests;

public class ProviderCompatibilityTests
{
    [Fact]
    public void ProviderCompatibility_OpenAiChat_Defaults()
    {
        var compat = ProviderCompatibility.OpenAiChat;
        Assert.True(compat.SupportsStreamingUsage);
        Assert.True(compat.SupportsReasoningEffort);
        Assert.True(compat.SupportsImages);
        Assert.True(compat.SupportsStrictTools);
        Assert.False(compat.SupportsStore);
        Assert.Equal(ToolCallIdFormat.Default, compat.ToolCallIdFormat);
    }

    [Fact]
    public void ProviderCompatibility_OpenAiResponses_Defaults()
    {
        var compat = ProviderCompatibility.OpenAiResponses;
        Assert.True(compat.SupportsStore);
        Assert.True(compat.SupportsStreamingUsage);
        Assert.True(compat.SupportsReasoningEffort);
        Assert.True(compat.SupportsImages);
        Assert.Equal(ToolCallIdFormat.Default, compat.ToolCallIdFormat);
    }

    [Fact]
    public void ProviderCompatibility_AnthropicMessages_Defaults()
    {
        var compat = ProviderCompatibility.AnthropicMessages;
        Assert.False(compat.SupportsStreamingUsage);
        Assert.True(compat.SupportsImages);
        Assert.True(compat.RequiresAssistantAfterToolResult);
        Assert.Equal(ToolCallIdFormat.Anthropic, compat.ToolCallIdFormat);
    }

    [Fact]
    public void ProviderCompatibility_GoogleGenAi_Defaults()
    {
        var compat = ProviderCompatibility.GoogleGenAi;
        Assert.False(compat.SupportsStreamingUsage);
        Assert.True(compat.SupportsImages);
    }

    [Fact]
    public void ProviderCompatibility_CanSetCustomValues()
    {
        var compat = new ProviderCompatibility
        {
            SupportsStore = false,
            SupportsStreamingUsage = false,
            RequiresToolResultName = true,
            ToolCallIdFormat = ToolCallIdFormat.ResponsesItem,
            MaxTokensField = MaxTokensField.MaxTokensToSample,
            ThinkingFormat = ThinkingFormat.ReasoningContent,
            CacheControlFormat = CacheControlFormat.AnthropicEphemeral
        };

        Assert.False(compat.SupportsStore);
        Assert.False(compat.SupportsStreamingUsage);
        Assert.True(compat.RequiresToolResultName);
        Assert.Equal(ToolCallIdFormat.ResponsesItem, compat.ToolCallIdFormat);
        Assert.Equal(MaxTokensField.MaxTokensToSample, compat.MaxTokensField);
        Assert.Equal(ThinkingFormat.ReasoningContent, compat.ThinkingFormat);
        Assert.Equal(CacheControlFormat.AnthropicEphemeral, compat.CacheControlFormat);
    }
}

public class ProviderStoragePolicyTests
{
    [Fact]
    public void ProviderStoragePolicy_Values()
    {
        Assert.Equal(0, (int)ProviderStoragePolicy.PreferStateless);
        Assert.Equal(1, (int)ProviderStoragePolicy.AllowProviderStateNoStore);
        Assert.Equal(2, (int)ProviderStoragePolicy.AllowProviderStoredState);
    }

    [Fact]
    public void ProviderTurnState_DefaultStoragePolicy()
    {
        var key = ProviderStateKey.Create(
            SessionId.New(), AgentId.New(), "test", "model", ApiType.OpenAiChat);
        var state = new ProviderTurnState(key, null, null, null, null);

        Assert.Equal(ProviderStoragePolicy.AllowProviderStateNoStore, state.StoragePolicy);
    }

    [Fact]
    public void ProviderTurnState_CustomStoragePolicy()
    {
        var key = ProviderStateKey.Create(
            SessionId.New(), AgentId.New(), "test", "model", ApiType.OpenAiChat);
        var state = new ProviderTurnState(key, null, null, null, null)
        {
            StoragePolicy = ProviderStoragePolicy.PreferStateless
        };

        Assert.Equal(ProviderStoragePolicy.PreferStateless, state.StoragePolicy);
    }

    [Fact]
    public void ProviderTurnState_IsStateful_WithPreviousResponseId()
    {
        var key = ProviderStateKey.Create(
            SessionId.New(), AgentId.New(), "test", "model", ApiType.OpenAiResponses);
        var state = new ProviderTurnState(key, "resp_123", null, null, null);

        Assert.True(state.IsStateful);
    }

    [Fact]
    public void ProviderTurnState_IsStateful_WithoutPreviousResponseId()
    {
        var key = ProviderStateKey.Create(
            SessionId.New(), AgentId.New(), "test", "model", ApiType.OpenAiResponses);
        var state = new ProviderTurnState(key, null, null, null, null);

        Assert.False(state.IsStateful);
    }

    [Fact]
    public void ProviderTurnState_ClearContinuation()
    {
        var key = ProviderStateKey.Create(
            SessionId.New(), AgentId.New(), "test", "model", ApiType.OpenAiResponses);
        var state = new ProviderTurnState(key, "resp_123", "conv_456", null, null);

        var cleared = state.ClearContinuation();

        Assert.Null(cleared.PreviousResponseId);
        Assert.Null(cleared.ConversationId);
        Assert.Null(cleared.ProviderMetadata);
        // Key should be preserved
        Assert.Equal(key, cleared.Key);
    }

    [Fact]
    public void ProviderTurnState_CanFork_ReturnsFalse()
    {
        var key = ProviderStateKey.Create(
            SessionId.New(), AgentId.New(), "test", "model", ApiType.OpenAiResponses);
        var state = new ProviderTurnState(key, "resp_123", null, null, null);

        Assert.False(state.CanFork());
    }
}

public class ModelCompatibilityTests
{
    [Fact]
    public void Model_GetEffectiveCompatibility_ReturnsOpenAiChatByDefault()
    {
        var model = new Model
        {
            Id = "gpt-4o",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiChat
        };

        var compat = model.GetEffectiveCompatibility();
        Assert.True(compat.SupportsStreamingUsage);
        Assert.True(compat.SupportsImages);
    }

    [Fact]
    public void Model_GetEffectiveCompatibility_ReturnsResponsesForResponsesType()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses
        };

        var compat = model.GetEffectiveCompatibility();
        Assert.True(compat.SupportsStore);
    }

    [Fact]
    public void Model_GetEffectiveCompatibility_ReturnsAnthropicForAnthropicType()
    {
        var model = new Model
        {
            Id = "claude-sonnet-4",
            ProviderName = "anthropic",
            ApiType = ApiType.AnthropicMessages
        };

        var compat = model.GetEffectiveCompatibility();
        Assert.True(compat.RequiresAssistantAfterToolResult);
        Assert.Equal(ToolCallIdFormat.Anthropic, compat.ToolCallIdFormat);
    }

    [Fact]
    public void Model_GetEffectiveCompatibility_UsesExplicitOverride()
    {
        var custom = new ProviderCompatibility
        {
            SupportsStore = false,
            SupportsImages = false
        };

        var model = new Model
        {
            Id = "gpt-4o",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiChat,
            Compatibility = custom
        };

        var compat = model.GetEffectiveCompatibility();
        Assert.False(compat.SupportsStore);
        Assert.False(compat.SupportsImages);
    }

    [Fact]
    public void Model_StoragePolicy_Defaults()
    {
        var model = new Model
        {
            Id = "gpt-4o",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiChat
        };

        Assert.Equal(ProviderStoragePolicy.PreferStateless, model.StoragePolicy);
    }

    [Fact]
    public void Model_CanSetCustomStoragePolicy()
    {
        var model = new Model
        {
            Id = "gpt-5.5",
            ProviderName = "openai",
            ApiType = ApiType.OpenAiResponses,
            StoragePolicy = ProviderStoragePolicy.AllowProviderStoredState
        };

        Assert.Equal(ProviderStoragePolicy.AllowProviderStoredState, model.StoragePolicy);
    }
}
