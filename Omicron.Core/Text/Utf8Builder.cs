using System;
using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Omicron.Core.Content;

namespace Omicron.Core.Text
{
    /// <summary>
    /// Omicron-owned mutable UTF-8 builder backed by pooled <see cref="byte[]"/> storage.
    /// Omicron-owned mutable UTF-8 builder backed by pooled <see cref="byte[]"/> storage.
    /// Embedded in <see cref="Utf8ContentBuffer"/> without extra heap allocation.
    /// Implements <see cref="IDisposable"/> (returns buffer to pool) and
    /// <see cref="IBufferWriter{T}"/> for direct span-level writing.
    /// </summary>
    /// <remarks>
    /// Replaces ZString's <c>Cysharp.Text.Utf8ValueStringBuilder</c>.
    /// </remarks>
    public partial struct Utf8Builder : IDisposable, IBufferWriter<byte>
    {
        private const int DefaultBufferSize = 4096;
        private const int ThreadStaticBufferSize = 65536;

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private static byte _newLine1;
        private static byte _newLine2;
        private static bool _crlf;

        [ThreadStatic]
        private static byte[]? _scratchBuffer;

        [ThreadStatic]
        private static bool _scratchBufferUsed;

        static Utf8Builder()
        {
            var nl = Utf8NoBom.GetBytes(Environment.NewLine);
            if (nl.Length == 1)
            {
                _newLine1 = nl[0];
                _crlf = false;
            }
            else
            {
                _newLine1 = nl[0];
                _newLine2 = nl[1];
                _crlf = true;
            }
        }

        private byte[]? _buffer;
        private int _index;
        private bool _disposeImmediately;
        private bool _disposed;
        private bool _isThreadStatic;

