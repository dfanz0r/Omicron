using System.Globalization;
using System.Text;
using System.Text.Json;
using Omicron.Core.Content;
using Omicron.Core.Events;
using Omicron.Core.IO;
using Omicron.Core.Text;
using Omicron.Core.Tools;
using Omicron.Core.Workspace;

namespace Omicron.Core.Extensions;

/// <summary>
///     Built-in extension that registers workspace tools including:
///     - read_path: read files and list directories
///     - read_file_hashlines: read a file with per-line hash anchors
///     - edit_file_hashline: edit a file with hash-anchor validation through transactions
/// </summary>
public sealed class BuiltinWorkspaceToolsExtension : IOmicronExtension
{
    private readonly Base64Processor _base64Processor;
    private readonly IEventSink? _eventSink;
    private readonly ContentProcessorRegistry _processors;
    private readonly IWorkspaceTransactionManager _txManager;
    private readonly IWorkspaceFileSystem _vfs;
    private readonly IWorkspace _workspace;

    public BuiltinWorkspaceToolsExtension(
        IWorkspace workspace,
        IWorkspaceFileSystem vfs,
        IWorkspaceTransactionManager txManager,
        ContentProcessorRegistry? processors = null,
        IEventSink? eventSink = null)
    {
        _workspace = workspace;
        _vfs = vfs;
        _txManager = txManager;
        _processors = processors ?? new ContentProcessorRegistry();
        _base64Processor = new Base64Processor();
        _eventSink = eventSink;
    }

    public string Id => "omicron.workspace-tools";
    public string DisplayName => "Workspace Tools";
    public Version Version => new(1, 0, 0);

