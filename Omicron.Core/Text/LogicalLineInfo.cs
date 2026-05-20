namespace Omicron.Core.Text;

/// <summary>
///     Metadata about a single logical line in the store.
///     <see cref="ByteLength" /> excludes the line-ending bytes (LF, CRLF, or CR).
/// </summary>
public readonly record struct LogicalLineInfo(long ByteStart, int ByteLength, int LineIndex);
