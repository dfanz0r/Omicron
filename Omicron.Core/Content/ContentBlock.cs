using System.Text.Json;
using System.Text.Json.Serialization;
using Cysharp.Text;

namespace Omicron.Core.Content;

// ============================================================
// Mutator delegates for borrow-based UTF-8 builder access (F2)
// ============================================================

/// <summary>
/// Callback delegate for temporary mutable access to a
/// <see cref="Utf8ValueStringBuilder"/> owned by a content buffer.
/// Use the generic overload <see cref="Utf8BuilderMutator{TState}"/>
/// with a static lambda to avoid closure allocations.
/// </summary>
public delegate void Utf8BuilderMutator(ref Utf8ValueStringBuilder builder);

/// <summary>
/// Stateful callback delegate for allocation-free temporary mutable
/// access to a <see cref="Utf8ValueStringBuilder"/>.
/// </summary>
public delegate void Utf8BuilderMutator<TState>(ref Utf8ValueStringBuilder builder, TState state);

// ============================================================
// Utf8ContentBuffer — owns the pooled UTF-8 builder (F2)
// ============================================================

/// <summary>
/// Owns UTF-8 text for content blocks. The backing storage is a ZString
/// <see cref="Utf8ValueStringBuilder"/>, so renderers can append the content
/// into larger caller-owned UTF-8 builders without round-tripping through
/// intermediate strings.
///
/// Mutable access is available only through callback-based
/// <see cref="Mutate(Utf8BuilderMutator)"/> borrows — the builder
/// ref is not exposed as a long-lived property.
/// </summary>
public sealed class Utf8ContentBuffer : IDisposable, IEquatable<Utf8ContentBuffer>
{
    private Utf8ValueStringBuilder _builder;
    private bool _disposed;

    public Utf8ContentBuffer()
    {
        _builder = ZString.CreateUtf8StringBuilder();
    }

    public Utf8ContentBuffer(string value) : this()
    {
        _builder.Append(value);
    }

    /// <summary>Take ownership of an existing builder, clearing the source.</summary>
    public Utf8ContentBuffer(ref Utf8ValueStringBuilder builder)
    {
        _builder = builder;
        builder = default; // prevent double dispose
    }

    /// <summary>
    /// Direct mutable access to the underlying builder.
    /// Marked internal to prevent long-lived mutable borrows outside this assembly.
    /// Callers should use <see cref="Mutate(Utf8BuilderMutator)"/> instead.
    /// Marked <see cref="JsonIgnoreAttribute"/> so reflection-based serializers
    /// (e.g. System.Text.Json with default options) cannot accidentally traverse
    /// the ref struct.
    /// </summary>
    [JsonIgnore]
    internal ref Utf8ValueStringBuilder Builder => ref _builder;

    /// <summary>Temporarily borrow the builder for mutation via callback.</summary>
    public void Mutate(Utf8BuilderMutator mutator)
    {
        ThrowIfDisposed();
        mutator(ref _builder);
    }

    /// <summary>
    /// Temporarily borrow the builder for mutation via a stateful callback.
    /// Use a static lambda to avoid closure allocations:
    /// <code>buffer.Mutate(path, static (ref Utf8ValueStringBuilder b, string p) => b.Append(p));</code>
    /// </summary>
    public void Mutate<TState>(TState state, Utf8BuilderMutator<TState> mutator)
    {
        ThrowIfDisposed();
        mutator(ref _builder, state);
    }

    /// <summary>Get the written buffer data as a span.</summary>
    public ReadOnlySpan<byte> AsSpan()
    {
        ThrowIfDisposed();
        return _builder.AsSpan();
    }

    /// <summary>Get the written buffer data as memory.</summary>
    public ReadOnlyMemory<byte> AsMemory()
    {
        ThrowIfDisposed();
        return _builder.AsMemory();
    }

    /// <summary>Get the written buffer data as an array segment.</summary>
    public ArraySegment<byte> AsArraySegment()
    {
        ThrowIfDisposed();
        return _builder.AsArraySegment();
    }

    /// <summary>Length of written buffer in bytes.</summary>
    public int Length
    {
        get
        {
            ThrowIfDisposed();
            return _builder.Length;
        }
    }

