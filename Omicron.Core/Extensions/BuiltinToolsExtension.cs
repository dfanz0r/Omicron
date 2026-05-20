using System.Data;
using System.Text.Json;
using Omicron.Core.Content;
using Omicron.Core.Text;
using Omicron.Core.Tools;

namespace Omicron.Core.Extensions;

/// <summary>
///     Built-in C# extension that registers calculator and time tools.
///     These tools were previously registered directly in Program.cs.
///     Now they use the same registration path that future WASM plugins will use.
/// </summary>
public sealed class BuiltinToolsExtension : IOmicronExtension
{
    public string Id => "omicron.builtin-tools";
    public string DisplayName => "Built-in Tools";
    public Version Version => new(1, 0, 0);

    public void Register(IExtensionContext context)
    {
        // Calculator tool
        context.RegisterTool(new ToolDefinition("calculator",
            "Evaluate a mathematical expression",
            ToolSchema.Object(new Dictionary<string, JsonElement>
                {
                    ["expression"] = ToolSchema.StringProperty(
                        "The mathematical expression to evaluate (e.g., '2 + 2 * 3')")
                },
                new[]
                {
                    "expression"
                }),
            async ctx =>
            {
                string expr = ctx.Arguments?.GetValueOrDefault("expression")?.ToString() ?? "";
                try
                {
                    object result = new DataTable().Compute(expr, null);
                    var r = Utf8Text.CreateBuilder();
                    try
                    {
                        r.AppendLiteral("```\n"u8);
                        r.Append(expr);
                        r.AppendLiteral(" = "u8);
                        r.Append(result?.ToString() ?? "");
                        r.AppendLiteral("\n```"u8);
                        return new ToolResult(TextData: Utf8String.FromUtf8(r.AsSpan()));
                    }
                    finally { r.Dispose(); }
                }
                catch (Exception ex)
                {
                    var r2 = Utf8Text.CreateBuilder();
                    try
                    {
                        r2.AppendLiteral("Error evaluating '"u8);
                        r2.Append(expr);
                        r2.AppendLiteral("': "u8);
                        r2.Append(ex.Message);
                        return new ToolResult(TextData: Utf8String.FromUtf8(r2.AsSpan()), IsError: true);
                    }
                    finally { r2.Dispose(); }
                }
            }));

        // Current time tool
        context.RegisterTool(new ToolDefinition("get_current_time",
            "Get the current date and time",
            ToolSchema.Object(new Dictionary<string, JsonElement>
                {
                    ["timezone"] = ToolSchema.StringProperty("Optional timezone (e.g., 'UTC', 'America/New_York')")
                },
                new[]
                {
                    "timezone"
                }),
            async ctx =>
            {
                string? tz = ctx.Arguments?.GetValueOrDefault("timezone")?.ToString();
                DateTime now = string.IsNullOrEmpty(tz)
                    ? DateTime.UtcNow
                    : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, tz!);
                var tzName = Utf8Text.CreateBuilder();
                try
                {
                    Utf8CompositeFormat.FormatSlow(ref tzName,
                    "Current time: {0:yyyy-MM-dd HH:mm:ss} {1}"u8,
                    now,
                    string.IsNullOrEmpty(tz) ? "UTC" : tz);
                    return new ToolResult(TextData: Utf8String.FromUtf8(tzName.AsSpan()));
                }
                finally
                {
                    tzName.Dispose();
                }
            }));
    }
}
