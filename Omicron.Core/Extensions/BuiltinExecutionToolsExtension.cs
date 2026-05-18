using System.Text;
using System.Text.Json;
using Cysharp.Text;
using Omicron.Core.Execution;
using Omicron.Core.Tools;
using Omicron.Core.Workspace;

namespace Omicron.Core.Extensions;

/// <summary>
/// Built-in extension that registers the shell tool,
/// implemented through the IExecutionBroker abstraction.
/// </summary>
public sealed class BuiltinExecutionToolsExtension : IOmicronExtension
{
    private readonly IExecutionBroker _execution;
    private readonly IWorkspace _workspace;

    public string Id => "omicron.execution-tools";
    public string DisplayName => "Execution Tools";
    public Version Version => new(1, 0, 0);

    public BuiltinExecutionToolsExtension(IExecutionBroker execution, IWorkspace workspace)
    {
        _execution = execution;
        _workspace = workspace;
    }

    public void Register(IExtensionContext context)
    {
        context.RegisterTool(new ToolDefinition(
            Name: "shell",
            Description:
                "Execute a command in the specified shell. " +
                $"Default timeout is {LocalExecutionBroker.DefaultTimeoutSeconds}s. " +
                $"Output is truncated at {LocalExecutionBroker.MaxOutputBytes / 1024} KB / {LocalExecutionBroker.MaxOutputLines} lines. " +
                "Commands run in the workspace directory by default; override with `cwd`. " +
                "Prefer short, focused commands.",
            Parameters: ToolSchema.Object(
                new Dictionary<string, JsonElement>
                {
                    ["shell"] = ToolSchema.StringProperty(
                        "Shell to use. Common shells: bash, sh, zsh, fish, pwsh, powershell, cmd"),
                    ["command"] = ToolSchema.StringProperty(
                        "The command to execute. Will be passed to the shell's command interpreter."),
                    ["timeout"] = ToolSchema.IntegerProperty(
                        $"Timeout in seconds (default: {LocalExecutionBroker.DefaultTimeoutSeconds}, max: 600)."),
                    ["cwd"] = ToolSchema.StringProperty(
                        "Working directory relative to workspace root. Default: workspace root.")
                },
                required: new[] { "shell", "command" }
            ),
            InvokeAsync: async ctx =>
            {
                var args = ctx.Arguments;
                var shellId = args.TryGetValue("shell", out var s) ? s?.ToString() ?? "" : "";
                var command = args.TryGetValue("command", out var c) ? c?.ToString() ?? "" : "";
                var timeout = TryGetInt(args, "timeout") ?? LocalExecutionBroker.DefaultTimeoutSeconds;
                var cwd = args.TryGetValue("cwd", out var cw) ? cw?.ToString() : null;

                if (string.IsNullOrWhiteSpace(command))
                    return new ToolResult("Error: command is required.", IsError: true);

                // Resolve working directory relative to workspace root
                var workDir = _workspace.RootPath;
                if (!string.IsNullOrWhiteSpace(cwd))
                {
                    var resolved = _workspace.ResolvePath(cwd);
                    if (resolved is null)
                        return new ToolResult($"Error: cwd escapes workspace root: {cwd}", IsError: true);
                    if (!Directory.Exists(resolved))
                        return new ToolResult($"Error: cwd not found or not a directory: {cwd}", IsError: true);
                    workDir = resolved;
                }

                var result = await _execution.ExecuteAsync(
                    new ExecutionRequest(ctx.SessionId, command, shellId, workDir, timeout,
                        ctx.ToolCallId),
                    ctx.CancellationToken);

                // Build header and combine with result bytes
                var combined = ZString.CreateUtf8StringBuilder();
                try
                {
                    combined.AppendLiteral("[shell: "u8);
                    combined.Append(shellId);
                    combined.AppendLiteral("] [cwd: "u8);
                    combined.Append(workDir);
                    combined.Append(']');
                    combined.AppendLine();
                    combined.AppendLiteral("$ "u8);
                    combined.Append(command);
                    combined.AppendLine();
                    combined.AppendLine();

                    if (result.Utf8Output.HasValue)
                    {
                        combined.AppendLiteral(result.Utf8Output.Value.Span);
                    }
                    else
                    {
                        combined.Append(result.Output);
                    }

                    return new ToolResult(
                        Utf8Data: combined.AsSpan().ToArray(),
                        IsError: result.ExitCode != 0 || result.TimedOut);
                }
                finally
                {
                    combined.Dispose();
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
        if (int.TryParse(val.ToString(), out var parsed)) return parsed;
        return null;
    }
}