    public void AppendTo(ref Utf8ValueStringBuilder destination)
    {
        ThrowIfDisposed();
        destination.AppendLiteral(_builder.AsSpan());
    }

    public override string ToString()
    {
        ThrowIfDisposed();
        return _builder.ToString();
    }

    public bool Equals(Utf8ContentBuffer? other)
    {
        ThrowIfDisposed();
        if (other is null) return false;
        other.ThrowIfDisposed();
        return _builder.AsSpan().SequenceEqual(other._builder.AsSpan());
    }

    public override bool Equals(object? obj) => obj is Utf8ContentBuffer other && Equals(other);

    public override int GetHashCode()
    {
        ThrowIfDisposed();
        var hash = new HashCode();
        foreach (var b in _builder.AsSpan())
            hash.Add(b);
        return hash.ToHashCode();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _builder.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Utf8ContentBuffer));
    }
}

// ============================================================
// IContentBlock — polymorphic content block interface (F1, F6)
// ============================================================

/// <summary>
/// Common interface for all content blocks. Provides uniform access to the
/// underlying UTF-8 text buffer, a stable kind identifier, and
/// <see cref="Utf8JsonWriter"/>-based serialization for persistence.
/// </summary>
public interface IContentBlock : IDisposable
{
    /// <summary>Stable UTF-8 kind discriminator (e.g. "plain_text"u8).</summary>
    ReadOnlySpan<byte> KindUtf8 { get; }

    /// <summary>The owned UTF-8 text buffer.</summary>
    Utf8ContentBuffer TextBuffer { get; }

    /// <summary>Convenience accessor for the text as a string (allocates).</summary>
    string Text { get; }

    /// <summary>Write this block's payload into a <see cref="Utf8JsonWriter"/>.
    /// The caller writes the start/end object; this method writes the properties.</summary>
    void WriteJson(Utf8JsonWriter writer);
}

/// <summary>
/// Helper methods for content block disposal.
/// </summary>
public static class ContentBlockHelper
{
    /// <summary>
    /// Dispose all content blocks in a list without clearing the list.
    /// The list reference remains valid (with disposed blocks) so that
    /// borrowers like TranscriptStore can still type-check block entries.
    /// Safe to call with null or empty lists. Idempotent.
    /// </summary>
    public static void DisposeBlocks(List<IContentBlock>? blocks)
    {
        if (blocks is not { Count: > 0 }) return;
        foreach (var block in blocks)
            block.Dispose();
    }
}

// ============================================================
// PlainTextContentBlock
// ============================================================

/// <summary>
/// Plain, unformatted text. No markdown, no special formatting.
/// </summary>
public sealed class PlainTextContentBlock : IContentBlock, IEquatable<PlainTextContentBlock>
{
    [JsonIgnore] public ReadOnlySpan<byte> KindUtf8 => "plain_text"u8;
    public Utf8ContentBuffer TextBuffer { get; }
    public string Text => TextBuffer.ToString();

    public PlainTextContentBlock() => TextBuffer = new Utf8ContentBuffer();

    public PlainTextContentBlock(string text) => TextBuffer = new Utf8ContentBuffer(text);

    /// <summary>Construct from a pre-built buffer (e.g. populated via builder API).</summary>
    public PlainTextContentBlock(Utf8ContentBuffer buffer) => TextBuffer = buffer;

    /// <summary>Take ownership of a pre-built UTF-8 builder.</summary>
    public PlainTextContentBlock(ref Utf8ValueStringBuilder builder)
    {
        TextBuffer = new Utf8ContentBuffer(ref builder);
    }

    public void Dispose() => TextBuffer.Dispose();

    public bool Equals(PlainTextContentBlock? other) => other is not null && TextBuffer.Equals(other.TextBuffer);
    public override bool Equals(object? obj) => obj is PlainTextContentBlock other && Equals(other);
    public override int GetHashCode() => TextBuffer.GetHashCode();

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("kind"u8, KindUtf8);
        writer.WritePropertyName("text"u8);
        writer.WriteStringValue(TextBuffer.AsSpan());
    }

    internal static PlainTextContentBlock FromJson(JsonElement root)
    {
        var text = root.GetProperty("text"u8).GetString() ?? "";
        return new PlainTextContentBlock(text);
    }
}

