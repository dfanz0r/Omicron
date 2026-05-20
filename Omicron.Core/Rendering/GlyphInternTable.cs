namespace Omicron.Core.Rendering;

/// <summary>
///     Maps non-ASCII grapheme cluster UTF-8 sequences to stable integer IDs.
///     Used by <see cref="TerminalFrame" /> and <see cref="AnsiEncoder" />.
///     Hard cap of 4096 entries. If exceeded, the table is cleared and a full
///     frame reset is required (the consumer should detect this via the
///     <see cref="Overflowed" /> flag).
///     Uses a hash map with chaining to avoid string allocations on lookup.
/// </summary>
public sealed class GlyphInternTable
{
    private const int MaxEntries = 4096;
    private readonly List<byte[]> _entries = [];
    private readonly Dictionary<int, List<(byte[] Key, int Id)>> _hashMap = new();

    /// <summary>Whether the table overflowed and was reset.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>Number of entries currently in the table.</summary>
    public int Count => _entries.Count;

    /// <summary>
    ///     Intern a UTF-8 grapheme cluster. Returns its stable integer ID
    ///     for this table instance. If the table is full, clears all entries
    ///     and sets <see cref="Overflowed" /> to true.
    /// </summary>
    public int Intern(ReadOnlySpan<byte> utf8)
    {
        if (Overflowed)
        {
            return 0;
        }

        int hash = ComputeHash(utf8);
        if (_hashMap.TryGetValue(hash, out List<(byte[] Key, int Id)>? list))
        {
            foreach ((byte[] key, int existingId) in list)
            {
                if (utf8.SequenceEqual(key))
                {
                    return existingId;
                }
            }
        }

        if (_entries.Count >= MaxEntries)
        {
            // Overflow: clear all entries
            _hashMap.Clear();
            _entries.Clear();
            Overflowed = true;
            return 0;
        }

        int id = _entries.Count;
        byte[] copy = utf8.ToArray();
        _entries.Add(copy);

        if (!_hashMap.TryGetValue(hash, out list))
        {
            list = new List<(byte[], int)>();
            _hashMap[hash] = list;
        }

        list.Add((copy, id));
        return id;
    }

    /// <summary>Resolve an intern ID back to its UTF-8 bytes.</summary>
    public ReadOnlySpan<byte> Resolve(int internId)
    {
        if (internId < 0 || internId >= _entries.Count)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        return _entries[internId];
    }

    /// <summary>
    ///     Copy all entries from another table, preserving their IDs.
    ///     Used by <see cref="DifferentialRenderer" /> to keep intern IDs valid
    ///     across swap-chain buffers.
    /// </summary>
    public void CopyFrom(GlyphInternTable other)
    {
        _hashMap.Clear();
        _entries.Clear();
        Overflowed = other.Overflowed;

        foreach (byte[] entry in other._entries)
        {
            int id = _entries.Count;
            _entries.Add(entry);
            int hash = ComputeHash(entry);
            if (!_hashMap.TryGetValue(hash, out List<(byte[] Key, int Id)>? list))
            {
                list = new List<(byte[], int)>();
                _hashMap[hash] = list;
            }

            list.Add((entry, id));
        }
    }

    /// <summary>Reset the table (clear all entries).</summary>
    public void Reset()
    {
        _hashMap.Clear();
        _entries.Clear();
        Overflowed = false;
    }

    private static int ComputeHash(ReadOnlySpan<byte> span)
    {
        int hash = 17;
        for (int i = 0; i < span.Length; i++)
        {
            hash = hash * 31 + span[i];
        }

        return hash;
    }
}