    public void Register(IExtensionContext context)
    {
        context.RegisterTool(new ToolDefinition("read_path",
            "Read a file or list a directory. "
            + "For files: returns content (text, hex dump, or base64) based on "
            + "file type and model capabilities. "
            + "Supports format override: 'auto' (default), 'text', 'hex', or 'base64'. "
            + "For directories: lists entries with per-file line count and size. "
            + "Output is truncated at 50 KB / 2000 lines per call.",
            ToolSchema.Object(new Dictionary<string, JsonElement>
                {
                    ["path"] = ToolSchema.StringProperty(
                        "Path to read. If a file: reads content. If a directory: lists contents."),
                    ["format"] = ToolSchema.EnumProperty(
                        "Output format: 'auto' (default, auto-detect), 'text' (force UTF-8), "
                        + "'hex' (hex dump), or 'base64' (raw base64 encoding).",
                        new[]
                        {
                            "auto", "text", "hex", "base64"
                        }),
                    ["offset"] = ToolSchema.IntegerProperty(
                        "Byte offset (hex/base64 mode) or 1-based line number (text mode). "
                        + "For hex, accepts decimal or 0x-prefixed hex values."),
                    ["limit"] = ToolSchema.IntegerProperty("Byte limit (hex/base64) or max lines (text). "
                                                           + "For hex, accepts decimal or 0x-prefixed hex.")
                },
                new[]
                {
                    "path"
                }),
            async ctx =>
            {
                IReadOnlyDictionary<string, object?> args = ctx.Arguments;
                string path = args.TryGetValue("path", out object? p) ? p?.ToString() ?? "" : "";
                string format = args.TryGetValue("format", out object? f)
                    ? f?.ToString() ?? "auto"
                    : "auto";
                int? offset = TryGetInt(args, "offset");
                int? limit = TryGetInt(args, "limit");

                try
                {
                    // --- Resolve path ---
                    WorkspacePath? wsPath = _vfs.Resolve(path);
                    if (wsPath is null)
                    {
                        return new ToolResult($"Error: path escapes workspace root: '{path}'",
                            IsError: true);
                    }

                    FileStat? stat = await _vfs.StatAsync(wsPath.Value, ctx.CancellationToken);
                    if (stat is null)
                    {
                        return new ToolResult($"Error: path not found: {path}", IsError: true);
                    }

                    // --- Directory listing via workspace renderer ---
                    if (stat.IsDirectory)
                    {
                        WorkspaceReadResult readResult = await _workspace.ReadPathAsync(path,
                            null,
                            ctx.CancellationToken);
                        return new ToolResult(readResult.Content,
                            IsError: false,
                            Blocks: readResult.Blocks?.Value);
                    }

                    // --- Read file bytes ---
                    ReadOnlyMemory<byte> bytes = await _vfs.ReadFileAsync(wsPath.Value, ctx.CancellationToken);
                    if (bytes.IsEmpty && stat.Size > 0)
                    {
                        return new ToolResult($"Error: could not read file: {path}",
                            IsError: true);
                    }

                    // --- Handle format override ---
                    IContentProcessor processor;
                    string effectiveFormat = format.ToLowerInvariant();

                    // File too large for memory — downgrade to hex
                    if (stat.Size > 50 * 1024 * 1024 && effectiveFormat == "auto")
                    {
                        effectiveFormat = "hex";
                    }

                    // Cap base64 encoding to 5 MB
                    if (effectiveFormat == "base64" && stat.Size > 5 * 1024 * 1024)
                    {
                        effectiveFormat = "hex";
                    }

                    if (effectiveFormat == "hex")
                    {
                        processor = new HexDumpProcessor();
                    }
                    else if (effectiveFormat == "base64")
                    {
                        ContentProcessorContext b64Ctx = BuildContext(ctx,
                            path,
                            wsPath.Value,
                            bytes,
                            stat.Size,
                            "base64",
                            offset,
                            limit);
                        ContentProcessorResult b64Result = await _base64Processor.ProcessAsync(b64Ctx,
                            ctx.CancellationToken);

                        // Emit ModalityUsedEvent for base64 outputs that require model capabilities
                        if (
                            b64Result.ActualModality
                            is OutputModality.ImageBase64
                            or OutputModality.PdfBase64
                            or OutputModality.AudioBase64
                            or OutputModality.VideoBase64
                        )
                        {
                            string b64Modality = b64Result.ActualModality switch
                            {
                                OutputModality.ImageBase64 => "image",
                                OutputModality.PdfBase64 => "pdf",
                                OutputModality.AudioBase64 => "audio",
                                OutputModality.VideoBase64 => "video",
                                _ => "unknown"
                            };

                            if (_eventSink is not null)
                            {
                                await _eventSink.EmitAsync(new ModalityUsedEvent(
                                    EventEnvelope.ForSession(ctx.SessionId),
                                    b64Modality,
                                    path,
                                    b64Result.ActualModality));
                            }
                        }

                        return new ToolResult(b64Result.Utf8Data.HasValue ? null : b64Result.Text,
                            b64Result.Utf8Data);
                    }
                    else if (effectiveFormat == "text")
                    {
                        processor = new TextProcessor();
                    }
                    else
                    {
                        // "auto": classify and resolve
                        DetectedFileType fileType = FileTypeClassifier.Classify(path, bytes.Span);

                        if (fileType == DetectedFileType.Text)
                        {
                            // Run IsText() safety check for files classified as Text
                            if (!TextEncodingDetector.IsText(bytes.Span))
                            {
                                fileType = DetectedFileType.UnknownBinary;
                            }
                        }

                        processor = _processors.Resolve(fileType) ?? new HexDumpProcessor();
                    }

                    ContentProcessorContext context = BuildContext(ctx,
                        path,
                        wsPath.Value,
                        bytes,
                        stat.Size,
                        effectiveFormat,
                        offset,
                        limit);
                    ContentProcessorResult result = await processor.ProcessAsync(context, ctx.CancellationToken);

                    // --- Emit ModalityUsedEvent if needed ---
                    if (
                        result.ActualModality
                        is OutputModality.ImageBase64
                        or OutputModality.PdfBase64
                        or OutputModality.AudioBase64
                        or OutputModality.VideoBase64
                    )
                    {
                        string modality = result.ActualModality switch
                        {
                            OutputModality.ImageBase64 => "image",
                            OutputModality.PdfBase64 => "pdf",
                            OutputModality.AudioBase64 => "audio",
                            OutputModality.VideoBase64 => "video",
                            _ => "unknown"
                        };

                        if (_eventSink is not null)
                        {
                            await _eventSink.EmitAsync(new ModalityUsedEvent(EventEnvelope.ForSession(ctx.SessionId),
                                modality,
                                path,
                                result.ActualModality));
                        }
                    }

                    return new ToolResult(result.Utf8Data.HasValue ? null : result.Text,
                        result.Utf8Data);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return new ToolResult($"Error reading '{path}': {ex.Message}",
                        IsError: true);
                }
            }));

        // ============================================================
        // read_file_hashlines — read a file with per-line hash anchors
        // ============================================================

        context.RegisterTool(new ToolDefinition("read_file_hashlines",
            "Read a text file with per-line hash anchors. "
            + "Each line shows a 2-letter anchor (a-z, no digits) derived from line content. "
            + "Use the anchor as start_hash when calling edit_file_hashline. "
            + "Format: {line}{anchor}|{content}. "
            + "Example: 42sr|    return a + b;\n"
            + "Rejects binary files. "
            + "Output is truncated at 2000 lines / 50 KB.",
            ToolSchema.Object(new Dictionary<string, JsonElement>
                {
                    ["path"] = ToolSchema.StringProperty("Path to the text file to read (relative to workspace root).")
                },
                new[]
                {
                    "path"
                }),
            async ctx =>
            {
                IReadOnlyDictionary<string, object?> args = ctx.Arguments;
                string path = args.TryGetValue("path", out object? p) ? p?.ToString() ?? "" : "";

                try
                {
                    WorkspacePath? wsPath = _vfs.Resolve(path);
                    if (wsPath is null)
                    {
                        return new ToolResult($"Error: path escapes workspace root: '{path}'",
                            IsError: true);
                    }

                    FileStat? stat = await _vfs.StatAsync(wsPath.Value, ctx.CancellationToken);
                    if (stat is null)
                    {
                        return new ToolResult($"Error: path not found: {path}", IsError: true);
                    }

                    ReadOnlyMemory<byte> bytes = await _vfs.ReadFileAsync(wsPath.Value, ctx.CancellationToken);
                    if (bytes.IsEmpty && stat.Size > 0)
                    {
                        return new ToolResult($"Error: could not read file: {path}",
                            IsError: true);
                    }

                    if (!TextEncodingDetector.IsText(bytes.Span))
                    {
                        return new ToolResult($"Error: binary file: {path}", IsError: true);
                    }

                    // Scan line boundaries without full-file string allocation
                    ReadOnlySpan<byte> span = bytes.Span;
                    var lineStarts = new List<int>
                    {
                        0
                    };
                    for (int i = 0; i < span.Length; i++)
                    {
                        // A trailing LF terminates the final content line; don't
                        // render an extra empty hashline anchor after it.
                        if (span[i] == (byte)'\n' && i + 1 < span.Length)
                        {
                            lineStarts.Add(i + 1);
                        }
                    }

                    int totalLines = lineStarts.Count;

                    Utf8Builder output = Utf8Text.CreateBuilder();
                    try
                    {
                        Utf8CompositeFormat.AppendFormatUtf8Slow(ref output,
                            "[FILE] {0}  ({1} lines, hashline anchors)"u8,
                            path,
                            totalLines);
                        output.AppendLine();
                        output.AppendLine();

                        const int maxOutputLines = 2000;
                        const int maxOutputBytes = 50 * 1024;
                        int writtenLines = 0;
                        int writtenBytes = output.Length;
                        bool truncated = false;

                        for (int i = 0; i < totalLines; i++)
                        {
                            int lineByteStart = lineStarts[i];
                            int lineByteEnd =
                                i + 1 < lineStarts.Count ? lineStarts[i + 1] : span.Length;

                            int lineLength = lineByteEnd - lineByteStart;
                            if (
                                lineLength > 0
                                && span[lineByteStart + lineLength - 1] == (byte)'\n'
                            )
                            {
                                lineLength--;
                            }

                            if (
                                lineLength > 0
                                && span[lineByteStart + lineLength - 1] == (byte)'\r'
                            )
                            {
                                lineLength--;
                            }

                            ReadOnlySpan<byte> lineUtf8 = span.Slice(lineByteStart, lineLength);
                            string anchor = LineHash.ComputeAnchorUtf8(lineUtf8);
                            // Estimate byte cost: line number digits + anchor(2) + '|' + line + newline
                            int lineNumDigits =
                                i < 9 ? 1
                                : i < 99 ? 2
                                : i < 999 ? 3
                                : i < 9999 ? 4
                                : 5;
                            int lineBytes = lineNumDigits + 2 + 1 + lineLength + 1;

                            if (
                                writtenLines >= maxOutputLines
                                || writtenBytes + lineBytes > maxOutputBytes
                            )
                            {
                                truncated = true;
                                break;
                            }

                            LineHash.FormatLineUtf8(ref output, i + 1, anchor, lineUtf8);
                            writtenLines++;
                            writtenBytes += lineBytes;
                        }

                        if (truncated)
                        {
                            Utf8CompositeFormat.AppendFormatUtf8Slow(ref output,
                                "... output truncated ({0} total lines, showing {1})"u8,
                                totalLines,
                                writtenLines);
                            output.AppendLine();
                        }

                        return new ToolResult(Utf8Data: output.AsSpan().ToArray());
                    }
                    finally
                    {
                        output.Dispose();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new ToolResult($"Error reading '{path}': {ex.Message}",
                        IsError: true);
                }
            }));

        // ============================================================
        // edit_file_hashline — edit a file with hash-anchor validation
        // ============================================================

        context.RegisterTool(new ToolDefinition("edit_file_hashline",
            "Edit a text file with hash-anchor validation through a workspace transaction. "
            + "Each edit targets a line by its start_line and start_hash (2-letter anchor "
            + "from read_file_hashlines output). old_text is validated as a second guard. "
            + "Edits must be non-overlapping. On success, returns a unified diff. "
            + "On validation failure, includes nearby current context and hashes. "
            + "Auto-commits after validation.",
            ToolSchema.Object(new Dictionary<string, JsonElement>
                {
                    ["path"] = ToolSchema.StringProperty("Path to the file to edit (relative to workspace root)."),
                    ["edits"] = ToolSchema.ArrayProperty(
                        "Array of edit operations. Each edit: { start_line (int), start_hash (string), old_text (string), new_text (string) }",
                        ToolSchema.Object(new Dictionary<string, JsonElement>
                            {
                                ["start_line"] = ToolSchema.IntegerProperty(
                                    "1-based line number where the edit begins (hint; rebased ±5 if hash matches nearby)."),
                                ["start_hash"] = ToolSchema.StringProperty(
                                    "2-letter anchor (a-z) from read_file_hashlines output for the target line."),
                                ["old_text"] = ToolSchema.StringProperty(
                                    "The exact existing text starting at this line (may span multiple lines)."),
                                ["new_text"] = ToolSchema.StringProperty("The replacement text.")
                            },
                            new[]
                            {
                                "start_line", "start_hash", "old_text", "new_text"
                            }))
                },
                new[]
                {
                    "path", "edits"
                }),
            async ctx =>
            {
                IReadOnlyDictionary<string, object?> args = ctx.Arguments;
                string path = args.TryGetValue("path", out object? p) ? p?.ToString() ?? "" : "";
                object? editsRaw = args!.GetValueOrDefault("edits");

                try
                {
                    // ---------- resolve path ----------

                    WorkspacePath? wsPath = _vfs.Resolve(path);
                    if (wsPath is null)
                    {
                        return new ToolResult($"Error: path escapes workspace root: '{path}'",
                            IsError: true);
                    }

                    FileStat? stat = await _vfs.StatAsync(wsPath.Value, ctx.CancellationToken);
                    if (stat is null)
                    {
                        return new ToolResult($"Error: path not found: {path}", IsError: true);
                    }

                    // ---------- read current file ----------

                    ReadOnlyMemory<byte> bytes = await _vfs.ReadFileAsync(wsPath.Value, ctx.CancellationToken);
                    if (bytes.IsEmpty && stat.Size > 0)
                    {
                        return new ToolResult($"Error: could not read file: {path}",
                            IsError: true);
                    }

                    // Content-based binary detection (BOM / null-byte / UTF-8 decode / printable ratio).
                    if (!TextEncodingDetector.IsText(bytes.Span))
                    {
                        return new ToolResult($"Error: binary file cannot be edited: {path}",
                            IsError: true);
                    }

                    // Scan line boundaries without full-file string allocation
                    ReadOnlySpan<byte> editSpan = bytes.Span;
                    var editLineStarts = new List<int>
                    {
                        0
                    };
                    for (int i = 0; i < editSpan.Length; i++)
                    {
                        if (editSpan[i] == (byte)'\n')
                        {
                            editLineStarts.Add(i + 1);
                        }
                    }

                    string[] currentLines = new string[editLineStarts.Count];
                    for (int i = 0; i < editLineStarts.Count; i++)
                    {
                        int lineByteStart = editLineStarts[i];
                        int lineByteEnd =
                            i + 1 < editLineStarts.Count
                                ? editLineStarts[i + 1]
                                : editSpan.Length;

                        int lineLength = lineByteEnd - lineByteStart;
                        if (
                            lineLength > 0
                            && editSpan[lineByteStart + lineLength - 1] == (byte)'\n'
                        )
                        {
                            lineLength--;
                        }

                        if (
                            lineLength > 0
                            && editSpan[lineByteStart + lineLength - 1] == (byte)'\r'
                        )
                        {
                            lineLength--;
                        }

                        currentLines[i] = Encoding.UTF8.GetString(editSpan.Slice(lineByteStart, lineLength));
                    }

                    // ---------- parse edits ----------

                    var parsedEdits = new List<ParsedEdit>();

                    if (
                        editsRaw is JsonElement editsJe
                        && editsJe.ValueKind == JsonValueKind.Array
                    )
                    {
                        foreach (JsonElement item in editsJe.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object)
                            {
                                int sl = item.TryGetProperty("start_line", out JsonElement slProp)
                                    ? slProp.GetInt32()
                                    : 0;
                                string sh = item.TryGetProperty("start_hash", out JsonElement shProp)
                                    ? shProp.GetString() ?? ""
                                    : "";
                                string ot = item.TryGetProperty("old_text", out JsonElement otProp)
                                    ? otProp.GetString() ?? ""
                                    : "";
                                string nt = item.TryGetProperty("new_text", out JsonElement ntProp)
                                    ? ntProp.GetString() ?? ""
                                    : "";
                                parsedEdits.Add(new ParsedEdit(sl, sh, ot, nt));
                            }
                        }
                    }

                    if (parsedEdits.Count == 0)
                    {
                        return new ToolResult(
                            "Error: no valid edits provided. Supply an 'edits' array with objects containing start_line, start_hash, old_text, new_text.",
                            IsError: true);
                    }

                    // Sort by start_line ascending
                    parsedEdits.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));

                    // ---------- resolve hashes with rebase ----------

                    var resolvedEdits =
                        new List<(ParsedEdit Edit, int ActualLine, int LineCount)>();
                    var errors = new List<string>();

                    foreach (ParsedEdit edit in parsedEdits)
                    {
                        string[] oldLines = edit.OldText.Replace("\r\n", "\n").Split('\n');
                        int lineCount = oldLines.Length;

                        // Try exact line first, then rebase ±5
                        int foundLine = -1;
                        int searchStart = Math.Max(1, edit.StartLine - 5);
                        int searchEnd = Math.Min(currentLines.Length, edit.StartLine + 5);

                        for (int line = searchStart; line <= searchEnd; line++)
                        {
                            string lineHash = LineHash.ComputeAnchor(currentLines[line - 1]);
                            if (lineHash == edit.StartHash)
                            {
                                foundLine = line;
                                break;
                            }
                        }

                        if (foundLine == -1)
                        {
                            // Build nearby context for error
                            Utf8Builder context = Utf8Text.CreateBuilder();
                            try
                            {
                                Utf8CompositeFormat.AppendFormatUtf8Slow(ref context,
                                    "Hash mismatch at line {0} (expected hash '{1}'):"u8,
                                    edit.StartLine,
                                    edit.StartHash);
                                context.AppendLine();
                                int ctxStart = Math.Max(1, edit.StartLine - 3);
                                int ctxEnd = Math.Min(currentLines.Length, edit.StartLine + 3);
                                for (int l = ctxStart; l <= ctxEnd; l++)
                                {
                                    string h = LineHash.ComputeAnchor(currentLines[l - 1]);
                                    Utf8CompositeFormat.AppendFormatUtf8Slow(ref context,
                                        "  {0}{1}|"u8,
                                        l,
                                        h);
                                    context.Append(currentLines[l - 1]);
                                    context.AppendLine();
                                }

                                errors.Add(context.ToString().TrimEnd());
                            }
                            finally
                            {
                                context.Dispose();
                            }

                            continue;
                        }

                        // Validate old_text matches at found line
                        string actualSpan = string.Join("\n",
                            currentLines.Skip(foundLine - 1).Take(lineCount));
                        if (actualSpan != edit.OldText)
                        {
                            Utf8Builder context = Utf8Text.CreateBuilder();
                            try
                            {
                                Utf8CompositeFormat.AppendFormatUtf8Slow(ref context,
                                    "old_text mismatch at resolved line {0} (hash '{1}'):"u8,
                                    foundLine,
                                    edit.StartHash);
                                context.AppendLine();
                                Utf8CompositeFormat.AppendFormatUtf8Slow(ref context,
                                    "  Expected: {0}"u8,
                                    edit.OldText);
                                context.AppendLine();
                                Utf8CompositeFormat.AppendFormatUtf8Slow(ref context,
                                    "  Actual:   {0}"u8,
                                    actualSpan);
                                context.AppendLine();
                                int ctxStart = Math.Max(1, foundLine - 2);
                                int ctxEnd = Math.Min(currentLines.Length,
                                    foundLine + lineCount + 1);
                                for (int l = ctxStart; l <= ctxEnd; l++)
                                {
                                    string h = LineHash.ComputeAnchor(currentLines[l - 1]);
                                    string marker =
                                        l >= foundLine && l < foundLine + lineCount
                                            ? "~~"
                                            : "  ";
                                    Utf8CompositeFormat.AppendFormatUtf8Slow(ref context,
                                        "{0}{1}{2}|"u8,
                                        marker,
                                        l,
                                        h);
                                    context.Append(currentLines[l - 1]);
                                    context.AppendLine();
                                }

                                errors.Add(context.ToString().TrimEnd());
                            }
                            finally
                            {
                                context.Dispose();
                            }

                            continue;
                        }

                        resolvedEdits.Add((edit, foundLine, lineCount));
                    }

                    if (errors.Count > 0)
                    {
                        return new ToolResult("Validation errors:\n\n" + string.Join("\n\n", errors),
                            IsError: true);
                    }

                    // ---------- check for overlapping edits ----------

                    for (int i = 1; i < resolvedEdits.Count; i++)
                    {
                        (ParsedEdit Edit, int ActualLine, int LineCount) prev = resolvedEdits[i - 1];
                        (ParsedEdit Edit, int ActualLine, int LineCount) curr = resolvedEdits[i];
                        int prevEnd = prev.ActualLine + prev.LineCount - 1;
                        if (curr.ActualLine <= prevEnd)
                        {
                            return new ToolResult($"Error: overlapping edits. "
                                                  + $"Edit at line {prev.Edit.StartLine} (resolved {prev.ActualLine}, "
                                                  + $"{prev.LineCount} line(s)) overlaps with edit at line {curr.Edit.StartLine} "
                                                  + $"(resolved {curr.ActualLine}).",
                                IsError: true);
                        }
                    }

                    // ---------- apply edits (bottom-up to preserve line numbers) ----------

                    var resultLines = new List<string>(currentLines);
                    for (int i = resolvedEdits.Count - 1; i >= 0; i--)
                    {
                        (ParsedEdit edit, int actualLine, int lineCount) = resolvedEdits[i];
                        string[] newLines = edit.NewText.Replace("\r\n", "\n").Split('\n');

                        // Remove old lines, insert new lines
                        resultLines.RemoveRange(actualLine - 1, lineCount);
                        resultLines.InsertRange(actualLine - 1, newLines);
                    }

                    string newText = string.Join("\n", resultLines);

                    // ---------- create transaction, stage, diff, commit ----------

                    await using IWorkspaceTransaction tx = _txManager.BeginTransaction();

                    byte[] newBytes = Encoding.UTF8.GetBytes(newText);
                    await tx.Files.WriteFileAsync(wsPath.Value,
                        newBytes.AsMemory(),
                        ctx.CancellationToken);

                    WorkspaceDiff diff = await tx.GetDiffAsync(ctx.CancellationToken);

                    await tx.CommitAsync(ctx.CancellationToken);

                    // ---------- format result ----------

                    Utf8Builder resultOutput = Utf8Text.CreateBuilder();
                    try
                    {
                        Utf8CompositeFormat.AppendFormatUtf8Slow(ref resultOutput,
                            "Edit committed: {0}"u8,
                            path);
                        resultOutput.AppendLine();

                        if (diff.HasChanges)
                        {
                            resultOutput.AppendLine();
                            WorkspaceFileDiff fileDiff = diff.FileDiffs[0];
                            if (fileDiff.IsBinary)
                            {
                                resultOutput.AppendLiteral("(binary file diff omitted)"u8);
                            }
                            else if (!string.IsNullOrEmpty(fileDiff.TextDiff))
                            {
                                resultOutput.Append(fileDiff.TextDiff);
                            }
                            else
                            {
                                resultOutput.AppendLiteral("(no textual changes)"u8);
                            }

                            resultOutput.AppendLine();
                        }

                        // Remind the model to call read_file_hashlines for fresh anchors
                        resultOutput.AppendLine();
                        resultOutput.AppendLiteral(
                            "Call read_file_hashlines to get fresh line anchors if you need to make further edits."u8);
                        resultOutput.AppendLine();

                        return new ToolResult(Utf8Data: resultOutput.AsSpan().ToArray());
                    }
                    finally
                    {
                        resultOutput.Dispose();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return new ToolResult($"Error editing '{path}': {ex.Message}",
                        IsError: true);
                }
            }));

