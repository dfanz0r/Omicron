using System.Text;
using System.Text.Json;
using Omicron.Core.Diff;
using Omicron.Core.Events;
using Omicron.Core.IO;
using Omicron.Core.Tools;
using Omicron.Core.Workspace;

namespace Omicron.Core.Extensions;

/// <summary>
/// Built-in extension that registers workspace tools including:
/// - read_path: read files and list directories
/// - read_file_hashlines: read a file with per-line hash anchors
/// - edit_file_hashline: edit a file with hash-anchor validation through transactions
/// </summary>
public sealed class BuiltinWorkspaceToolsExtension : IOmicronExtension
{
    private readonly IWorkspace _workspace;
    private readonly IWorkspaceFileSystem _vfs;
    private readonly IWorkspaceTransactionManager _txManager;
    private readonly ContentProcessorRegistry _processors;
    private readonly Base64Processor _base64Processor;
    private readonly IEventSink? _eventSink;

    public string Id => "omicron.workspace-tools";
    public string DisplayName => "Workspace Tools";
    public Version Version => new(1, 0, 0);

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

    public void Register(IExtensionContext context)
    {
        context.RegisterTool(new ToolDefinition(
            Name: "read_path",
            Description:
                "Read a file or list a directory. " +
                "For files: returns content (text, hex dump, or base64) based on " +
                "file type and model capabilities. " +
                "Supports format override: 'auto' (default), 'text', 'hex', or 'base64'. " +
                "For directories: lists entries with per-file line count and size. " +
                "Output is truncated at 50 KB / 2000 lines per call.",
            Parameters: ToolSchema.Object(
                new Dictionary<string, JsonElement>
                {
                    ["path"] = ToolSchema.StringProperty(
                        "Path to read. If a file: reads content. If a directory: lists contents."),
                    ["format"] = ToolSchema.EnumProperty(
                        "Output format: 'auto' (default, auto-detect), 'text' (force UTF-8), " +
                        "'hex' (hex dump), or 'base64' (raw base64 encoding).",
                        new[] { "auto", "text", "hex", "base64" }),
                    ["offset"] = ToolSchema.IntegerProperty(
                        "Byte offset (hex/base64 mode) or 1-based line number (text mode). " +
                        "For hex, accepts decimal or 0x-prefixed hex values."),
                    ["limit"] = ToolSchema.IntegerProperty(
                        "Byte limit (hex/base64) or max lines (text). " +
                        "For hex, accepts decimal or 0x-prefixed hex.")
                },
                required: new[] { "path" }
            ),
            InvokeAsync: async ctx =>
            {
                var args = ctx.Arguments;
                var path = args.TryGetValue("path", out var p) ? p?.ToString() ?? "" : "";
                var format = args.TryGetValue("format", out var f) ? f?.ToString() ?? "auto" : "auto";
                var offset = TryGetInt(args, "offset");
                var limit = TryGetInt(args, "limit");

                try
                {
                    // --- Resolve path ---
                    var wsPath = _vfs.Resolve(path);
                    if (wsPath is null)
                        return new ToolResult($"Error: path escapes workspace root: '{path}'", IsError: true);

                    var stat = await _vfs.StatAsync(wsPath.Value, ctx.CancellationToken);
                    if (stat is null)
                        return new ToolResult($"Error: path not found: {path}", IsError: true);

                    // --- Directory listing (restored with binary indicator and sizes) ---
                    if (stat.IsDirectory)
                    {
                        var allEntries = await _vfs.ReadDirectoryAsync(wsPath.Value, ctx.CancellationToken);
                        var sorted = allEntries
                            .OrderBy(e => e.IsDirectory ? 0 : 1)
                            .ThenBy(e => e.Name, StringComparer.Ordinal)
                            .ToList();
                        var shown = sorted.Take(200).ToList();

                        var sb = new StringBuilder();
                        sb.AppendLine($"[DIR] {path}/  ({sorted.Count(e => !e.IsDirectory)} files, {sorted.Count(e => e.IsDirectory)} dirs)");
                        sb.AppendLine();
                        foreach (var entry in shown)
                        {
                            if (entry.IsDirectory)
                                sb.AppendLine($"  [DIR]  {entry.Name}");
                            else if (entry.LineCount.HasValue)
                                sb.AppendLine($"  [FILE] {entry.Name}  {FormatSize(entry.Size)}  ({entry.LineCount} lines)");
                            else
                                sb.AppendLine($"  [FILE] {entry.Name}  {FormatSize(entry.Size)}  (binary)");
                        }
                        if (sorted.Count > 200)
                            sb.AppendLine($"... ({sorted.Count} total entries)");
                        sb.AppendLine();
                        sb.AppendLine($"{sorted.Count(e => !e.IsDirectory)} files, {sorted.Count(e => e.IsDirectory)} dirs");

                        return new ToolResult(sb.ToString().TrimEnd());
                    }

                    // --- Read file bytes ---
                    var bytes = await _vfs.ReadFileAsync(wsPath.Value, ctx.CancellationToken);
                    if (bytes.IsEmpty && stat.Size > 0)
                        return new ToolResult($"Error: could not read file: {path}", IsError: true);

                    // --- Handle format override ---
                    IContentProcessor processor;
                    string effectiveFormat = format.ToLowerInvariant();

                    // File too large for memory — downgrade to hex
                    if (stat.Size > 50 * 1024 * 1024 && effectiveFormat == "auto")
                        effectiveFormat = "hex";

                    // Cap base64 encoding to 5 MB
                    if (effectiveFormat == "base64" && stat.Size > 5 * 1024 * 1024)
                        effectiveFormat = "hex";

                    if (effectiveFormat == "hex")
                    {
                        processor = new HexDumpProcessor();
                    }
                    else if (effectiveFormat == "base64")
                    {
                        var b64Ctx = BuildContext(ctx, path, wsPath.Value, bytes, stat.Size, "base64", offset, limit);
                        var b64Result = await _base64Processor.ProcessAsync(b64Ctx, ctx.CancellationToken);

                        // Emit ModalityUsedEvent for base64 outputs that require model capabilities
                        if (b64Result.ActualModality is OutputModality.ImageBase64 or OutputModality.PdfBase64
                            or OutputModality.AudioBase64 or OutputModality.VideoBase64)
                        {
                            var b64Modality = b64Result.ActualModality switch
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
                                    b64Modality, path, b64Result.ActualModality));
                            }
                        }

                        return new ToolResult(b64Result.Text, IsError: false);
                    }
                    else if (effectiveFormat == "text")
                    {
                        processor = new TextProcessor();
                    }
                    else
                    {
                        // "auto": classify and resolve
                        var fileType = FileTypeClassifier.Classify(path, bytes.Span);

                        if (fileType == DetectedFileType.Text)
                        {
                            // Run IsText() safety check for files classified as Text
                            if (!TextEncodingDetector.IsText(bytes.Span))
                                fileType = DetectedFileType.UnknownBinary;
                        }

                        processor = _processors.Resolve(fileType) ?? new HexDumpProcessor();
                    }

                    var context = BuildContext(ctx, path, wsPath.Value, bytes, stat.Size, effectiveFormat, offset, limit);
                    var result = await processor.ProcessAsync(context, ctx.CancellationToken);

                    // --- Emit ModalityUsedEvent if needed ---
                    if (result.ActualModality is OutputModality.ImageBase64 or OutputModality.PdfBase64
                        or OutputModality.AudioBase64 or OutputModality.VideoBase64)
                    {
                        var modality = result.ActualModality switch
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
                                modality, path, result.ActualModality));
                        }
                    }

                    return new ToolResult(result.Text, IsError: false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return new ToolResult($"Error reading '{path}': {ex.Message}", IsError: true);
                }
            }
        ));

        // ============================================================
        // read_file_hashlines — read a file with per-line hash anchors
        // ============================================================

        context.RegisterTool(new ToolDefinition(
            Name: "read_file_hashlines",
            Description:
                "Read a text file with per-line hash anchors. " +
                "Each line shows a 2-letter anchor (a-z, no digits) derived from line content. " +
                "Use the anchor as start_hash when calling edit_file_hashline. " +
                "Format: {line}{anchor}|{content}. " +
                "Example: 42sr|    return a + b;\n" +
                "Rejects binary files. " +
                "Output is truncated at 2000 lines / 50 KB.",
            Parameters: ToolSchema.Object(
                new Dictionary<string, JsonElement>
                {
                    ["path"] = ToolSchema.StringProperty(
                        "Path to the text file to read (relative to workspace root).")
                },
                required: new[] { "path" }
            ),
            InvokeAsync: async ctx =>
            {
                var args = ctx.Arguments;
                var path = args.TryGetValue("path", out var p) ? p?.ToString() ?? "" : "";

                try
                {
                    var wsPath = _vfs.Resolve(path);
                    if (wsPath is null)
                        return new ToolResult($"Error: path escapes workspace root: '{path}'", IsError: true);

                    var stat = await _vfs.StatAsync(wsPath.Value, ctx.CancellationToken);
                    if (stat is null)
                        return new ToolResult($"Error: path not found: {path}", IsError: true);

                    var bytes = await _vfs.ReadFileAsync(wsPath.Value, ctx.CancellationToken);
                    if (bytes.IsEmpty && stat.Size > 0)
                        return new ToolResult($"Error: could not read file: {path}", IsError: true);

                    if (!TextEncodingDetector.IsText(bytes.Span))
                        return new ToolResult($"Error: binary file: {path}", IsError: true);

                    var text = Encoding.UTF8.GetString(bytes.Span);
                    var allLines = text.Replace("\r\n", "\n").Split('\n');
                    var totalLines = allLines.Length;

                    var sb = new StringBuilder();
                    sb.AppendLine($"[FILE] {path}  ({totalLines} lines, hashline anchors)");

                    const int maxOutputLines = 2000;
                    const int maxOutputBytes = 50 * 1024;
                    int writtenLines = 0;
                    int writtenBytes = 0;
                    bool truncated = false;

                    for (int i = 0; i < allLines.Length; i++)
                    {
                        var lineText = allLines[i];
                        var anchor = LineHash.ComputeAnchor(lineText);
                        var formatted = LineHash.FormatLine(i + 1, anchor, lineText);
                        var lineBytes = Encoding.UTF8.GetByteCount(formatted) + 1; // +1 for newline

                        if (writtenLines >= maxOutputLines ||
                            (writtenBytes > 0 && writtenBytes + lineBytes > maxOutputBytes))
                        {
                            truncated = i < allLines.Length - 1;
                            break;
                        }

                        sb.AppendLine(formatted);
                        writtenLines++;
                        writtenBytes += lineBytes;
                    }

                    if (truncated)
                        sb.AppendLine($"... output truncated ({totalLines} total lines, showing {writtenLines})");

                    return new ToolResult(sb.ToString().TrimEnd());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new ToolResult($"Error reading '{path}': {ex.Message}", IsError: true);
                }
            }
        ));

        // ============================================================
        // edit_file_hashline — edit a file with hash-anchor validation
        // ============================================================

        context.RegisterTool(new ToolDefinition(
            Name: "edit_file_hashline",
            Description:
                "Edit a text file with hash-anchor validation through a workspace transaction. " +
                "Each edit targets a line by its start_line and start_hash (2-letter anchor " +
                "from read_file_hashlines output). old_text is validated as a second guard. " +
                "Edits must be non-overlapping. On success, returns a unified diff. " +
                "On validation failure, includes nearby current context and hashes. " +
                "Auto-commits after validation.",
            Parameters: ToolSchema.Object(
                new Dictionary<string, JsonElement>
                {
                    ["path"] = ToolSchema.StringProperty(
                        "Path to the file to edit (relative to workspace root)."),
                    ["edits"] = ToolSchema.ArrayProperty(
                        "Array of edit operations. Each edit: { start_line (int), start_hash (string), old_text (string), new_text (string) }",
                        ToolSchema.Object(
                            new Dictionary<string, JsonElement>
                            {
                                ["start_line"] = ToolSchema.IntegerProperty(
                                    "1-based line number where the edit begins (hint; rebased ±5 if hash matches nearby)."),
                                ["start_hash"] = ToolSchema.StringProperty(
                                    "2-letter anchor (a-z) from read_file_hashlines output for the target line."),
                                ["old_text"] = ToolSchema.StringProperty(
                                    "The exact existing text starting at this line (may span multiple lines)."),
                                ["new_text"] = ToolSchema.StringProperty(
                                    "The replacement text.")
                            },
                            required: new[] { "start_line", "start_hash", "old_text", "new_text" }
                        )
                    )
                },
                required: new[] { "path", "edits" }
            ),
            InvokeAsync: async ctx =>
            {
                var args = ctx.Arguments;
                var path = args.TryGetValue("path", out var p) ? p?.ToString() ?? "" : "";
                var editsRaw = args!.GetValueOrDefault("edits");

                try
                {
                    // ---------- resolve path ----------

                    var wsPath = _vfs.Resolve(path);
                    if (wsPath is null)
                        return new ToolResult($"Error: path escapes workspace root: '{path}'", IsError: true);

                    var stat = await _vfs.StatAsync(wsPath.Value, ctx.CancellationToken);
                    if (stat is null)
                        return new ToolResult($"Error: path not found: {path}", IsError: true);

                    // ---------- read current file ----------

                    var bytes = await _vfs.ReadFileAsync(wsPath.Value, ctx.CancellationToken);
                    if (bytes.IsEmpty && stat.Size > 0)
                        return new ToolResult($"Error: could not read file: {path}", IsError: true);

                    // Content-based binary detection (BOM / null-byte / UTF-8 decode / printable ratio).
                    if (!TextEncodingDetector.IsText(bytes.Span))
                        return new ToolResult($"Error: binary file cannot be edited: {path}", IsError: true);

                    var currentText = Encoding.UTF8.GetString(bytes.Span);

                    var currentLines = currentText.Replace("\r\n", "\n").Split('\n');

                    // ---------- parse edits ----------

                    var parsedEdits = new List<ParsedEdit>();

                    if (editsRaw is JsonElement editsJe && editsJe.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in editsJe.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object)
                            {
                                var sl = item.TryGetProperty("start_line", out var slProp) ? slProp.GetInt32() : 0;
                                var sh = item.TryGetProperty("start_hash", out var shProp) ? shProp.GetString() ?? "" : "";
                                var ot = item.TryGetProperty("old_text", out var otProp) ? otProp.GetString() ?? "" : "";
                                var nt = item.TryGetProperty("new_text", out var ntProp) ? ntProp.GetString() ?? "" : "";
                                parsedEdits.Add(new ParsedEdit(sl, sh, ot, nt));
                            }
                        }
                    }

                    if (parsedEdits.Count == 0)
                        return new ToolResult("Error: no valid edits provided. Supply an 'edits' array with objects containing start_line, start_hash, old_text, new_text.", IsError: true);

                    // Sort by start_line ascending
                    parsedEdits.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));

                    // ---------- resolve hashes with rebase ----------

                    var resolvedEdits = new List<(ParsedEdit Edit, int ActualLine, int LineCount)>();
                    var errors = new List<string>();

                    foreach (var edit in parsedEdits)
                    {
                        var oldLines = edit.OldText.Replace("\r\n", "\n").Split('\n');
                        var lineCount = oldLines.Length;

                        // Try exact line first, then rebase ±5
                        int foundLine = -1;
                        int searchStart = Math.Max(1, edit.StartLine - 5);
                        int searchEnd = Math.Min(currentLines.Length, edit.StartLine + 5);

                        for (int line = searchStart; line <= searchEnd; line++)
                        {
                            var lineHash = LineHash.ComputeAnchor(currentLines[line - 1]);
                            if (lineHash == edit.StartHash)
                            {
                                foundLine = line;
                                break;
                            }
                        }

                        if (foundLine == -1)
                        {
                            // Build nearby context for error
                            var context = new StringBuilder();
                            context.AppendLine($"Hash mismatch at line {edit.StartLine} (expected hash '{edit.StartHash}'):");
                            int ctxStart = Math.Max(1, edit.StartLine - 3);
                            int ctxEnd = Math.Min(currentLines.Length, edit.StartLine + 3);
                            for (int l = ctxStart; l <= ctxEnd; l++)
                            {
                                var h = LineHash.ComputeAnchor(currentLines[l - 1]);
                                context.AppendLine($"  {l}{h}|{currentLines[l - 1]}");
                            }
                            errors.Add(context.ToString().TrimEnd());
                            continue;
                        }

                        // Validate old_text matches at found line
                        var actualSpan = string.Join("\n",
                            currentLines.Skip(foundLine - 1).Take(lineCount));
                        if (actualSpan != edit.OldText)
                        {
                            var context = new StringBuilder();
                            context.AppendLine($"old_text mismatch at resolved line {foundLine} (hash '{edit.StartHash}'):");
                            context.AppendLine($"  Expected: {edit.OldText}");
                            context.AppendLine($"  Actual:   {actualSpan}");
                            int ctxStart = Math.Max(1, foundLine - 2);
                            int ctxEnd = Math.Min(currentLines.Length, foundLine + lineCount + 1);
                            for (int l = ctxStart; l <= ctxEnd; l++)
                            {
                                var h = LineHash.ComputeAnchor(currentLines[l - 1]);
                                var marker = (l >= foundLine && l < foundLine + lineCount) ? "~~" : "  ";
                                context.AppendLine($"{marker}{l}{h}|{currentLines[l - 1]}");
                            }
                            errors.Add(context.ToString().TrimEnd());
                            continue;
                        }

                        resolvedEdits.Add((edit, foundLine, lineCount));
                    }

                    if (errors.Count > 0)
                        return new ToolResult("Validation errors:\n\n" + string.Join("\n\n", errors), IsError: true);

                    // ---------- check for overlapping edits ----------

                    for (int i = 1; i < resolvedEdits.Count; i++)
                    {
                        var prev = resolvedEdits[i - 1];
                        var curr = resolvedEdits[i];
                        var prevEnd = prev.ActualLine + prev.LineCount - 1;
                        if (curr.ActualLine <= prevEnd)
                        {
                            return new ToolResult(
                                $"Error: overlapping edits. " +
                                $"Edit at line {prev.Edit.StartLine} (resolved {prev.ActualLine}, " +
                                $"{prev.LineCount} line(s)) overlaps with edit at line {curr.Edit.StartLine} " +
                                $"(resolved {curr.ActualLine}).",
                                IsError: true);
                        }
                    }

                    // ---------- apply edits (bottom-up to preserve line numbers) ----------

                    var resultLines = new List<string>(currentLines);
                    for (int i = resolvedEdits.Count - 1; i >= 0; i--)
                    {
                        var (edit, actualLine, lineCount) = resolvedEdits[i];
                        var newLines = edit.NewText.Replace("\r\n", "\n").Split('\n');

                        // Remove old lines, insert new lines
                        resultLines.RemoveRange(actualLine - 1, lineCount);
                        resultLines.InsertRange(actualLine - 1, newLines);
                    }

                    var newText = string.Join("\n", resultLines);

                    // ---------- create transaction, stage, diff, commit ----------

                    await using var tx = _txManager.BeginTransaction();

                    var newBytes = Encoding.UTF8.GetBytes(newText);
                    await tx.Files.WriteFileAsync(wsPath.Value, newBytes.AsMemory(), ctx.CancellationToken);

                    var diff = await tx.GetDiffAsync(ctx.CancellationToken);

                    await tx.CommitAsync(ctx.CancellationToken);

                    // ---------- format result ----------

                    var resultSb = new StringBuilder();
                    resultSb.AppendLine($"Edit committed: {path}");

                    if (diff.HasChanges)
                    {
                        resultSb.AppendLine();
                        var fileDiff = diff.FileDiffs[0];
                        if (fileDiff.IsBinary)
                            resultSb.AppendLine("(binary file diff omitted)");
                        else if (!string.IsNullOrEmpty(fileDiff.TextDiff))
                            resultSb.AppendLine(fileDiff.TextDiff);
                        else
                            resultSb.AppendLine("(no textual changes)");
                    }

                    // Remind the model to call read_file_hashlines for fresh anchors
                    resultSb.AppendLine();
                    resultSb.AppendLine("Call read_file_hashlines to get fresh line anchors if you need to make further edits.");

                    return new ToolResult(resultSb.ToString().TrimEnd());
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return new ToolResult($"Error editing '{path}': {ex.Message}", IsError: true);
                }
            }
        ));

        // ============================================================
        // write_file — create or overwrite a file
        // ============================================================

        context.RegisterTool(new ToolDefinition(
            Name: "write_file",
            Description:
                "Create a new file or overwrite an existing file with the given content. " +
                "Parent directories are created automatically. " +
                "Use this to create new files; use edit_file_hashline to modify existing files safely. " +
                "Paths are relative to the workspace root.",
            Parameters: ToolSchema.Object(
                new Dictionary<string, JsonElement>
                {
                    ["path"] = ToolSchema.StringProperty(
                        "Path to the file to create or overwrite (relative to workspace root)."),
                    ["content"] = ToolSchema.StringProperty(
                        "The text content to write to the file.")
                },
                required: new[] { "path", "content" }
            ),
            InvokeAsync: async ctx =>
            {
                var args = ctx.Arguments;
                var path = args.TryGetValue("path", out var p) ? p?.ToString() ?? "" : "";
                var content = args.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";

                try
                {
                    var wsPath = _vfs.Resolve(path);
                    if (wsPath is null)
                        return new ToolResult($"Error: path escapes workspace root: '{path}'", IsError: true);

                    var bytes = Encoding.UTF8.GetBytes(content);

                    // VFS creates parent directories automatically
                    await _vfs.WriteFileAsync(wsPath.Value, bytes.AsMemory(), ctx.CancellationToken);

                    var lines = content.Replace("\r\n", "\n").Split('\n').Length;
                    return new ToolResult(
                        $"Wrote {path} ({FormatSize(bytes.Length)}, {lines} lines).",
                        IsError: false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return new ToolResult($"Error writing '{path}': {ex.Message}", IsError: true);
                }
            }
        ));
    }

    private static int? TryGetInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val is null) return null;
        if (val is int i) return i;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var ji))
            return ji;
        if (val is long l) return (int)l;
        if (val is double d) return (int)d;

        var str = val.ToString();
        if (string.IsNullOrEmpty(str)) return null;

        // Support 0x-prefixed hex values
        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(str[2..], System.Globalization.NumberStyles.HexNumber, null, out var hexVal))
                return hexVal;
            return null;
        }

        if (int.TryParse(str, out var parsed)) return parsed;
        return null;
    }

    /// <summary>
    /// Build a ContentProcessorContext from tool invocation context and file info.
    /// </summary>
    /// <summary>Format file size for human-readable display.</summary>
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

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
        var absPath = Path.Combine(_vfs.RootPath, wsPath.Value);
        return new ContentProcessorContext(
            AbsolutePath: absPath,
            RelativePath: relativePath,
            Bytes: bytes,
            FileSize: fileSize,
            SessionId: ctx.SessionId,
            ModelMetadata: ctx.ModelMetadata,
            Format: format,
            Offset: offset,
            Limit: limit,
            EventSink: _eventSink,
            CancellationToken: ctx.CancellationToken);
    }

    /// <summary>
    /// Parsed edit operation from tool arguments.
    /// </summary>
    private sealed record ParsedEdit(
        int StartLine,
        string StartHash,
        string OldText,
        string NewText);
}
