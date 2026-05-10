using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Sessions;
using Xunit;

namespace Omicron.Core.Tests;

public class OmicronHostSessionCreationTests
{
    [Fact]
    public async Task CreateSessionAsync_WithSessionConfig_CreatesSessionRecord()
    {
        var host = new Omicron.Core.OmicronHost(Environment.CurrentDirectory);
        var model = new Model
        {
            Id = "test-model",
            Name = "Test Model",
            ProviderName = "test"
        };
        var config = SessionConfig.Create(model, "You are a test.", maxIterations: 50);

        var session = await host.CreateSessionAsync(config);

        Assert.NotNull(session);
        Assert.Equal(config.Model, session.Model);
        Assert.Equal(config.SystemPrompt, session.SystemPrompt);
        Assert.Equal(config.ApiKey, session.ApiKey);
        Assert.Equal(config.MaxIterations, session.MaxIterations);

        // Verify a session record was created
        var record = await host.SessionStore.GetSessionAsync(session.Id);
        Assert.NotNull(record);
        Assert.Equal(model.Id, record!.ModelId);
        Assert.Equal("You are a test.", record.SystemPrompt);
    }

    [Fact]
    public void CreateSession_SyncWrapper_DelegatesCorrectly()
    {
        var host = new Omicron.Core.OmicronHost(Environment.CurrentDirectory);
        var model = new Model
        {
            Id = "sync-test",
            Name = "Sync Test",
            ProviderName = "test"
        };
        var config = SessionConfig.Create(model, "Sync wrapper test", maxTokens: 4096);

        var session = host.CreateSession(config);

        Assert.NotNull(session);
        Assert.Equal(model, session.Model);
        Assert.Equal("Sync wrapper test", session.SystemPrompt);
        Assert.Equal(4096, session.MaxTokens);
    }

    [Fact]
    public async Task CreateSessionAsync_NullConfig_Throws()
    {
        var host = new Omicron.Core.OmicronHost(Environment.CurrentDirectory);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            host.CreateSessionAsync(null!).AsTask());
    }

    [Fact]
    public void CreateSession_LegacyOverload_StillWorks()
    {
        var host = new Omicron.Core.OmicronHost(Environment.CurrentDirectory);
        var model = new Model
        {
            Id = "legacy-test",
            Name = "Legacy Test",
            ProviderName = "test"
        };

        var session = host.CreateSession(model, "legacy", maxIterations: 10);

        Assert.NotNull(session);
        Assert.Equal(model, session.Model);
        Assert.Equal("legacy", session.SystemPrompt);
        Assert.Equal(10, session.MaxIterations);
    }
}
