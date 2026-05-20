using Omicron.Core.Models;
using Omicron.Core.Providers;
using Xunit;

namespace Omicron.Core.Tests;

public class ToolCallIdMapperTests
{
    private readonly ToolCallIdMapper _mapper = new();

    [Fact]
    public void NewLogicalId_ReturnsUniqueId()
    {
        string id1 = _mapper.NewLogicalId();
        string id2 = _mapper.NewLogicalId();

        Assert.NotNull(id1);
        Assert.NotNull(id2);
        Assert.NotEqual(id1, id2);
        Assert.StartsWith("call_", id1);
    }

    [Fact]
    public void ToWireId_PreservesValidId()
    {
        string wireId = _mapper.ToWireId("call_abc123",
            ApiType.OpenAiChat,
            ProviderCompatibility.OpenAiChat);
        Assert.Equal("call_abc123", wireId);
    }

    [Fact]
    public void ToWireId_SanitizesInvalidCharacters()
    {
        string wireId = _mapper.ToWireId("call_abc$%^&*()",
            ApiType.OpenAiChat,
            ProviderCompatibility.OpenAiChat);
        Assert.DoesNotContain("$", wireId);
        Assert.DoesNotContain("%", wireId);
        Assert.DoesNotContain("^", wireId);
        Assert.DoesNotContain("&", wireId);
        Assert.DoesNotContain("*", wireId);
    }

    [Fact]
    public void ToWireId_ReturnsNewIdForEmpty()
    {
        string wireId = _mapper.ToWireId("", ApiType.OpenAiChat, ProviderCompatibility.OpenAiChat);
        Assert.StartsWith("call_", wireId);
    }

    [Fact]
    public void ToWireId_ReturnsNewIdForNull()
    {
        string wireId = _mapper.ToWireId(null!, ApiType.OpenAiChat, ProviderCompatibility.OpenAiChat);
        Assert.StartsWith("call_", wireId);
    }

    [Fact]
    public void ToWireId_TruncatesLongIds()
    {
        string longId = "call_" + new string('x', 100);
        string wireId = _mapper.ToWireId(longId, ApiType.OpenAiChat, ProviderCompatibility.OpenAiChat);
        Assert.True(wireId.Length <= 64);
    }

    [Fact]
    public void ToWireId_AnthropicMaxLength()
    {
        string longId = "call_" + new string('x', 100);
        string wireId = _mapper.ToWireId(longId,
            ApiType.AnthropicMessages,
            ProviderCompatibility.AnthropicMessages);
        Assert.True(wireId.Length <= 64);
    }

    [Fact]
    public void ToWireId_ResponsesMaxLength()
    {
        string longId = "call_" + new string('x', 100);
        string wireId = _mapper.ToWireId(longId,
            ApiType.OpenAiResponses,
            ProviderCompatibility.OpenAiResponses);
        Assert.True(wireId.Length <= 64);
    }

    [Fact]
    public void ToLogicalId_PreservesValidId()
    {
        string logicalId = _mapper.ToLogicalId("call_abc123",
            ApiType.OpenAiChat,
            ProviderCompatibility.OpenAiChat);
        Assert.Equal("call_abc123", logicalId);
    }

    [Fact]
    public void ToLogicalId_ReturnsNewIdForNull()
    {
        string logicalId = _mapper.ToLogicalId(null!,
            ApiType.OpenAiChat,
            ProviderCompatibility.OpenAiChat);
        Assert.StartsWith("call_", logicalId);
    }
}