        // ============================================================
        // write_file — create or overwrite a file
        // ============================================================

        context.RegisterTool(new ToolDefinition("write_file",
            "Create a new file or overwrite an existing file with the given content. "
            + "Parent directories are created automatically. "
            + "Use this to create new files; use edit_file_hashline to modify existing files safely. "
            + "Paths are relative to the workspace root.",
            ToolSchema.Object(new Dictionary<string, JsonElement>
                {
                    ["path"] = ToolSchema.StringProperty(
                        "Path to the file to create or overwrite (relative to workspace root)."),
                    ["content"] = ToolSchema.StringProperty("The text content to write to the file.")
                },
                new[]
                {
                    "path", "content"
                }),
            async ctx =>
            {
                IReadOnlyDictionary<string, object?> args = ctx.Arguments;
                string path = args.TryGetValue("path", out object? p) ? p?.ToString() ?? "" : "";
                string content = args.TryGetValue("content", out object? c) ? c?.ToString() ?? "" : "";

                try
                {
                    WorkspacePath? wsPath = _vfs.Resolve(path);
                    if (wsPath is null)
                    {
                        return new ToolResult($"Error: path escapes workspace root: '{path}'",
                            IsError: true);
                    }

                    byte[] bytes = Encoding.UTF8.GetBytes(content);

                    // VFS creates parent directories automatically
                    await _vfs.WriteFileAsync(wsPath.Value,
                        bytes.AsMemory(),
                        ctx.CancellationToken);

                    int lineCount = content.AsSpan().Count('\n') + 1;
                    return new ToolResult($"Wrote {path} ({FormatSize.Format(bytes.Length)}, {lineCount} lines).",
                        IsError: false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return new ToolResult($"Error writing '{path}': {ex.Message}",
                        IsError: true);
                }
            }));
    }