// ============================================================
// MarkdownContentBlock
// ============================================================

/// <summary>
/// Markdown-formatted text. Future TUI/GUI renderers may parse
/// the markdown; the text renderer passes it through as-is.
/// </summary>
public sealed class MarkdownContentBlock : IContentBlock, IEquatable<MarkdownContentBlock>
{
    [JsonIgnore] public ReadOnlySpan<byte> KindUtf8 => "markdown"u8;
    public Utf8ContentBuffer TextBuffer { get; }
    public string Text => TextBuffer.ToString();

    public MarkdownContentBlock(string markdown) => TextBuffer = new Utf8ContentBuffer(markdown);

    /// <summary>Construct from a pre-built buffer.</summary>
    public MarkdownContentBlock(Utf8ContentBuffer buffer) => TextBuffer = buffer;

    /// <summary>Take ownership of a pre-built UTF-8 builder.</summary>
    public MarkdownContentBlock(ref Utf8ValueStringBuilder builder)
    {
        TextBuffer = new Utf8ContentBuffer(ref builder);
    }

    public void Dispose() => TextBuffer.Dispose();

    public bool Equals(MarkdownContentBlock? other) => other is not null && TextBuffer.Equals(other.TextBuffer);
    public override bool Equals(object? obj) => obj is MarkdownContentBlock other && Equals(other);
    public override int GetHashCode() => TextBuffer.GetHashCode();

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("kind"u8, KindUtf8);
        writer.WritePropertyName("text"u8);
        writer.WriteStringValue(TextBuffer.AsSpan());
    }

    internal static MarkdownContentBlock FromJson(JsonElement root)
    {
        var text = root.GetProperty("text"u8).GetString() ?? "";
        return new MarkdownContentBlock(text);
    }
}

// ============================================================
// CodeContentBlock (F6: equality includes Language and Path)
// ============================================================

/// <summary>
/// A code snippet with optional language hint and source path.
/// </summary>
public sealed class CodeContentBlock : IContentBlock, IEquatable<CodeContentBlock>
{
    [JsonIgnore] public ReadOnlySpan<byte> KindUtf8 => "code"u8;
    public Utf8ContentBuffer TextBuffer { get; }
    public string Text => TextBuffer.ToString();
    public string? Language { get; }
    public string? Path { get; }

    public CodeContentBlock(string code, string? Language = null, string? Path = null)
    {
        TextBuffer = new Utf8ContentBuffer(code);
        this.Language = Language;
        this.Path = Path;
    }

    /// <summary>Construct from a pre-built buffer.</summary>
    public CodeContentBlock(Utf8ContentBuffer buffer, string? Language = null, string? Path = null)
    {
        TextBuffer = buffer;
        this.Language = Language;
        this.Path = Path;
    }

    /// <summary>Take ownership of a pre-built UTF-8 builder.</summary>
    public CodeContentBlock(ref Utf8ValueStringBuilder builder, string? Language = null, string? Path = null)
    {
        TextBuffer = new Utf8ContentBuffer(ref builder);
        this.Language = Language;
        this.Path = Path;
    }

    public void Dispose() => TextBuffer.Dispose();

    public bool Equals(CodeContentBlock? other)
        => other is not null
        && TextBuffer.Equals(other.TextBuffer)
        && Language == other.Language
        && Path == other.Path;

    public override bool Equals(object? obj) => obj is CodeContentBlock other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(TextBuffer, Language, Path);

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("kind"u8, KindUtf8);
        writer.WritePropertyName("text"u8);
        writer.WriteStringValue(TextBuffer.AsSpan());
        if (Language is not null) writer.WriteString("language"u8, Language);
        if (Path is not null) writer.WriteString("path"u8, Path);
    }

    internal static CodeContentBlock FromJson(JsonElement root)
    {
        var text = root.GetProperty("text"u8).GetString() ?? "";
        var language = root.TryGetProperty("language"u8, out var langEl) ? langEl.GetString() : null;
        var path = root.TryGetProperty("path"u8, out var pathEl) ? pathEl.GetString() : null;
        return new CodeContentBlock(text, language, path);
    }
}

