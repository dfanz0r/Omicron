using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Omicron.Core.Generators;

/// <summary>
/// Incremental source generator that emits custom formatter fast paths
/// for types registered with <c>Utf8FormatterAttribute{T}</c>.
/// </summary>
[Generator]
public class Utf8FormatterGenerator : IIncrementalGenerator
{
    private const string AttributeFullName = "Omicron.Core.Text.Utf8FormatterAttribute`1";
    private const string IUtf8SpanFormattableName = "System.IUtf8SpanFormattable";
    private const string ISpanFormattableName = "System.ISpanFormattable";
    private const string FormatterClassName = "Omicron.Core.Text.Utf8ValueFormatter";
    private const string GeneratedHookMethod = "TryFormatGenerated";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Combine compilation info with registration scanning
        var generatorState = context.CompilationProvider.Select(static (compilation, _) =>
        {
            var registrations = new List<Registration>();

            // Only emit when the defining declaration exists in this compilation.
            // Partial method definitions and implementations must be in the same
            // compilation. If Utf8ValueFormatter is only a metadata reference
            // (e.g. in the test project), we must not emit an implementation.
            bool shouldEmit = HasDefiningDeclaration(compilation);

            if (shouldEmit)
            {
                var attrType = compilation.GetTypeByMetadataName(AttributeFullName);
                if (attrType is not null)
                {
                    foreach (var attr in compilation.Assembly.GetAttributes())
                    {
                        if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass?.OriginalDefinition, attrType))
                            continue;

                        var typeArg = attr.AttributeClass?.TypeArguments.FirstOrDefault();
                        if (typeArg is null)
                            continue;

                        var reg = CreateRegistration(typeArg, compilation);
                        if (reg is not null)
                            registrations.Add(reg.Value);
                    }
                }
            }