    private static int? TryGetInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out object? val) || val is null)
        {
            return null;
        }

        if (val is int i)
        {
            return i;
        }

        if (
            val is JsonElement je
            && je.ValueKind == JsonValueKind.Number
            && je.TryGetInt32(out int ji)
        )
        {
            return ji;
        }

        if (val is long l)
        {
            return (int)l;
        }

        if (val is double d)
        {
            return (int)d;
        }

        string? str = val.ToString();
        if (string.IsNullOrEmpty(str))
        {
            return null;
        }

        // Support 0x-prefixed hex values
        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (
                int.TryParse(str[2..],
                    NumberStyles.HexNumber,
                    null,
                    out int hexVal)
            )
            {
                return hexVal;
            }

            return null;
        }

        if (int.TryParse(str, out int parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    ///     Build a ContentProcessorContext from tool invocation context and file info.
    /// </summary>
    private ContentProcessorContext BuildContext(
        ToolInvocationContext ctx,
        string relativePath,
        WorkspacePath wsPath,
        ReadOnlyMemory<byte> bytes,
        long fileSize,
        string format,
        int? offset,
        int? limit)
    {
        string absPath = Path.Combine(_vfs.RootPath, wsPath.Value);
        return new ContentProcessorContext(absPath,
            relativePath,
            bytes,
            fileSize,
            ctx.SessionId,
            ctx.ModelMetadata,
            format,
            offset,
            limit,
            _eventSink,
            ctx.CancellationToken);
    }

    /// <summary>
    ///     Parsed edit operation from tool arguments.
    /// </summary>
    private sealed record ParsedEdit(
        int StartLine,
        string StartHash,
        string OldText,
        string NewText);
}
