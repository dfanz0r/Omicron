namespace Omicron.Core.Text;

/// <summary>
/// An append-only index of logical lines over a UTF-8 byte store.
/// New bytes are scanned only once — the index never re-scans
/// already-indexed regions.
///
/// Supports streaming input: incomplete lines (no trailing newline)
/// are not committed until <see cref="Flush"/> is called or a newline
/// is found in a subsequent <see cref="AppendScan"/> call.
/// </summary>
public sealed class LogicalLineIndex
{
    private readonly List<LogicalLineInfo> _lines = [];
    private long? _pendingLineStart;
    private bool _hasPendingLine;

    /// <summary>All finalized lines (each ended by a newline).</summary>
    public IReadOnlyList<LogicalLineInfo> Lines => _lines;

    /// <summary>Number of lines including any pending incomplete line.</summary>
    public int LineCount => _lines.Count + (_hasPendingLine ? 1 : 0);

    /// <summary>
    /// Scan <paramref name="utf8"/> for line endings (LF, CRLF, CR).
    /// Lines ending with a newline are committed immediately. Trailing
    /// content without a newline is held as a pending line.
    /// </summary>
    public void AppendScan(ReadOnlySpan<byte> utf8, long globalByteStart)
    {
        if (utf8.IsEmpty)
            return;

        int i = 0;

        // If we have a pending line from a previous call, its start
        // is already set. Otherwise, start one now.
        if (!_hasPendingLine)
        {
            _pendingLineStart = globalByteStart;
            _hasPendingLine = true;
        }

        while (i < utf8.Length)
        {
            byte b = utf8[i];

            if (b == (byte)'\n')
            {
                // LF — emit line from _pendingLineStart to just before the LF
                EmitLine(globalByteStart + i);
                i++; // consume LF
                // Start a new pending line after the LF
                if (i < utf8.Length)
                {
                    _pendingLineStart = globalByteStart + i;
                    _hasPendingLine = true;
                }
                else
                {
                    // The buffer ends right after the LF; next call will start a pending line
                    _hasPendingLine = false;
                    _pendingLineStart = null;
                }
            }
            else if (b == (byte)'\r')
            {
                if (i + 1 < utf8.Length && utf8[i + 1] == (byte)'\n')
                {
                    // CRLF
                    EmitLine(globalByteStart + i);
                    i += 2; // consume CR+LF
                }
                else
                {
                    // CR alone
                    EmitLine(globalByteStart + i);
                    i++; // consume CR
                }

                // Start a new pending line after the line ending
                if (i < utf8.Length)
                {
                    _pendingLineStart = globalByteStart + i;
                    _hasPendingLine = true;
                }
                else
                {
                    _hasPendingLine = false;
                    _pendingLineStart = null;
                }
            }
            else
            {
                i++;
            }
        }
    }

    private void EmitLine(long byteEnd)
    {
        if (!_pendingLineStart.HasValue)
        {
            // Shouldn't happen, but guard against it
            _pendingLineStart = byteEnd;
        }

        int length = (int)(byteEnd - _pendingLineStart!.Value);
        if (length < 0) length = 0;
        _lines.Add(new LogicalLineInfo(_pendingLineStart!.Value, length, _lines.Count));
        _hasPendingLine = false;
        _pendingLineStart = null;
    }

    /// <summary>
    /// Finalize and commit any pending incomplete line.
    /// </summary>
    public void Flush(long storeLength)
    {
        if (_hasPendingLine && _pendingLineStart.HasValue)
        {
            int length = (int)(storeLength - _pendingLineStart.Value);
            if (length > 0)
                _lines.Add(new LogicalLineInfo(_pendingLineStart.Value, length, _lines.Count));
            _hasPendingLine = false;
            _pendingLineStart = null;
        }
    }

    /// <summary>
    /// Find the line containing the given byte offset.
    /// Returns null if the offset is outside all lines.
    /// </summary>
    public LogicalLineInfo? FindLineContaining(long byteOffset)
    {
        if (_lines.Count == 0)
            return null;

        int lo = 0, hi = _lines.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            var line = _lines[mid];
            if (byteOffset < line.ByteStart)
                hi = mid - 1;
            else if (byteOffset >= line.ByteStart + line.ByteLength)
                lo = mid + 1;
            else
                return line;
        }

        return null;
    }
}
