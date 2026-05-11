using System.Buffers;

namespace Omicron.Core.Rendering;

/// <summary>Extension methods for <see cref="IBufferWriter{T}"/>.</summary>
internal static class BufferWriterExtensions
{
    /// <summary>Write a span of bytes to the buffer writer.</summary>
    public static void Write(this IBufferWriter<byte> writer, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        var span = writer.GetSpan(data.Length);
        data.CopyTo(span);
        writer.Advance(data.Length);
    }
}