// ============================================================
// DiffContentBlock (F6: equality includes Path)
// ============================================================

/// <summary>
/// A unified diff (patch) with optional source path.
/// </summary>
public sealed class DiffContentBlock : IContentBlock, IEquatable<DiffContentBlock>
{
    [JsonIgnore] public ReadOnlySpan<byte> KindUtf8 => "diff"u8;
    public Utf8ContentBuffer TextBuffer { get; }
    public string Text => TextBuffer.ToString();
    public string? Path { get; }

    public DiffContentBlock(string unifiedDiff, string? Path = null)
    {
        TextBuffer = new Utf8ContentBuffer(unifiedDiff);
        this.Path = Path;
    }

    /// <summary>Construct from a pre-built buffer.</summary>
    public DiffContentBlock(Utf8ContentBuffer buffer, string? Path = null)
    {
        TextBuffer = buffer;
        this.Path = Path;
    }

    /// <summary>Take ownership of a pre-built UTF-8 builder.</summary>
    public DiffContentBlock(ref Utf8ValueStringBuilder builder, string? Path = null)
    {
        TextBuffer = new Utf8ContentBuffer(ref builder);
        this.Path = Path;
    }

    public void Dispose() => TextBuffer.Dispose();

    public bool Equals(DiffContentBlock? other)
        => other is not null && TextBuffer.Equals(other.TextBuffer) && Path == other.Path;
    public override bool Equals(object? obj) => obj is DiffContentBlock other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(TextBuffer, Path);

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("kind"u8, KindUtf8);
        writer.WritePropertyName("text"u8);
        writer.WriteStringValue(TextBuffer.AsSpan());
        if (Path is not null) writer.WriteString("path"u8, Path);
    }

    internal static DiffContentBlock FromJson(JsonElement root)
    {
        var text = root.GetProperty("text"u8).GetString() ?? "";
        var path = root.TryGetProperty("path"u8, out var pathEl) ? pathEl.GetString() : null;
        return new DiffContentBlock(text, path);
    }
}

// ============================================================
// FilePreviewContentBlock (F6: equality includes all metadata)
// ============================================================

/// <summary>
/// A file preview — typically from a read_path or file read tool.
/// Carries the path, a text preview excerpt, size, and line count.
/// </summary>
public sealed class FilePreviewContentBlock : IContentBlock, IEquatable<FilePreviewContentBlock>
{
    [JsonIgnore] public ReadOnlySpan<byte> KindUtf8 => "file_preview"u8;
    public string Path { get; }
    public Utf8ContentBuffer TextBuffer { get; }
    public string Text => TextBuffer.ToString();
    public long Size { get; }
    public int? LineCount { get; }
    public bool IsBinary { get; }

    public FilePreviewContentBlock(string Path, string Preview, long Size, int? LineCount, bool IsBinary)
    {
        this.Path = Path;
        TextBuffer = new Utf8ContentBuffer(Preview);
        this.Size = Size;
        this.LineCount = LineCount;
        this.IsBinary = IsBinary;
    }

    public FilePreviewContentBlock(string Path, long Size, int? LineCount, bool IsBinary)
    {
        this.Path = Path;
        TextBuffer = new Utf8ContentBuffer();
        this.Size = Size;
        this.LineCount = LineCount;
        this.IsBinary = IsBinary;
    }

    /// <summary>Construct from a pre-built buffer.</summary>
    public FilePreviewContentBlock(string Path, Utf8ContentBuffer buffer, long Size, int? LineCount, bool IsBinary)
    {
        this.Path = Path;
        TextBuffer = buffer;
        this.Size = Size;
        this.LineCount = LineCount;
        this.IsBinary = IsBinary;
    }

    /// <summary>Take ownership of a pre-built UTF-8 builder.</summary>
    public FilePreviewContentBlock(string Path, ref Utf8ValueStringBuilder builder, long Size, int? LineCount, bool IsBinary)
    {
        this.Path = Path;
        TextBuffer = new Utf8ContentBuffer(ref builder);
        this.Size = Size;
        this.LineCount = LineCount;
        this.IsBinary = IsBinary;
    }

