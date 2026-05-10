using Omicron.Core.Events;
using Xunit;

namespace Omicron.Core.Tests;

public class EventRegistryTests
{
    [Fact]
    public void OmicronEventRegistry_ContainsAllConcreteEventTypes()
    {
        // All concrete OmicronEvent types must be registered for deserialization round-trip
        var expectedTypes = new HashSet<Type>
        {
            typeof(SessionStartedEvent),
            typeof(SessionEndedEvent),
            typeof(SessionResetEvent),
            typeof(SessionErrorEvent),
            typeof(TurnStartedEvent),
            typeof(UserMessageEvent),
            typeof(AssistantTextDeltaEvent),
            typeof(AssistantResponseCompleteEvent),
            typeof(ToolInvocationStartedEvent),
            typeof(ToolInvocationCompletedEvent),
            typeof(PermissionRequestedEvent),
            typeof(ExecutionStartedEvent),
            typeof(ExecutionCompletedEvent),
            typeof(ProviderStateUpdatedEvent),
            typeof(ProviderStateClearedEvent),
            typeof(TransactionStartedEvent),
            typeof(TransactionStagedEvent),
            typeof(TransactionCommittedEvent),
            typeof(TransactionRolledBackEvent),
        };

        var registered = OmicronEventRegistry.AllTypes;
        Assert.Subset(expectedTypes, registered.ToHashSet());
        Assert.Subset(registered.ToHashSet(), expectedTypes);
    }

    [Fact]
    public void OmicronEventRegistry_RoundTripsTypeNames()
    {
        foreach (var type in OmicronEventRegistry.AllTypes)
        {
            var name = type.Name;
            var resolved = OmicronEventRegistry.GetType(name);
            Assert.NotNull(resolved);
            Assert.Equal(type, resolved);
        }
    }

    [Fact]
    public void OmicronEventRegistry_GetType_ReturnsNullForUnknown()
    {
        Assert.Null(OmicronEventRegistry.GetType("NonExistentEventType"));
    }

    [Fact]
    public void OmicronEventRegistry_GetName_ReturnsCorrectName()
    {
        Assert.Equal(nameof(SessionStartedEvent), OmicronEventRegistry.GetName(typeof(SessionStartedEvent)));
        Assert.Equal(nameof(UserMessageEvent), OmicronEventRegistry.GetName(typeof(UserMessageEvent)));
    }
}
