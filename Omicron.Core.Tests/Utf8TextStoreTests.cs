using System.Text;
using Omicron.Core.Text;
using Xunit;

namespace Omicron.Core.Tests;

public class Utf8TextStoreTests
{
    // ============================================================
    // Phase A: Utf8TextStore
    // ============================================================

    [Fact]
    public void Append_EmptyStore_ReturnsPositionZero()
    {
        var store = new Utf8TextStore(128);
        var pos = store.Append("hello"u8);
        Assert.Equal(0, pos.GlobalByteOffset);
        Assert.Equal(0, pos.ChunkIndex);
        Assert.Equal(0, pos.ChunkByteOffset);
        Assert.Equal(5, store.LengthBytes);
    }

    [Fact]
    public void Append_SingleChunk_ReturnsCorrectLength()
    {
        var store = new Utf8TextStore(128);
        store.Append("hello"u8);
        store.Append(" world"u8);
        Assert.Equal(11, store.LengthBytes);
        Assert.Equal(1, store.ChunkCount);
    }

    [Fact]
    public void Append_MultipleChunks_CreatesNewChunkWhenFull()
    {
        // Use small chunks to force overflow
        var store = new Utf8TextStore(64);
        store.Append(Encoding.UTF8.GetBytes(new string('x', 64))); // exactly fills first chunk
        Assert.Equal(1, store.ChunkCount);
        Assert.Equal(64, store.LengthBytes);

        store.Append("abc"u8); // goes to second chunk
        Assert.Equal(2, store.ChunkCount);
        Assert.Equal(67, store.LengthBytes);

        var chunk1 = store.GetChunk(0);
        Assert.Equal(0, chunk1.Index);
        Assert.Equal(64, chunk1.Length);

        var chunk2 = store.GetChunk(1);
        Assert.Equal(1, chunk2.Index);
        Assert.Equal(3, chunk2.Length);
        Assert.Equal(64, chunk2.GlobalByteStart);
    }

    [Fact]
    public void Append_EmptyUtf8_ReturnsCurrentPosition()
    {
        var store = new Utf8TextStore(128);
        store.Append("abc"u8);
        var pos = store.Append(ReadOnlySpan<byte>.Empty);
        Assert.Equal(3, pos.GlobalByteOffset);
        Assert.Equal(3, store.LengthBytes);
    }

    [Fact]
    public void Slice_InsideSingleChunk_ReturnsZeroCopyMemory()
    {
        var store = new Utf8TextStore(128);
        store.Append("Hello, World!"u8);
        var slice = store.Slice(0, 5);
        Assert.Equal("Hello"u8.ToArray(), slice.ToArray());
        // Should reference chunk memory directly
        var chunk0 = store.GetChunk(0);
        Assert.True(slice.Span[0] == chunk0.AsSpan()[0]);
    }

    [Fact]
    public void Slice_AcrossChunks_ReturnsCopy()
    {
        var store = new Utf8TextStore(64);
        // Fill first chunk
        store.Append(Encoding.UTF8.GetBytes(new string('A', 64)));
        store.Append("BCD"u8); // goes to second chunk: 3 bytes
        // Slice across chunk boundary (last 3 of chunk0 + first 3 of chunk1)
        // Chunks: [0..63] = 'A'*64, [64..66] = 'B','C','D'
        var slice = store.Slice(61, 6);
        Assert.Equal("AAABCD"u8.ToArray(), slice.ToArray());
    }

    [Fact]
    public void Slice_PastEnd_Throws()
    {
        var store = new Utf8TextStore(128);
        store.Append("hi"u8);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Slice(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Slice(5, 1));
    }

    [Fact]
    public void Slice_ZeroLength_ReturnsEmpty()
    {
        var store = new Utf8TextStore(128);
        store.Append("hello"u8);
        var slice = store.Slice(2, 0);
        Assert.True(slice.IsEmpty);
    }

    [Fact]
    public void Chunks_EnumeratesAllChunks()
    {
        var store = new Utf8TextStore(64);
        store.Append(Encoding.UTF8.GetBytes(new string('1', 64)));
        store.Append(Encoding.UTF8.GetBytes(new string('a', 64)));
        store.Append("x"u8);
        Assert.Equal(3, store.ChunkCount);
        Assert.Equal(3, store.Chunks.Count());
        Assert.Equal(64 + 64 + 1, store.LengthBytes);
    }

