using System.Text.Json;
using Omicron.Core.Tools;
using Omicron.Core.Workspace;

namespace Omicron.Core.Extensions;

/// <summary>
/// Built-in extension that registers the read_path tool,
/// implemented through the IWorkspace abstraction.
/// </summary>
public sealed class BuiltinWorkspaceToolsExtension : IOmicronExtension
{
    private readonly IWorkspace _workspace;

    public string Id => "omicron.workspace-tools";
    public string DisplayName => "Workspace Tools";
    public Version Version => new(1, 0, 0);

    public BuiltinWorkspaceToolsExtension(IWorkspace workspace)
    {
        _workspace = workspace;
    }

    public void Register(IExtensionContext context)
    {
        context.RegisterTool(new ToolDefinition(
            Name: "read_path",
            Description:
                "Read a file or list a directory. " +
                "For files: returns content with line numbers, size, and line count. " +
                "Supports line offset/limit and byte-based chunk indexing for large files. " +
                "For directories: lists entries with per-file line count and size, " +
                "per-folder direct-child counts, and a summary. " +
                "Output is truncated at 50 KB / 2000 lines per call; use chunk or offset to continue.",
            Parameters: ToolSchema.Object(
                new Dictionary<string, JsonElement>
                {
                    ["path"] = ToolSchema.StringProperty(
                        "Path to read. If a file: reads content. If a directory: lists contents."),
                    ["offset"] = ToolSchema.IntegerProperty(
                        "1-based line number to start reading from. Ignored for directories."),
                    ["limit"] = ToolSchema.IntegerProperty(
                        "Maximum lines to return. Defaults to fit within truncation limit. Ignored for directories."),
                    ["chunk"] = ToolSchema.IntegerProperty(
                        "0-based chunk index for byte-based access. " +
                        "Each chunk is ~50 KB. " +
                        "When set, offset is relative to the start of this chunk. " +
                        "Useful for resuming after truncated output. Ignored for directories.")
                },
                required: new[] { "path" }
            ),
            InvokeAsync: async ctx =>
            {
                var args = ctx.Arguments;
                var path = args.TryGetValue("path", out var p) ? p?.ToString() ?? "" : "";
                var offset = TryGetInt(args, "offset");
                var limit = TryGetInt(args, "limit");
                var chunk = TryGetInt(args, "chunk");

                var result = await _workspace.ReadPathAsync(
                    path,
                    new ReadOptions { Offset = offset, Limit = limit, Chunk = chunk },
                    ctx.CancellationToken);

                return new ToolResult(result.Content, IsError: result.Content.StartsWith("Error"));
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
        if (int.TryParse(val.ToString(), out var parsed)) return parsed;
        return null;
    }
}
