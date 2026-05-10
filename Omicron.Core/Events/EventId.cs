namespace Omicron.Core.Events;

/// <summary>
/// A unique identifier for a single event in the event log.
/// </summary>
public readonly record struct EventId(Guid Value)
{
    public static EventId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// A unique identifier for a session.
/// </summary>
public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.NewGuid());
    public static readonly SessionId Empty = default;
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// A unique identifier for an agent instance.
/// </summary>
public readonly record struct AgentId(Guid Value)
{
    public static AgentId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// A unique identifier for a tool call, correlating the request with its result.
/// </summary>
public readonly record struct ToolCallId(string Value)
{
    public override string ToString() => Value;
}