    public void Dispose() => TextBuffer.Dispose();

    public bool Equals(FilePreviewContentBlock? other)
        => other is not null
        && TextBuffer.Equals(other.TextBuffer)
        && Path == other.Path
        && Size == other.Size
        && LineCount == other.LineCount
        && IsBinary == other.IsBinary;

    public override bool Equals(object? obj) => obj is FilePreviewContentBlock other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(TextBuffer, Path, Size, LineCount, IsBinary);

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("kind"u8, KindUtf8);
        writer.WriteString("path"u8, Path);
        writer.WritePropertyName("text"u8);
        writer.WriteStringValue(TextBuffer.AsSpan());
        writer.WriteNumber("size"u8, Size);
        if (LineCount is not null)
            writer.WriteNumber("line_count"u8, LineCount.Value);
        writer.WriteBoolean("is_binary"u8, IsBinary);
    }

    internal static FilePreviewContentBlock FromJson(JsonElement root)
    {
        var path = root.GetProperty("path"u8).GetString() ?? "";
        var text = root.TryGetProperty("text"u8, out var textEl) ? textEl.GetString() ?? "" : "";
        var size = root.TryGetProperty("size"u8, out var sizeEl) ? sizeEl.GetInt64() : 0L;
        var lineCount = root.TryGetProperty("line_count"u8, out var lcEl) ? lcEl.GetInt32() : (int?)null;
        var isBinary = root.TryGetProperty("is_binary"u8, out var ibEl) && ibEl.GetBoolean();
        return new FilePreviewContentBlock(path, text, size, lineCount, isBinary);
    }
}

// ============================================================
// ErrorContentBlock (F6: equality includes DetailsBuffer)
// ============================================================

/// <summary>
/// An error message with optional detail.
/// </summary>
public sealed class ErrorContentBlock : IContentBlock, IEquatable<ErrorContentBlock>
{
    [JsonIgnore] public ReadOnlySpan<byte> KindUtf8 => "error"u8;
    public Utf8ContentBuffer TextBuffer { get; }
    public string Text => TextBuffer.ToString();
    public Utf8ContentBuffer? DetailsBuffer { get; }
    public string? Details => DetailsBuffer?.ToString();

    public ErrorContentBlock(string Message, string? Details = null)
    {
        TextBuffer = new Utf8ContentBuffer(Message);
        DetailsBuffer = Details is null ? null : new Utf8ContentBuffer(Details);
    }

    /// <summary>Construct from a pre-built message buffer.</summary>
    public ErrorContentBlock(Utf8ContentBuffer messageBuffer, string? Details = null)
    {
        TextBuffer = messageBuffer;
        DetailsBuffer = Details is null ? null : new Utf8ContentBuffer(Details);
    }

    /// <summary>Take ownership of a pre-built UTF-8 builder for the message.</summary>
    public ErrorContentBlock(ref Utf8ValueStringBuilder builder, string? Details = null)
    {
        TextBuffer = new Utf8ContentBuffer(ref builder);
        DetailsBuffer = Details is null ? null : new Utf8ContentBuffer(Details);
    }

    public void Dispose()
    {
        TextBuffer.Dispose();
        DetailsBuffer?.Dispose();
    }

    public bool Equals(ErrorContentBlock? other)
        => other is not null
        && TextBuffer.Equals(other.TextBuffer)
        && (DetailsBuffer is null) == (other.DetailsBuffer is null)
        && (DetailsBuffer is null || DetailsBuffer.Equals(other.DetailsBuffer));

    public override bool Equals(object? obj) => obj is ErrorContentBlock other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(TextBuffer, DetailsBuffer);

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("kind"u8, KindUtf8);
        writer.WritePropertyName("text"u8);
        writer.WriteStringValue(TextBuffer.AsSpan());
        if (DetailsBuffer is not null)
        {
            writer.WritePropertyName("details"u8);
            writer.WriteStringValue(DetailsBuffer.AsSpan());
        }
    }

    internal static ErrorContentBlock FromJson(JsonElement root)
    {
        var text = root.GetProperty("text"u8).GetString() ?? "";
        var details = root.TryGetProperty("details"u8, out var detEl) ? detEl.GetString() : null;
        return new ErrorContentBlock(text, details);
    }
}