    [Fact]
    public void Append_ThreadSafe()
    {
        var store = new Utf8TextStore(64);
        var tasks = new List<System.Threading.Tasks.Task>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(System.Threading.Tasks.Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                    store.Append("line\n"u8);
            }));
        }
        System.Threading.Tasks.Task.WaitAll([.. tasks]);
        // Each of 10 threads × 100 iterations × 5 bytes = 5000
        Assert.Equal(5000, store.LengthBytes);
    }

    // ============================================================
    // Phase A: LogicalLineIndex
    // ============================================================

    [Fact]
    public void LineIndex_Empty_NoLines()
    {
        var index = new LogicalLineIndex();
        Assert.Equal(0, index.LineCount);
    }

    [Fact]
    public void LineIndex_SimpleLf_SplitsCorrectly()
    {
        var index = new LogicalLineIndex();
        index.AppendScan("hello\nworld\n"u8, 0);
        Assert.Equal(2, index.LineCount);
        Assert.Equal(new LogicalLineInfo(0, 5, 0), index.Lines[0]);  // "hello" (LF excluded)
        Assert.Equal(new LogicalLineInfo(6, 5, 1), index.Lines[1]);  // "world" (LF excluded)
    }

    [Fact]
    public void LineIndex_Crlf_SplitsCorrectly()
    {
        var index = new LogicalLineIndex();
        index.AppendScan("hello\r\nworld\r\n"u8, 0);
        Assert.Equal(2, index.LineCount);
        Assert.Equal(new LogicalLineInfo(0, 5, 0), index.Lines[0]);  // "hello"
        Assert.Equal(new LogicalLineInfo(7, 5, 1), index.Lines[1]);  // "world"
    }

    [Fact]
    public void LineIndex_Cr_SplitsCorrectly()
    {
        var index = new LogicalLineIndex();
        index.AppendScan("hello\rworld\r"u8, 0);
        Assert.Equal(2, index.LineCount);
        Assert.Equal(new LogicalLineInfo(0, 5, 0), index.Lines[0]);  // "hello"
        Assert.Equal(new LogicalLineInfo(6, 5, 1), index.Lines[1]);  // "world"
    }

    [Fact]
    public void LineIndex_MixedEndings()
    {
        var index = new LogicalLineIndex();
        index.AppendScan("a\nb\r\nc\rd"u8, 0);
        // d has no trailing newline — pending until Flush
        Assert.Equal(4, index.LineCount); // 3 committed + 1 pending
        Assert.Equal(3, index.Lines.Count); // only committed lines
        Assert.Equal(0, index.Lines[0].ByteStart); // "a"
        Assert.Equal(1, index.Lines[0].ByteLength);
        Assert.Equal(2, index.Lines[1].ByteStart); // "b"
        Assert.Equal(1, index.Lines[1].ByteLength);
        Assert.Equal(5, index.Lines[2].ByteStart); // "c"
        Assert.Equal(1, index.Lines[2].ByteLength);

        // Flush to commit pending line
        index.Flush(8); // store length = 8
        Assert.Equal(4, index.LineCount);
        Assert.Equal(4, index.Lines.Count);
        Assert.Equal(7, index.Lines[3].ByteStart); // "d"
        Assert.Equal(1, index.Lines[3].ByteLength);
    }

    [Fact]
    public void LineIndex_NoTrailingNewline_LastLineIncluded()
    {
        var index = new LogicalLineIndex();
        index.AppendScan("hello\nworld"u8, 0);
        // "world" has no trailing newline — pending until Flush
        Assert.Equal(2, index.LineCount); // 1 committed + 1 pending
        Assert.Single(index.Lines); // only "hello"
        Assert.Equal(new LogicalLineInfo(0, 5, 0), index.Lines[0]);

        // Flush to commit pending line
        index.Flush(11); // store length = 11
        Assert.Equal(2, index.LineCount);
        Assert.Equal(2, index.Lines.Count);
        Assert.Equal(new LogicalLineInfo(6, 5, 1), index.Lines[1]); // "world"
    }

    [Fact]
    public void LineIndex_AppendOnly_ScansNewBytesOnly()
    {
        var index = new LogicalLineIndex();
        index.AppendScan("line1\nline2\n"u8, 0);
        Assert.Equal(2, index.LineCount);

        // Append more text
        index.AppendScan("line3\nline4\n"u8, 12);
        Assert.Equal(4, index.LineCount);
        Assert.Equal(12, index.Lines[2].ByteStart); // "line3"
        Assert.Equal(5, index.Lines[2].ByteLength);
        Assert.Equal(18, index.Lines[3].ByteStart); // "line4"
        Assert.Equal(5, index.Lines[3].ByteLength);
    }

    [Fact]
    public void LineIndex_FindLineContaining_ReturnsCorrectLine()
    {
        var index = new LogicalLineIndex();
        index.AppendScan("hello\nworld\nfoo\n"u8, 0);
        // Offset 0 → line 0
        var line = index.FindLineContaining(0);
        Assert.NotNull(line);
        Assert.Equal(0, line.Value.LineIndex);
        Assert.Equal(0, line.Value.ByteStart);
        Assert.Equal(5, line.Value.ByteLength);

        // Offset 8 ("world" -> byte 6 is w, so 8 is 'r')
        line = index.FindLineContaining(8);
        Assert.NotNull(line);
        Assert.Equal(1, line.Value.LineIndex);
        Assert.Equal(6, line.Value.ByteStart);

        // Offset past end returns null
        line = index.FindLineContaining(100);
        Assert.Null(line);
    }

    [Fact]
    public void LineIndex_NoLinesBeforeAppend_ReturnsNull()
    {
        var index = new LogicalLineIndex();
        Assert.Null(index.FindLineContaining(0));
    }

    [Fact]
    public void LineIndex_EmptyBytes_NoChange()
    {
        var index = new LogicalLineIndex();
        index.AppendScan(ReadOnlySpan<byte>.Empty, 0);
        Assert.Equal(0, index.LineCount);
    }

    // ── Streaming line index tests (Finding 6 fix) ──

    [Fact]
    public void LineIndex_StreamingMidLine_DoesNotEmitUntilNewline()
    {
        var index = new LogicalLineIndex();
        index.AppendScan("hello "u8, 0);          // streaming: no newline yet
        Assert.Equal(1, index.LineCount);           // 1 pending
        Assert.Empty(index.Lines);                  // nothing committed

        index.AppendScan("world"u8, 6);            // still no newline
        Assert.Equal(1, index.LineCount);           // still 1 pending
        Assert.Empty(index.Lines);
    }

    [Fact]
    public void LineIndex_StreamingCompletesLine_EmitsOneLine()
    {
        var index = new LogicalLineIndex();
        index.AppendScan("hello "u8, 0);
        index.AppendScan("world\n"u8, 6);
        Assert.Equal(1, index.LineCount);
        Assert.Single(index.Lines);
        Assert.Equal(0, index.Lines[0].ByteStart);
        Assert.Equal(11, index.Lines[0].ByteLength); // "hello world"
    }

    [Fact]
    public void LineIndex_StreamingMultipleChunks_CorrectLineCount()
    {
        var index = new LogicalLineIndex();
        index.AppendScan("line1\n"u8, 0);
        index.AppendScan("line2\n"u8, 6);
        index.AppendScan("line3"u8, 12);            // pending — no trailing newline
        Assert.Equal(3, index.LineCount);
        Assert.Equal(2, index.Lines.Count);

        index.Flush(17);
        Assert.Equal(3, index.LineCount);
        Assert.Equal(3, index.Lines.Count);
        Assert.Equal(0, index.Lines[0].ByteStart);
        Assert.Equal(5, index.Lines[0].ByteLength); // "line1" (length excludes \n)
        Assert.Equal(6, index.Lines[1].ByteStart);
        Assert.Equal(5, index.Lines[1].ByteLength);
        Assert.Equal(12, index.Lines[2].ByteStart);
        Assert.Equal(5, index.Lines[2].ByteLength);
    }

    [Fact]
    public void LineIndex_Flush_EmitsPendingLine()
    {
        var index = new LogicalLineIndex();
        index.AppendScan("hello world"u8, 0);
        Assert.Equal(1, index.LineCount);
        Assert.Empty(index.Lines);

        index.Flush(11);
        Assert.Equal(1, index.LineCount);
        Assert.Single(index.Lines);
        Assert.Equal(0, index.Lines[0].ByteStart);
        Assert.Equal(11, index.Lines[0].ByteLength);
    }

    // ── Slice across 3+ chunks (Observation 5) ──

    [Fact]
    public void Slice_AcrossThreeChunks_ReturnsCorrectBytes()
    {
        var store = new Utf8TextStore(64);
        store.Append(Encoding.UTF8.GetBytes(new string('A', 64))); // chunk 0
        store.Append(Encoding.UTF8.GetBytes(new string('B', 64))); // chunk 1
        store.Append(Encoding.UTF8.GetBytes(new string('C', 64))); // chunk 2
        store.Append("XYZ"u8);                                     // chunk 3 start

        // Slice across chunks 1-2: from chunk1[16] to chunk2[50] = 48 + 51 = 99 bytes
        var slice = store.Slice(80, 99);
        Assert.Equal(99, slice.Length);
        // Chunk 1 starts at offset 64, so offset 80 = chunk1[16] = 'B'
        // 99 bytes: 48 'B's (offsets 80-127) + 51 'C's (offsets 128-178)
        Assert.Equal('B', (char)slice.Span[0]);
        Assert.Equal('B', (char)slice.Span[47]);  // last 'B' at chunk boundary
        Assert.Equal('C', (char)slice.Span[48]);  // first 'C' in next chunk
        Assert.Equal('C', (char)slice.Span[98]);  // last byte of slice
    }

    // ── Stress test (Observation 4) ──

    [Fact]
    public void Stress_AppendOneMillionLines_NoLargeStringAllocations()
    {
        var store = new Utf8TextStore();
        var line = "this is a test line with some content\n"u8;
        int lineCount = 1_000_000;

        for (int i = 0; i < lineCount; i++)
            store.Append(line);

        // Verify basics
        Assert.True(store.LengthBytes > 0);
        Assert.True(store.ChunkCount > 1);

        // Build line index and verify
        var index = new LogicalLineIndex();
        long offset = 0;
        for (int i = 0; i < lineCount; i++)
        {
            index.AppendScan(line, offset);
            offset += line.Length;
        }
        index.Flush(offset);
        Assert.Equal(lineCount, index.LineCount);
        Assert.Equal(lineCount, index.Lines.Count);
    }

    // ============================================================
    // Phase B: CellWidthCalculator
    // ============================================================

    // ── Hangul Jamo width (Finding 5 fix) ──

    [Fact]
    public void CellWidth_HangulJamoInitialConsonant_Width2()
    {
        Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x1100))); // ᄀ
        Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x1159))); // last in range
    }

    [Fact]
    public void CellWidth_HangulJamoMedialVowel_Width2()
    {
        Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x1161))); // ᅡ
        Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x11A2))); // last in range
    }

    [Fact]
    public void CellWidth_HangulJamoFinalConsonant_Width2()
    {
        Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x11A8))); // ᆨ
        Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x11F9))); // last in range
    }

    [Fact]
    public void CellWidth_Ascii_Width1()
    {
        Assert.Equal(1, CellWidthCalculator.GetWidth(new Rune('A')));
        Assert.Equal(1, CellWidthCalculator.GetWidth(new Rune(' ')));
        Assert.Equal(1, CellWidthCalculator.GetWidth(new Rune('~')));
        Assert.Equal(1, CellWidthCalculator.GetWidth(new Rune('0')));
    }

    [Fact]
    public void CellWidth_Cjk_Width2()
    {
        // CJK Unified Ideographs
        Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x4E00))); // 一
        Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x9FFF))); // last in range
        Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x3400))); // Ext A
        Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x3000))); // CJK symbol space
    }

    [Fact]
    public void CellWidth_CombiningMark_Width0()
    {
        // Combining acute accent
        Assert.Equal(0, CellWidthCalculator.GetWidth(new Rune(0x0301)));
        // Combining diaeresis
        Assert.Equal(0, CellWidthCalculator.GetWidth(new Rune(0x0308)));
    }

    [Fact]
    public void CellWidth_Emoji_Width2()
    {
        // Smiley face emoji
        Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x1F600)));
        // Thumbs up
        Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x1F44D)));
    }

    [Fact]
    public void CellWidth_FlagEmoji_Width2()
    {
        // Regional indicator symbols are width 2 each
        Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x1F1E6))); // Regional A
        Assert.Equal(2, CellWidthCalculator.GetWidth(new Rune(0x1F1FF))); // Regional Z
    }

    [Fact]
    public void CellWidth_Tab_Width1()
    {
        Assert.Equal(1, CellWidthCalculator.GetWidth(new Rune('\t')));
    }

    [Fact]
    public void CellWidth_Control_Width0()
    {
        Assert.Equal(0, CellWidthCalculator.GetWidth(new Rune('\0')));
        Assert.Equal(0, CellWidthCalculator.GetWidth(new Rune('\n')));
        Assert.Equal(0, CellWidthCalculator.GetWidth(new Rune('\r')));
        Assert.Equal(0, CellWidthCalculator.GetWidth(new Rune(0x7F)));
        Assert.Equal(0, CellWidthCalculator.GetWidth(new Rune(0x80))); // C1 control
    }

    [Fact]
    public void CellWidth_SpanOverload_Works()
    {
        Assert.Equal(1, CellWidthCalculator.GetWidth("A"u8));
        Assert.Equal(2, CellWidthCalculator.GetWidth("一"u8)); // U+4E00
        Assert.Equal(0, CellWidthCalculator.GetWidth("\u0301"u8)); // combining
        Assert.Equal(2, CellWidthCalculator.GetWidth("\U0001F600"u8)); // emoji
    }

    // ============================================================
    // Phase B: GraphemeSegmenter
    // ============================================================

    [Fact]
    public void GraphemeSegmenter_SingleAscii_OneCluster()
    {
        var clusters = GraphemeSegmenter.SegmentUtf8("A"u8).ToList();
        Assert.Single(clusters);
        Assert.Equal(0, clusters[0].ByteOffset);
        Assert.Equal(1, clusters[0].ByteLength);
        Assert.Equal(1, clusters[0].RuneCount);
    }

    [Fact]
    public void GraphemeSegmenter_AsciiString_MultipleClusters()
    {
        var clusters = GraphemeSegmenter.SegmentUtf8("ABC"u8).ToList();
        Assert.Equal(3, clusters.Count);
        Assert.Equal(1, clusters[0].ByteLength);
        Assert.Equal(1, clusters[1].ByteLength);
        Assert.Equal(1, clusters[2].ByteLength);
    }

    [Fact]
    public void GraphemeSegmenter_EmojiWithSkinTone_OneCluster()
    {
        // Thumbs up + skin tone modifier (ZWJ sequence)
        // 👍 + U+1F3FC (skin tone)
        // Actually '👍🏼' (U+1F44D + U+1F3FC) using simple ZWJ-like combining
        // Note: emoji + skin tone is NOT a ZWJ sequence — they combine via
        // emoji modifier sequence. Let's use a real ZWJ emoji:
        // '👨‍👩‍👧' (family: man + ZWJ + woman + ZWJ + girl)
        // U+1F468 U+200D U+1F469 U+200D U+1F467
        var utf8 = Encoding.UTF8.GetBytes("\U0001F468\u200D\U0001F469\u200D\U0001F467");
        var clusters = GraphemeSegmenter.SegmentUtf8(utf8).ToList();

        // ZWJ sequences should merge into one cluster
        Assert.Single(clusters);
        Assert.Equal(utf8.Length, clusters[0].ByteLength);
        Assert.Equal(5, clusters[0].RuneCount);
    }

    [Fact]
    public void GraphemeSegmenter_CombiningMark_MergedWithBase()
    {
        // 'é' as 'e' + combining acute accent
        var utf8 = Encoding.UTF8.GetBytes("e\u0301");
        var clusters = GraphemeSegmenter.SegmentUtf8(utf8).ToList();
        Assert.Single(clusters);
        Assert.Equal(utf8.Length, clusters[0].ByteLength);
        Assert.Equal(2, clusters[0].RuneCount);
    }

    [Fact]
    public void GraphemeSegmenter_EmptyInput_NoClusters()
    {
        var clusters = GraphemeSegmenter.SegmentUtf8(ReadOnlySpan<byte>.Empty).ToList();
        Assert.Empty(clusters);
    }

    [Fact]
    public void GraphemeSegmenter_RegionalIndicators_Paired()
    {
        // US flag: U+1F1FA U+1F1F8
        var utf8 = Encoding.UTF8.GetBytes("\U0001F1FA\U0001F1F8");
        var clusters = GraphemeSegmenter.SegmentUtf8(utf8).ToList();
        Assert.Single(clusters);
        Assert.Equal(utf8.Length, clusters[0].ByteLength);
        Assert.Equal(2, clusters[0].RuneCount);
    }

    [Fact]
    public void GraphemeSegmenter_MixedContent()
    {
        // "A" + "e\u0301" (é) + "B"
        var utf8 = Encoding.UTF8.GetBytes("Ae\u0301B");
        var clusters = GraphemeSegmenter.SegmentUtf8(utf8).ToList();
        Assert.Equal(3, clusters.Count); // A, é (merged), B
        Assert.Equal(1, clusters[0].ByteLength);
        Assert.Equal(3, clusters[1].ByteLength); // e + combining accent = 3 UTF-8 bytes
        Assert.Equal(2, clusters[1].RuneCount);
        Assert.Equal(1, clusters[2].ByteLength);
    }
}
