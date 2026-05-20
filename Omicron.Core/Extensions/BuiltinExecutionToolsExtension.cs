using System.Text.Json;
using Omicron.Core.Content;
using Omicron.Core.Execution;
using Omicron.Core.Text;
using Omicron.Core.Tools;
using Omicron.Core.Workspace;

namespace Omicron.Core.Extensions;

/// <summary>
///     Built-in extension that registers the shell tool,
///     implemented through the IExecutionBroker abstraction.
/// </summary>
public sealed class BuiltinExecutionToolsExtension : IOmicronExtension
{
    private readonly IExecutionBroker _execution;

    private static Utf8String ToolErrorUtf8(ReadOnlySpan<byte> prefix, string value)
    {
        using var b = Utf8Text.CreateBuilder();
        b.AppendLiteral("Error: "u8);
        b.AppendLiteral(prefix);
        b.Append(value);
        return Utf8String.FromUtf8(b.AsSpan());
    }
    private readonly IWorkspace _workspace;

    public BuiltinExecutionToolsExtension(IExecutionBroker execution, IWorkspace workspace)
    {
        _execution = execution;
        _workspace = workspace;
    }

    public string Id => "omicron.execution-tools";
    public string DisplayName => "Execution Tools";
    public Version Version => new(1, 0, 0);

    public void Register(IExtensionContext context)
    {
        context.RegisterTool(new ToolDefinition("shell",
            "Execute a command in the specified shell. "
            + $"Default timeout is {LocalExecutionBroker.DefaultTimeoutSeconds}s. "
            + $"Output is truncated at {LocalExecutionBroker.MaxOutputBytes / 1024} KB / {LocalExecutionBroker.MaxOutputLines} lines. "
            + "Commands run in the workspace directory by default; override with `cwd`. "
            + "Prefer short, focused commands.",
            ToolSchema.Object(new Dictionary<string, JsonElement>
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
                new[]
                {
                    "shell", "command"
                }),
            async ctx =>
            {
                IReadOnlyDictionary<string, object?> args = ctx.Arguments;
                string shellId = args.TryGetValue("shell", out object? s) ? s?.ToString() ?? "" : "";
                string command = args.TryGetValue("command", out object? c) ? c?.ToString() ?? "" : "";
                int timeout =
                    TryGetInt(args, "timeout") ?? LocalExecutionBroker.DefaultTimeoutSeconds;
                string? cwd = args.TryGetValue("cwd", out object? cw) ? cw?.ToString() : null;

                if (string.IsNullOrWhiteSpace(command))
                {
                    return new ToolResult(TextData: Utf8String.FromUtf8("Error: command is required."u8), IsError: true);
                }

                // Resolve working directory relative to workspace root
                string workDir = _workspace.RootPath;
                if (!string.IsNullOrWhiteSpace(cwd))
                {
                    string? resolved = _workspace.ResolvePath(cwd);
                    if (resolved is null)
                    {
                        return new ToolResult(
                            TextData: ToolErrorUtf8("cwd escapes workspace root: "u8, cwd),
                            IsError: true);
                    }

                    if (!Directory.Exists(resolved))
                    {
                        return new ToolResult(
                            TextData: ToolErrorUtf8("cwd not found or not a directory: "u8, cwd),
                            IsError: true);
                    }

                    workDir = resolved;
                }

                ExecutionResult result = await _execution.ExecuteAsync(new ExecutionRequest(ctx.SessionId,
                        command,
                        shellId,
                        workDir,
                        timeout,
                        ctx.ToolCallId),
                    ctx.CancellationToken);

                // Build header and combine with result bytes
                Utf8Builder combined = Utf8Text.CreateBuilder();
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

                    return new ToolResult(TextData: Utf8String.FromUtf8(combined.AsSpan()),
                        IsError: result.ExitCode != 0 || result.TimedOut);
                }
                finally
                {
                    combined.Dispose();
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

        if (int.TryParse(val.ToString(), out int parsed))
        {
            return parsed;
        }

        return null;
    }
}
