using Microsoft.CodeAnalysis;

namespace Omicron.Core.Generators;

internal static class DiagnosticDescriptors
{
    private const string Category = "Utf8FormatterGenerator";

    public static readonly DiagnosticDescriptor InvalidRegistration = new DiagnosticDescriptor(
        id: "OMN001",
        title: "Invalid Utf8Formatter registration",
        messageFormat: "Type '{0}' does not implement IUtf8SpanFormattable or ISpanFormattable",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Types registered with [Utf8FormatterAttribute<T>] must implement IUtf8SpanFormattable or ISpanFormattable.");

    public static readonly DiagnosticDescriptor InternalError = new DiagnosticDescriptor(
        id: "OMN002",
        title: "Utf8FormatterGenerator internal error",
        messageFormat: "Internal error: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An unexpected error occurred in the Utf8FormatterGenerator.");
}
