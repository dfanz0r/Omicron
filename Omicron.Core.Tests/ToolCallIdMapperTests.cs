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
        var id1 = _mapper.NewLogicalId();
        var id2 = _mapper.NewLogicalId();

        Assert.NotNull(id1);
        Assert.NotNull(id2);
        Assert.NotEqual(id1, id2);
        Assert.StartsWith("call_", id1);
    }

    [Fact]
    public void ToWireId_PreservesValidId()
    {
        var wireId = _mapper.ToWireId("call_abc123", ApiType.OpenAiChat, ProviderCompatibility.OpenAiChat);
        Assert.Equal("call_abc123", wireId);
    }

    [Fact]
    public void ToWireId_SanitizesInvalidCharacters()
    {
        var wireId = _mapper.ToWireId("call_abc$%^&*()", ApiType.OpenAiChat, ProviderCompatibility.OpenAiChat);
        Assert.DoesNotContain("$", wireId);
        Assert.DoesNotContain("%", wireId);
        Assert.DoesNotContain("^", wireId);
        Assert.DoesNotContain("&", wireId);
        Assert.DoesNotContain("*", wireId);
    }

    [Fact]
    public void ToWireId_ReturnsNewIdForEmpty()
    {
        var wireId = _mapper.ToWireId("", ApiType.OpenAiChat, ProviderCompatibility.OpenAiChat);
        Assert.StartsWith("call_", wireId);
    }

    [Fact]
    public void ToWireId_ReturnsNewIdForNull()
    {
        var wireId = _mapper.ToWireId(null!, ApiType.OpenAiChat, ProviderCompatibility.OpenAiChat);
        Assert.StartsWith("call_", wireId);
    }

    [Fact]
    public void ToWireId_TruncatesLongIds()
    {
        var longId = "call_" + new string('x', 100);
        var wireId = _mapper.ToWireId(longId, ApiType.OpenAiChat, ProviderCompatibility.OpenAiChat);
        Assert.True(wireId.Length <= 64);
    }

    [Fact]
    public void ToWireId_AnthropicMaxLength()
    {
        var longId = "call_" + new string('x', 100);
        var wireId = _mapper.ToWireId(longId, ApiType.AnthropicMessages, ProviderCompatibility.AnthropicMessages);
        Assert.True(wireId.Length <= 64);
    }

    [Fact]
    public void ToWireId_ResponsesMaxLength()
    {
        var longId = "call_" + new string('x', 100);
        var wireId = _mapper.ToWireId(longId, ApiType.OpenAiResponses, ProviderCompatibility.OpenAiResponses);
        Assert.True(wireId.Length <= 64);
    }

    [Fact]
    public void ToLogicalId_PreservesValidId()
    {
        var logicalId = _mapper.ToLogicalId("call_abc123", ApiType.OpenAiChat, ProviderCompatibility.OpenAiChat);
        Assert.Equal("call_abc123", logicalId);
    }

    [Fact]
    public void ToLogicalId_ReturnsNewIdForNull()
    {
        var logicalId = _mapper.ToLogicalId(null!, ApiType.OpenAiChat, ProviderCompatibility.OpenAiChat);
        Assert.StartsWith("call_", logicalId);
    }
}
