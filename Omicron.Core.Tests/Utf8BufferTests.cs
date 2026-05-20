using System;
using System.Buffers;
using System.Text;
using Omicron.Core.Content;
using Omicron.Core.Text;
using Xunit;

namespace Omicron.Core.Tests;

public class Utf8BufferTests
{
    [Fact]
    public void DefaultConstructor_ProducesEmptyBuffer()
    {
        using var buf = new Utf8Buffer();
        Assert.Equal(0, buf.WrittenCount);
        Assert.True(buf.WrittenSpan.IsEmpty);
    }

    [Fact]
    public void AppendLiteral_Ascii()
    {
        using var buf = new Utf8Buffer();
        buf.Append("Hello"u8);
        Assert.Equal(5, buf.WrittenCount);
        Assert.Equal("Hello", Encoding.UTF8.GetString(buf.WrittenSpan));
    }

    [Fact]
    public void AppendLiteral_MultiByte()
    {
        using var buf = new Utf8Buffer();
        buf.Append("héllo"u8);
        Assert.Equal("héllo", buf.ToString());
    }

    [Fact]
    public void Append_Byte()
    {
        using var buf = new Utf8Buffer();
        buf.Append((byte)'A');
        Assert.Equal(1, buf.WrittenCount);
        Assert.Equal("A", buf.ToString());
    }

    [Fact]
    public void MultipleAppends_Concat()
    {
        using var buf = new Utf8Buffer();
        buf.Append("Hello"u8);
        buf.Append(" "u8);
        buf.Append("World"u8);
        Assert.Equal("Hello World", buf.ToString());
    }

    [Fact]
    public void ToArray_ReturnsCopy()
    {
        using var buf = new Utf8Buffer();
        buf.Append("Test"u8);
        var arr = buf.ToArray();
        Assert.Equal("Test", Encoding.UTF8.GetString(arr));
        // Verify it's a copy by mutating
        arr[0] = (byte)'X';
        Assert.Equal("Test", buf.ToString());
    }

    [Fact]
    public void ToUtf8String_ProducesUtf8String()
    {
        using var buf = new Utf8Buffer();
        buf.Append("héllo"u8);
        var us = buf.ToUtf8String();
        Assert.Equal("héllo", us.ToString());
    }

    [Fact]
    public void ToUtf8String_Empty_ReturnsEmpty()
    {
        using var buf = new Utf8Buffer();
        var us = buf.ToUtf8String();
        Assert.Same(Utf8String.Empty, us);
    }

    [Fact]
    public void TryCopyTo_SufficientSpace_Succeeds()
    {
        using var buf = new Utf8Buffer();
        buf.Append("Hello"u8);
        Span<byte> dest = stackalloc byte[10];
        Assert.True(buf.TryCopyTo(dest, out var written));
        Assert.Equal(5, written);
        Assert.Equal("Hello", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void TryCopyTo_InsufficientSpace_Fails()
    {
        using var buf = new Utf8Buffer();
        buf.Append("Hello"u8);
        Span<byte> dest = stackalloc byte[3];
        Assert.False(buf.TryCopyTo(dest, out var written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void CopyTo_Writer()
    {
        using var buf = new Utf8Buffer();
        buf.Append("Hello"u8);
        var writer = new ArrayBufferWriter<byte>();
        buf.CopyTo(ref writer);
        Assert.Equal("Hello", Encoding.UTF8.GetString(writer.WrittenSpan));
    }

    [Fact]
    public void Disposed_WrittenCount_Throws()
    {
        var buf = new Utf8Buffer();
        buf.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { var _ = buf.WrittenCount; });
    }

    [Fact]
    public void GrowBeyondInitialCapacity()
    {
        using var buf = new Utf8Buffer(64);
        var data = new byte[1000];
        new Random(42).NextBytes(data);
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(32 + (data[i] % 95));

        buf.Append(data);
        Assert.Equal(1000, buf.WrittenCount);
        Assert.Equal(data, buf.WrittenSpan.ToArray());
    }

    [Fact]
    public void Dispose_MultipleTimes_Safe()
    {
        var buf = new Utf8Buffer();
        buf.Dispose();
        buf.Dispose(); // should not throw
    }

    [Fact]
    public void Disposed_Append_Throws()
    {
        var buf = new Utf8Buffer();
        buf.Dispose();
        Assert.Throws<ObjectDisposedException>(() => buf.Append("x"u8));
    }

    [Fact]
    public void Disposed_WrittenSpan_Throws()
    {
        var buf = new Utf8Buffer();
        buf.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { var _ = buf.WrittenSpan; });
    }

    [Fact]
    public void ToString_ReturnsDecodedContent()
    {
        using var buf = new Utf8Buffer();
        buf.Append("héllo wörld"u8);
        Assert.Equal("héllo wörld", buf.ToString());
    }

    [Fact]
    public void ToString_Empty_ReturnsEmptyString()
    {
        using var buf = new Utf8Buffer();
        Assert.Equal(string.Empty, buf.ToString());
    }

    [Fact]
    public void NegativeCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Utf8Buffer(-1));
    }

    [Fact]
    public void SmallCapacity_AppendSucceeds()
    {
        using var buf = new Utf8Buffer(16);
        buf.Append(new byte[16]);
        Assert.Equal(16, buf.WrittenCount);
        // Growth is triggered beyond initial rental
    }
}
