namespace Omicron.Core.Events;

/// <summary>
/// Registry of all concrete OmicronEvent types.
/// Replaces the manual switch in JsonlSessionStore.DeserializeEvent with
/// a type lookup. Ensures that new event types cannot be added without
/// being registered — a test enumerates AllTypes and verifies round-trip.
/// </summary>
public static class OmicronEventRegistry
{
    private static readonly Dictionary<string, Type> _types = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Type, string> _names = new();

    static OmicronEventRegistry()
    {
        Register<SessionStartedEvent>();
        Register<SessionEndedEvent>();
        Register<SessionResetEvent>();
        Register<SessionErrorEvent>();
        Register<TurnStartedEvent>();
        Register<UserMessageEvent>();
        Register<AssistantTextDeltaEvent>();
        Register<AssistantResponseCompleteEvent>();
        Register<ToolInvocationStartedEvent>();
        Register<ToolInvocationCompletedEvent>();
        Register<PermissionRequestedEvent>();
        Register<ExecutionStartedEvent>();
        Register<ExecutionCompletedEvent>();
        Register<ProviderStateUpdatedEvent>();
        Register<ProviderStateClearedEvent>();
        Register<TransactionStartedEvent>();
        Register<TransactionStagedEvent>();
        Register<TransactionCommittedEvent>();
        Register<TransactionRolledBackEvent>();
        Register<ModalityUsedEvent>();
    }

    private static void Register<T>() where T : OmicronEvent
    {
        var name = typeof(T).Name;
        _types[name] = typeof(T);
        _names[typeof(T)] = name;
    }

    /// <summary>Get the CLR type for a given event type name.</summary>
    public static Type? GetType(string typeName) =>
        _types.GetValueOrDefault(typeName);

    /// <summary>Get the canonical type name for a given CLR type.</summary>
    public static string GetName(Type type) => _names[type];

    /// <summary>All registered event types (for round-trip testing).</summary>
    public static IReadOnlyCollection<Type> AllTypes => _types.Values;
}