// ============================================================
// ToolCallContentBlock (F7: now implements IContentBlock)
// ============================================================

/// <summary>
/// A tool call invocation block. Used for structured tool call display
/// both in the TUI transcript and via content block rendering.
/// </summary>
public sealed class ToolCallContentBlock : IContentBlock, IEquatable<ToolCallContentBlock>
{
    [JsonIgnore] public ReadOnlySpan<byte> KindUtf8 => "tool_call"u8;
    public string ToolCallId { get; }
    public string ToolName { get; }
    public IReadOnlyDictionary<string, object?>? Arguments { get; }

    public Utf8ContentBuffer TextBuffer { get; }
    public string Text => TextBuffer.ToString();

    public ToolCallContentBlock(string toolCallId, string toolName, IReadOnlyDictionary<string, object?>? arguments = null)
    {
        ToolCallId = toolCallId;
        ToolName = toolName;
        Arguments = arguments;

        // Synthesize a text representation for the buffer
        var sb = ZString.CreateUtf8StringBuilder();
        try
        {
            sb.AppendLiteral("[Tool Call: "u8);
            sb.Append(toolName);
            if (toolCallId is not null)
            {
                sb.AppendLiteral(" ("u8);
                sb.Append(toolCallId);
                sb.Append(')');
            }
            sb.Append(']');
            TextBuffer = new Utf8ContentBuffer(ref sb);
        }
        finally
        {
            sb.Dispose();
        }
    }

    public void Dispose() => TextBuffer.Dispose();

    public bool Equals(ToolCallContentBlock? other)
        => other is not null
        && ToolCallId == other.ToolCallId
        && ToolName == other.ToolName;

    public override bool Equals(object? obj) => obj is ToolCallContentBlock other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(ToolCallId, ToolName);

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("kind"u8, KindUtf8);
        writer.WriteString("tool_call_id"u8, ToolCallId);
        writer.WriteString("tool_name"u8, ToolName);
        if (Arguments is { Count: > 0 })
        {
            writer.WritePropertyName("arguments"u8);
            JsonSerializer.Serialize(writer, Arguments);
        }
    }

    internal static ToolCallContentBlock FromJson(JsonElement root)
    {
        var toolCallId = root.GetProperty("tool_call_id"u8).GetString() ?? "";
        var toolName = root.GetProperty("tool_name"u8).GetString() ?? "";
        IReadOnlyDictionary<string, object?>? arguments = null;
        if (root.TryGetProperty("arguments"u8, out var argsEl))
        {
            arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsEl.GetRawText());
        }
        return new ToolCallContentBlock(toolCallId, toolName, arguments);
    }
}

// ============================================================
// ContentBlockJsonConverter — polymorphic dispatch only (F1)
// ============================================================

/// <summary>
/// JSON converter for <see cref="IContentBlock"/> polymorphic serialization.
/// Handles dispatch based on the "kind" property; concrete block types
/// own their payload shape via <see cref="IContentBlock.WriteJson"/> and
/// internal <c>FromJson</c> factories.
/// </summary>
public sealed class ContentBlockJsonConverter : JsonConverter<IContentBlock>
{
    public override void Write(Utf8JsonWriter writer, IContentBlock value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        value.WriteJson(writer);
        writer.WriteEndObject();
    }

    public override IContentBlock Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("kind"u8, out var kindEl))
            throw new JsonException("Content block missing 'kind' property.");

        var kind = kindEl.GetString() ?? "";
        return kind switch
        {
            "plain_text"    => PlainTextContentBlock.FromJson(root),
            "markdown"      => MarkdownContentBlock.FromJson(root),
            "code"          => CodeContentBlock.FromJson(root),
            "diff"          => DiffContentBlock.FromJson(root),
            "file_preview"  => FilePreviewContentBlock.FromJson(root),
            "error"         => ErrorContentBlock.FromJson(root),
            "tool_call"     => ToolCallContentBlock.FromJson(root),
            _               => throw new JsonException($"Unknown content block kind: {kind}")
        };
    }
}