            return new GeneratorState(registrations, shouldEmit);
        });

        context.RegisterSourceOutput(generatorState, EmitSource);
    }

    /// <summary>
    /// Check whether the current compilation contains source declarations for
    /// <c>Utf8ValueFormatter.TryFormatGenerated</c> — i.e. the partial method
    /// defining declaration exists in source (not just in a referenced assembly).
    /// </summary>
    private static bool HasDefiningDeclaration(Compilation compilation)
    {
        // Walk syntax trees looking for a partial class Utf8ValueFormatter
        // inside namespace Omicron.Core.Text that declares TryFormatGenerated.
        foreach (var tree in compilation.SyntaxTrees)
        {
            var root = tree.GetRoot();
            if (root is null)
                continue;

            var model = compilation.GetSemanticModel(tree);
            if (model is null)
                continue;

            // Find class declarations named Utf8ValueFormatter
            var classDecls = root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                .Where(c => c.Identifier.ValueText == "Utf8ValueFormatter");

            foreach (var classDecl in classDecls)
            {
                var symbol = model.GetDeclaredSymbol(classDecl);
                if (symbol is null)
                    continue;

                // Verify it's the right namespace
                if (symbol.ContainingNamespace?.ToDisplayString() != "Omicron.Core.Text")
                    continue;

                // Check for a partial method declaration named TryFormatGenerated
                // with no body (i.e. the defining declaration).
                var members = classDecl.Members;
                foreach (var member in members)
                {
                    if (member is Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax methodDecl
                        && methodDecl.Identifier.ValueText == GeneratedHookMethod
                        && methodDecl.Body is null
                        && methodDecl.ExpressionBody is null)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static Registration? CreateRegistration(ITypeSymbol typeArg, Compilation compilation)
    {
        var typeName = typeArg.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Check if it implements IUtf8SpanFormattable
        var iutf8 = compilation.GetTypeByMetadataName(IUtf8SpanFormattableName);
        bool hasIUtf8 = iutf8 is not null && TypeImplements(typeArg, iutf8);

        // Check if it implements ISpanFormattable
        var ispan = compilation.GetTypeByMetadataName(ISpanFormattableName);
        bool hasISpan = ispan is not null && TypeImplements(typeArg, ispan);

        // Check if it's a value type (for null-check generation)
        bool isValueType = typeArg.IsValueType;

        // Check accessibility - must be public or internal
        if (typeArg.DeclaredAccessibility != Accessibility.Public &&
            typeArg.DeclaredAccessibility != Accessibility.Internal)
        {
            return null; // skip non-visible types
        }

        if (!hasIUtf8 && !hasISpan)
        {
            // Return a registration with a diagnostic flag
            return new Registration(typeName, isValueType, hasIUtf8, hasISpan, hasError: true);
        }

        return new Registration(typeName, isValueType, hasIUtf8, hasISpan, hasError: false);
    }

    private static bool TypeImplements(ITypeSymbol type, INamedTypeSymbol interfaceType)
    {
        // Check all interfaces including inherited
        foreach (var iface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, interfaceType))
                return true;
        }
        // Also check the type itself (for direct implementation)
        if (type is INamedTypeSymbol named)
        {
            foreach (var iface in named.Interfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, interfaceType))
                    return true;
            }
        }
        return false;
    }

    private static void EmitSource(SourceProductionContext context, GeneratorState state)
    {
        if (!state.ShouldEmit)
            return;

        var registrations = state.Registrations;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> by Omicron.Core.Generators.Utf8FormatterGenerator</auto-generated>");
        sb.AppendLine("#pragma warning disable CS8600, CS8604, CS8762");
        sb.AppendLine();
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Buffers;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine();
        sb.AppendLine("namespace Omicron.Core.Text");
        sb.AppendLine("{");

        sb.AppendLine("    internal static partial class Utf8ValueFormatter");
        sb.AppendLine("    {");

        // Emit the TryFormatGenerated method
        sb.AppendLine("        private static partial Utf8FormatResult TryFormatGenerated<T>(");
        sb.AppendLine("            ref T value,");
        sb.AppendLine("            Span<byte> destination,");
        sb.AppendLine("            out int written,");
        sb.AppendLine("            ReadOnlySpan<byte> formatSpec,");
        sb.AppendLine("            IFormatProvider? provider)");
        sb.AppendLine("        {");

        // Emit branches for each registration
        foreach (var reg in registrations)
        {
            if (reg.HasError)
            {
                // Report diagnostic for invalid registration
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.InvalidRegistration,
                    Location.None,
                    reg.TypeName);
                context.ReportDiagnostic(diagnostic);
                continue;
            }

            sb.AppendLine($"            if (typeof(T) == typeof({reg.TypeName}))");
            sb.AppendLine("            {");

            // Null check for reference types
            if (!reg.IsValueType)
            {
                sb.AppendLine("                if (value is null)");
                sb.AppendLine("                {");
                sb.AppendLine("                    written = 0;");
                sb.AppendLine("                    return Utf8FormatResult.Success;");
                sb.AppendLine("                }");
            }

            // Cast and format
            sb.AppendLine($"                ref var typed = ref Unsafe.As<T, {reg.TypeName}>(ref value);");

            if (reg.HasIUtf8SpanFormattable)
            {
                // IUtf8SpanFormattable path
                sb.AppendLine();
                sb.AppendLine("                if (formatSpec.IsEmpty)");
                sb.AppendLine("                {");
                sb.AppendLine("                    if (typed.TryFormat(destination, out written, default, provider))");
                sb.AppendLine("                        return Utf8FormatResult.Success;");
                sb.AppendLine("                    return Utf8FormatResult.InsufficientSpace;");
                sb.AppendLine("                }");
                sb.AppendLine();
                sb.AppendLine("                // Non-empty ASCII format specifier: transcode inline");
                sb.AppendLine("                // Max 32 chars; longer specifiers are rejected as unsupported.");
                sb.AppendLine("                if (formatSpec.Length > 32)");
                sb.AppendLine("                {");
                sb.AppendLine("                    written = 0;");
                sb.AppendLine("                    return Utf8FormatResult.UnsupportedType;");
                sb.AppendLine("                }");
                sb.AppendLine("                Span<char> specChars = stackalloc char[32];");
                sb.AppendLine("                for (int i = 0; i < formatSpec.Length; i++)");
                sb.AppendLine("                {");
                sb.AppendLine("                    byte sb = formatSpec[i];");
                sb.AppendLine("                    if (sb > 127)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        written = 0;");
                sb.AppendLine("                        return Utf8FormatResult.UnsupportedType;");
                sb.AppendLine("                    }");
                sb.AppendLine("                    specChars[i] = (char)sb;");
                sb.AppendLine("                }");
                sb.AppendLine();
                sb.AppendLine("                if (typed.TryFormat(destination, out written, specChars[..formatSpec.Length], provider))");
                sb.AppendLine("                    return Utf8FormatResult.Success;");
                sb.AppendLine("                return Utf8FormatResult.InsufficientSpace;");
            }
            else
            {
                // ISpanFormattable path (slow bridge)
                sb.AppendLine();
                sb.AppendLine("                // Transcode format specifier once for reuse across retries");
                sb.AppendLine("                char[]? formatChars = null;");
                sb.AppendLine("                try");
                sb.AppendLine("                {");
                sb.AppendLine("                    if (!formatSpec.IsEmpty)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        formatChars = System.Buffers.ArrayPool<char>.Shared.Rent(formatSpec.Length);");
                sb.AppendLine("                        for (int i = 0; i < formatSpec.Length; i++)");
                sb.AppendLine("                        {");
                sb.AppendLine("                            byte sb = formatSpec[i];");
                sb.AppendLine("                            if (sb > 127)");
                sb.AppendLine("                            {");
                sb.AppendLine("                                written = 0;");
                sb.AppendLine("                                return Utf8FormatResult.UnsupportedType;");
                sb.AppendLine("                            }");
                sb.AppendLine("                            formatChars[i] = (char)sb;");
                sb.AppendLine("                        }");
                sb.AppendLine("                    }");
                sb.AppendLine();
                sb.AppendLine("                    int charCapacity = 256;");
                sb.AppendLine("                    const int maxCharCapacity = 1024 * 1024;");
                sb.AppendLine();
                sb.AppendLine("                    while (true)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        Span<char> chars = charCapacity <= 512");
                sb.AppendLine("                            ? stackalloc char[charCapacity]");
                sb.AppendLine("                            : new char[charCapacity];");
                sb.AppendLine();
                sb.AppendLine("                        var formatSpan = formatChars is null");
                sb.AppendLine("                            ? ReadOnlySpan<char>.Empty");
                sb.AppendLine("                            : formatChars.AsSpan(0, formatSpec.Length);");
                sb.AppendLine();
                sb.AppendLine("                        if (typed.TryFormat(chars, out var charsWritten, formatSpan, provider))");
                sb.AppendLine("                        {");
                sb.AppendLine("                            int maxBytes = Encoding.UTF8.GetMaxByteCount(charsWritten);");
                sb.AppendLine("                            if (maxBytes > destination.Length)");
                sb.AppendLine("                            {");
                sb.AppendLine("                                written = maxBytes;");
                sb.AppendLine("                                return Utf8FormatResult.InsufficientSpace;");
                sb.AppendLine("                            }");
                sb.AppendLine();
                sb.AppendLine("                            written = Encoding.UTF8.GetBytes(chars[..charsWritten], destination);");
                sb.AppendLine("                            return Utf8FormatResult.Success;");
                sb.AppendLine("                        }");
                sb.AppendLine();
                sb.AppendLine("                        if (charCapacity >= maxCharCapacity)");
                sb.AppendLine("                        {");
                sb.AppendLine("                            written = 0;");
                sb.AppendLine("                            return Utf8FormatResult.InsufficientSpace;");
                sb.AppendLine("                        }");
                sb.AppendLine();
                sb.AppendLine("                        charCapacity = Math.Min(charCapacity * 2, maxCharCapacity);");
                sb.AppendLine("                    }");
                sb.AppendLine("                }");
                sb.AppendLine("                finally");
                sb.AppendLine("                {");
                sb.AppendLine("                    if (formatChars is not null)");
                sb.AppendLine("                        System.Buffers.ArrayPool<char>.Shared.Return(formatChars);");
                sb.AppendLine("                }");
            }

            sb.AppendLine("            }");
        }

        // No matching type — return NoFormatter (fallback handles it)
        sb.AppendLine();
        sb.AppendLine("            written = 0;");
        sb.AppendLine("            return Utf8FormatResult.NoFormatter;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource("Utf8ValueFormatter.Generated.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    internal readonly struct GeneratorState
    {
        public readonly ImmutableArray<Registration> Registrations;
        public readonly bool ShouldEmit;

        public GeneratorState(List<Registration> registrations, bool shouldEmit)
        {
            Registrations = registrations.ToImmutableArray();
            ShouldEmit = shouldEmit;
        }
    }

    internal readonly struct Registration
    {
        public readonly string TypeName;
        public readonly bool IsValueType;
        public readonly bool HasIUtf8SpanFormattable;
        public readonly bool HasISpanFormattable;
        public readonly bool HasError;

        public Registration(string typeName, bool isValueType, bool hasIUtf8, bool hasISpan, bool hasError)
        {
            TypeName = typeName;
            IsValueType = isValueType;
            HasIUtf8SpanFormattable = hasIUtf8;
            HasISpanFormattable = hasISpan;
            HasError = hasError;
        }
    }
}
