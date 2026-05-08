using System.Data;
using System.Text.Json;
using Omicron.Core.Tools;

namespace Omicron.Core.Extensions;

/// <summary>
/// Built-in C# extension that registers calculator and time tools.
/// These tools were previously registered directly in Program.cs.
/// Now they use the same registration path that future WASM plugins will use.
/// </summary>
public sealed class BuiltinToolsExtension : IOmicronExtension
{
    public string Id => "omicron.builtin-tools";
    public string DisplayName => "Built-in Tools";
    public Version Version => new(1, 0, 0);

    public BuiltinToolsExtension()
    {
    }

    public void Register(IExtensionContext context)
    {
        // Calculator tool
        context.RegisterTool(new ToolDefinition(
            Name: "calculator",
            Description: "Evaluate a mathematical expression",
            Parameters: ToolSchema.Object(
                new Dictionary<string, JsonElement>
                {
                    ["expression"] = ToolSchema.StringProperty(
                        "The mathematical expression to evaluate (e.g., '2 + 2 * 3')")
                },
                required: new[] { "expression" }
            ),
            InvokeAsync: async ctx =>
            {
                var expr = ctx.Arguments?.GetValueOrDefault("expression")?.ToString() ?? "";
                try
                {
                    var result = new DataTable().Compute(expr, null);
                    return new ToolResult($"```\n{expr} = {result}\n```");
                }
                catch (Exception ex)
                {
                    return new ToolResult($"Error evaluating '{expr}': {ex.Message}", IsError: true);
                }
            }
        ));

        // Current time tool
        context.RegisterTool(new ToolDefinition(
            Name: "get_current_time",
            Description: "Get the current date and time",
            Parameters: ToolSchema.Object(
                new Dictionary<string, JsonElement>
                {
                    ["timezone"] = ToolSchema.StringProperty(
                        "Optional timezone (e.g., 'UTC', 'America/New_York')")
                },
                required: new[] { "timezone" }
            ),
            InvokeAsync: async ctx =>
            {
                var tz = ctx.Arguments?.GetValueOrDefault("timezone")?.ToString();
                var now = string.IsNullOrEmpty(tz)
                    ? DateTime.UtcNow
                    : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, tz!);
                return new ToolResult(
                    $"Current time: {now:yyyy-MM-dd HH:mm:ss} {(string.IsNullOrEmpty(tz) ? "UTC" : tz)}");
            }
        ));
    }
}