        /// <summary>Length of written content in bytes.</summary>
        public readonly int Length
        {
            get
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(Utf8Builder));
                return _index;
            }
        }

        /// <summary>Whether the builder has been disposed.</summary>
        public readonly bool IsDisposed => _disposed;

        /// <summary>
        /// Creates a builder using thread-static scratch storage for short-lived use.
        /// Must be disposed before another thread-static builder is created on the same thread.
        /// </summary>
        public Utf8Builder(bool useThreadStaticScratch)
        {
            if (useThreadStaticScratch && _scratchBufferUsed)
            {
                ThrowNestedException();
            }

            byte[]? buf;
            if (useThreadStaticScratch)
            {
                buf = _scratchBuffer;
                if (buf is null)
                {
                    buf = _scratchBuffer = new byte[ThreadStaticBufferSize];
                }
                _scratchBufferUsed = true;
            }
            else
            {
                buf = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
            }

            _buffer = buf;
            _index = 0;
            _disposeImmediately = useThreadStaticScratch;
            _isThreadStatic = useThreadStaticScratch;
            _disposed = false;
        }

        /// <summary>
        /// Returns the inner buffer to the pool. Safe to call multiple times.
        /// After disposal, all read/write methods throw <see cref="ObjectDisposedException"/>.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_buffer is not null)
            {
                if (!_isThreadStatic)
                {
                    ArrayPool<byte>.Shared.Return(_buffer);
                }
                _buffer = null;
                _index = 0;
                if (_disposeImmediately)
                {
                    _scratchBufferUsed = false;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CheckDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Utf8Builder));
        }

        /// <summary>Lazy init for default-constructed builder.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureInitialized()
        {
            if (_buffer is null)
            {
                _buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
                _index = 0;
                _disposed = false;
                _disposeImmediately = false;
            }
        }

        /// <summary>Clears written content without returning storage.</summary>
        public void Clear()
        {
            CheckDisposed();
            EnsureInitialized();
            _index = 0;
        }

        /// <summary>Ensures capacity for at least <paramref name="sizeHint"/> additional bytes.</summary>
        private void Grow(int sizeHint)
        {
            var current = _buffer!.Length;
            var needed = _index + sizeHint;
            if (needed <= current) return;

            var nextSize = Math.Max(current * 2, needed);
            var newBuffer = ArrayPool<byte>.Shared.Rent(nextSize);
            _buffer.AsSpan(0, _index).CopyTo(newBuffer);
            if (!_isThreadStatic)
                ArrayPool<byte>.Shared.Return(_buffer);
            _isThreadStatic = false;
            _buffer = newBuffer;
        }

        // ---- AppendLiteral ----

        /// <summary>Appends raw UTF-8 bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AppendLiteral(ReadOnlySpan<byte> utf8)
        {
            CheckDisposed();
            EnsureInitialized();
            if (utf8.Length == 0) return;
            if (_buffer!.Length - _index < utf8.Length)
                Grow(utf8.Length);
            utf8.CopyTo(_buffer.AsSpan(_index));
            _index += utf8.Length;
        }

        // ---- Append (char) ----

        /// <summary>Appends a single character encoded as UTF-8.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Append(char value)
        {
            CheckDisposed();
            EnsureInitialized();
            int maxLen = Utf8NoBom.GetMaxByteCount(1);
            if (_buffer!.Length - _index < maxLen)
                Grow(maxLen);
            fixed (byte* bp = &_buffer[_index])
            {
                _index += Utf8NoBom.GetBytes(&value, 1, bp, maxLen);
            }
        }

        /// <summary>Appends a character repeated <paramref name="repeatCount"/> times.</summary>
        public void Append(char value, int repeatCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(repeatCount);
            CheckDisposed();
            EnsureInitialized();

            if (value <= 0x7F) // ASCII
            {
                if (_buffer!.Length - _index < repeatCount)
                    Grow(repeatCount);
                _buffer.AsSpan(_index, repeatCount).Fill((byte)value);
                _index += repeatCount;
            }
            else
            {
                Span<byte> utf8Bytes = stackalloc byte[Utf8NoBom.GetMaxByteCount(1)];
                ReadOnlySpan<char> chars = stackalloc char[1] { value };
                int len = Utf8NoBom.GetBytes(chars, utf8Bytes);
                int total = len * repeatCount;
                if (_buffer!.Length - _index < total)
                    Grow(total);
                for (int i = 0; i < repeatCount; i++)
                {
                    utf8Bytes[..len].CopyTo(_buffer.AsSpan(_index));
                    _index += len;
                }
            }
        }

        // ---- Append (string) ----

        /// <summary>Appends a string value as UTF-8.</summary>
        public void Append(string? value)
        {
            if (value is null) return;
            Append(value.AsSpan());
        }

        /// <summary>Appends a substring.</summary>
        public void Append(string value, int startIndex, int count)
        {
            if (value is null)
            {
                if (startIndex == 0 && count == 0) return;
                throw new ArgumentNullException(nameof(value));
            }
            Append(value.AsSpan(startIndex, count));
        }

        // ---- Append (ReadOnlySpan<char>) ----

        /// <summary>Appends UTF-16 characters encoded as UTF-8.</summary>
        public void Append(ReadOnlySpan<char> value)
        {
            CheckDisposed();
            EnsureInitialized();
            if (value.Length == 0) return;
            int maxLen = Utf8NoBom.GetMaxByteCount(value.Length);
            if (_buffer!.Length - _index < maxLen)
                Grow(maxLen);
            _index += Utf8NoBom.GetBytes(value, _buffer.AsSpan(_index));
        }

        // ---- AppendLine ----

        /// <summary>Appends the platform newline sequence.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AppendLine()
        {
            CheckDisposed();
            EnsureInitialized();
            if (_crlf)
            {
                if (_buffer!.Length - _index < 2) Grow(2);
                _buffer[_index] = _newLine1;
                _buffer[_index + 1] = _newLine2;
                _index += 2;
            }
            else
            {
                if (_buffer!.Length - _index < 1) Grow(1);
                _buffer[_index] = _newLine1;
                _index += 1;
            }
        }

        /// <summary>Appends a string followed by newline.</summary>
        public void AppendLine(string? value)
        {
            Append(value);
            AppendLine();
        }

        /// <summary>Appends chars followed by newline.</summary>
        public void AppendLine(ReadOnlySpan<char> value)
        {
            Append(value);
            AppendLine();
        }

        /// <summary>Appends a character followed by newline.</summary>
        public void AppendLine(char value)
        {
            Append(value);
            AppendLine();
        }

        // ---- Append<T> (generic value formatting) ----

        /// <summary>
        /// Appends the UTF-8 representation of a value.
        /// Supports Utf8Formatter-backed types, <see cref="IUtf8SpanFormattable"/>,
        /// and falls back to <c>ToString()</c>.
        /// </summary>
        public void Append<T>(T value)
        {
            CheckDisposed();
            EnsureInitialized();
            if (value is null) return;

            // Quick attempt into a stackalloc buffer for small values
            Span<byte> scratch = stackalloc byte[256];
            if (TryFormatOne(value, scratch, out int written) && written <= scratch.Length)
            {
                if (written <= _buffer!.Length - _index)
                {
                    scratch[..written].CopyTo(_buffer.AsSpan(_index));
                    _index += written;
                    return;
                }
                Grow(written);
                scratch[..written].CopyTo(_buffer.AsSpan(_index));
                _index += written;
                return;
            }

            // Exponential-growth retry loop for large values
            int sizeHint = Math.Max(256, written);
            int safetyLimit = 100 * 1024 * 1024; // 100 MB safety limit

            for (int retry = 0; ; retry++)
            {
                Grow(sizeHint);
                if (TryFormatOne(value, _buffer.AsSpan(_index), out written))
                {
                    _index += written;
                    return;
                }
                sizeHint = Math.Max(sizeHint * 2, written);
                if (sizeHint > safetyLimit)
                    break;
            }

            // If the value implements IUtf8SpanFormattable and we exhausted retries, throw.
            // Only fall back to ToString() for types without IUtf8SpanFormattable support.
            if (value is IUtf8SpanFormattable)
            {
                throw new InvalidOperationException(
                    $"Cannot format value of type {typeof(T).Name}: required buffer exceeds safety limit.");
            }

            // Final fallback: ToString() → UTF-8
            var s = value?.ToString();
            if (s is not null)
            {
                int maxLen = Utf8NoBom.GetMaxByteCount(s.Length);
                if (_buffer!.Length - _index < maxLen)
                    Grow(maxLen);
                _index += Utf8NoBom.GetBytes(s.AsSpan(), _buffer.AsSpan(_index));
            }
        }

        /// <summary>Appends a value followed by newline.</summary>
        public void AppendLine<T>(T value)
        {
            Append(value);
            AppendLine();
        }

        // ---- IBufferWriter<byte> ----

        /// <summary>IBufferWriter.GetSpan.</summary>
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            CheckDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            EnsureInitialized();
            if (sizeHint > _buffer!.Length - _index)
                Grow(sizeHint);
            return _buffer.AsSpan(_index);
        }

        /// <summary>IBufferWriter.GetMemory.</summary>
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            CheckDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            EnsureInitialized();
            if (sizeHint > _buffer!.Length - _index)
                Grow(sizeHint);
            return _buffer.AsMemory(_index);
        }

        /// <summary>IBufferWriter.Advance.</summary>
        public void Advance(int count)
        {
            CheckDisposed();
            EnsureInitialized();
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer!.Length - _index);
            _index += count;
        }

        // ---- Output ----

        /// <summary>Returns the written content as a span.</summary>
        public readonly ReadOnlySpan<byte> AsSpan()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Utf8Builder));
            if (_buffer is null) return default;
            return _buffer.AsSpan(0, _index);
        }

        /// <summary>Returns the written content as memory.</summary>
        public readonly ReadOnlyMemory<byte> AsMemory()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Utf8Builder));
            if (_buffer is null) return default;
            return _buffer.AsMemory(0, _index);
        }

        /// <summary>Returns the written content as an array segment.</summary>
        public readonly ArraySegment<byte> AsArraySegment()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Utf8Builder));
            if (_buffer is null) return new ArraySegment<byte>(Array.Empty<byte>());
            return new ArraySegment<byte>(_buffer, 0, _index);
        }

        /// <summary>Copies written content to an <see cref="IBufferWriter{T}"/>.</summary>
        public void CopyTo(IBufferWriter<byte> writer)
        {
            var span = writer.GetSpan(_index);
            AsSpan().CopyTo(span);
            writer.Advance(_index);
        }

        /// <summary>Tries to copy written content to a span.</summary>
        public bool TryCopyTo(Span<byte> destination, out int bytesWritten)
        {
            var span = AsSpan();
            if (destination.Length < span.Length)
            {
                bytesWritten = 0;
                return false;
            }
            span.CopyTo(destination);
            bytesWritten = span.Length;
            return true;
        }

        /// <summary>Decodes written content to a <see cref="string"/>.</summary>
        public override string ToString()
        {
            var span = AsSpan();
            if (span.Length == 0) return string.Empty;
            return Utf8NoBom.GetString(span);
        }

        // ---- TryFormatOne (inline value formatting) ----

        /// <summary>
        /// Attempts to format a value into the destination span.
        /// Returns <c>true</c> on success with <paramref name="written"/> set to bytes written.
        /// On failure (<c>false</c>), <paramref name="written"/> is set to a capacity estimate.
        /// Known <see cref="IUtf8SpanFormattable"/> types are retried with exponential growth.
        /// </summary>
        private static bool TryFormatOne<T>(T value, Span<byte> dest, out int written)
        {
            // Fast path for common types via Unsafe.As + Utf8Formatter
            if (typeof(T) == typeof(int))
                return Utf8Formatter.TryFormat(Unsafe.As<T, int>(ref value), dest, out written);
            if (typeof(T) == typeof(long))
                return Utf8Formatter.TryFormat(Unsafe.As<T, long>(ref value), dest, out written);
            if (typeof(T) == typeof(uint))
                return Utf8Formatter.TryFormat(Unsafe.As<T, uint>(ref value), dest, out written);
            if (typeof(T) == typeof(ulong))
                return Utf8Formatter.TryFormat(Unsafe.As<T, ulong>(ref value), dest, out written);
            if (typeof(T) == typeof(short))
                return Utf8Formatter.TryFormat(Unsafe.As<T, short>(ref value), dest, out written);
            if (typeof(T) == typeof(ushort))
                return Utf8Formatter.TryFormat(Unsafe.As<T, ushort>(ref value), dest, out written);
            if (typeof(T) == typeof(byte))
                return Utf8Formatter.TryFormat(Unsafe.As<T, byte>(ref value), dest, out written);
            if (typeof(T) == typeof(sbyte))
                return Utf8Formatter.TryFormat(Unsafe.As<T, sbyte>(ref value), dest, out written);
            if (typeof(T) == typeof(float))
                return Utf8Formatter.TryFormat(Unsafe.As<T, float>(ref value), dest, out written);
            if (typeof(T) == typeof(double))
                return Utf8Formatter.TryFormat(Unsafe.As<T, double>(ref value), dest, out written);
            if (typeof(T) == typeof(decimal))
                return Utf8Formatter.TryFormat(Unsafe.As<T, decimal>(ref value), dest, out written);
            if (typeof(T) == typeof(Guid))
                return Utf8Formatter.TryFormat(Unsafe.As<T, Guid>(ref value), dest, out written);
            if (typeof(T) == typeof(DateTime))
                return Utf8Formatter.TryFormat(Unsafe.As<T, DateTime>(ref value), dest, out written);
            if (typeof(T) == typeof(DateTimeOffset))
                return Utf8Formatter.TryFormat(Unsafe.As<T, DateTimeOffset>(ref value), dest, out written);
            if (typeof(T) == typeof(TimeSpan))
                return Utf8Formatter.TryFormat(Unsafe.As<T, TimeSpan>(ref value), dest, out written);
            if (typeof(T) == typeof(bool))
            {
                var text = Unsafe.As<T, bool>(ref value) ? "True"u8 : "False"u8;
                return Utf8TryCopy(text, dest, out written);
            }
            if (typeof(T) == typeof(Utf8String))
            {
                var us = Unsafe.As<T, Utf8String>(ref value);
                if (us is null) { written = 0; return true; }
                return Utf8TryCopy(us.Utf8Span, dest, out written);
            }
            if (typeof(T) == typeof(string))
            {
                var s = Unsafe.As<T, string>(ref value);
                if (s is null) { written = 0; return true; }
                int maxLen = Utf8NoBom.GetMaxByteCount(s.Length);
                if (maxLen > dest.Length)
                {
                    written = maxLen;
                    return false;
                }
                written = Utf8NoBom.GetBytes(s.AsSpan(), dest);
                return true;
            }
            if (typeof(T) == typeof(char))
            {
                ushort raw = Unsafe.As<T, ushort>(ref value);
                char c = (char)raw;
                int maxLen = Utf8NoBom.GetMaxByteCount(1);
                if (maxLen > dest.Length)
                {
                    written = maxLen;
                    return false;
                }
                written = Utf8NoBom.GetBytes(stackalloc char[1] { c }, dest);
                return true;
            }

            // IUtf8SpanFormattable runtime check (may box value types)
            if (value is IUtf8SpanFormattable f)
            {
                // Try the provided buffer first; if it fails, grow via caller
                if (f.TryFormat(dest, out written, default, null))
                    return true;
                // Estimate: allocate dest.Length * 2 on the caller side
                written = dest.Length * 2;
                return false;
            }

            // ISpanFormattable fallback (via string)
            if (value is ISpanFormattable sf)
            {
                Span<char> charBuf = stackalloc char[256];
                if (sf.TryFormat(charBuf, out var charsWritten, default, null))
                {
                    int maxLen = Utf8NoBom.GetMaxByteCount(charsWritten);
                    if (maxLen > dest.Length)
                    {
                        written = maxLen;
                        return false;
                    }
                    written = Utf8NoBom.GetBytes(charBuf[..charsWritten], dest);
                    return true;
                }
            }

            // No supported formatter; caller will fall back to ToString()
            written = 0;
            return false;
        }

        private static bool Utf8TryCopy(ReadOnlySpan<byte> source, Span<byte> dest, out int written)
        {
            if (source.Length <= dest.Length)
            {
                source.CopyTo(dest);
                written = source.Length;
                return true;
            }
            written = source.Length;
            return false;
        }

        private static void ThrowNestedException()
        {
            throw new InvalidOperationException(
                "A thread-static Utf8Builder is already in use on this thread. " +
                "Dispose it before creating another.");
        }
    }
}
