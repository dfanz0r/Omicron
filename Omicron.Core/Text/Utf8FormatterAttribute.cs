namespace Omicron.Core.Text;

/// <summary>
/// Registers a type for a source-generated custom formatter fast path.
/// Assembly-level only:
///
/// <code>
/// [assembly: Utf8Formatter&lt;MyCustomStruct&gt;]
/// </code>
///
/// The Roslyn incremental generator emits explicit <c>typeof(T) == typeof(...)</c>
/// branches into <see cref="Utf8ValueFormatter.TryFormatGenerated"/>, avoiding boxing
/// and enabling NativeAOT-compatible custom struct formatting.
/// </summary>
/// <typeparam name="T">The type to register. Must implement <c>IUtf8SpanFormattable</c>
/// or <c>ISpanFormattable</c>.</typeparam>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class Utf8FormatterAttribute<T> : Attribute
{
}
