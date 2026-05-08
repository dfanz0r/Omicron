# RFC 0013: Content Parsing, Markdown Rendering, and Syntax Highlighting

Status: **Planned**

## Purpose

Define Omicron's architecture for parsing and rendering rich content in transcripts and panels. This covers:

- Incremental markdown parsing for streaming LLM output
- Syntax highlighting for code blocks in both TUI and GUI
- Theme system for syntax colors
- Integration with the text store, block model, and edit harness

Without a deliberate architecture, Omicron could accumulate ad hoc regex-based parsing, inconsistent highlighting, and poor streaming performance.

## Goals

- Render markdown from streaming LLM output incrementally (not batch-only)
- Highlight code blocks with language-aware syntax coloring
- Support enough languages to cover common agent output
- Keep rendering fast enough for 20–100 streaming chunks/second
- Share parse/theme data between TUI and GUI frontends
- Produce structured AST data that the edit harness can consume for context curation
- Allow plugins to register custom language grammars or highlighters

## Non-Goals

- Full IDE-grade language server features (that is LSP territory)
- WYSIWYG markdown editing
- Supporting every language ever created in the initial implementation
- Replacing the terminal emulator's VT/ANSI parser

## Architecture Overview

```
Streaming UTF-8 bytes
  ↓
Markdown parser (incremental, CommonMark + GFM)
  ↓
Markdown AST (blocks + inline spans)
  ↓
Content block model (TextBlock, CodeBlock, ...)
  ↓
Syntax highlighter (language detection → tokenization → color mapping)
  ↓
Styled cell / rich text output
  ↓
TUI frame buffer or GUI rich text control
```

## Markdown Parsing

### Requirements

- CommonMark-compliant base
- GFM extensions: tables, task lists, strikethrough, autolinks
- Fenced code blocks with language labels
- Streaming/incremental: new input should not require full reparse
- Output: block-level AST with inline span annotations

### Candidate Parsers

For .NET:

```text
Markdig (C#, CommonMark + GFM + extensions, very fast)
  - Already used in .NET ecosystems
  - Extensible via pipeline
  - May need incremental parse wrapper

Pulldown-cmark (Rust)
  - Used by Zed for markdown rendering
  - Fast, streaming-friendly event-based parser
  - If going through Rust FFI, this is a strong candidate
```

### Incremental Parse Strategy

Full re-parsing on every streaming chunk is wasteful. Recommended approach:

```csharp
public sealed class IncrementalMarkdownParser
{
    private readonly StringBuilder _buffer = new();
    private MarkdownDocument? _ast;

    /// <summary>
    /// Append new UTF-8 bytes and return the updated AST delta.
    /// Returns only the blocks that changed or were added.
    /// </summary>
    public MarkdownDelta Append(ReadOnlySpan<byte> utf8)
    {
        // 1. Append bytes to buffer
        // 2. If the last block was a "lazy continuation" (paragraph, list item),
        //    only reparse the last N bytes.
        // 3. If a fenced code block is open, buffer until closing fence.
        // 4. Otherwise, reparse from last safe commit point.
        // 5. Return delta: new/modified blocks with byte ranges.
    }
}
```

Key insight: streaming LLM output mostly appends to the last paragraph or code block. A parser that can resume from the last complete block boundary avoids O(n) reparse on every chunk.

The parser should maintain a **safe commit point** — the last byte position where a complete block ended. New input before that point is stable and does not need re-parsing.

```text
[paragraph text...]
[paragraph more...]← safe commit point (block complete)
[ongoing paragraph...  ← no commit yet, block is still open
```

### Markdown AST

The parser should produce a flat-ish block-and-inline AST:

```csharp
public abstract record MdBlock(long ByteOffset, int ByteLength);

public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdCodeBlock(string? Language, ReadOnlyMemory<byte> Utf8Code) : MdBlock;
public sealed record MdListBlock(bool IsOrdered, IReadOnlyList<MdListItem> Items) : MdBlock;
public sealed record MdTable(IReadOnlyList<MdTableRow> Rows) : MdBlock;
public sealed record MdBlockQuote(IReadOnlyList<MdBlock> Children) : MdBlock;
public sealed record MdThematicBreak : MdBlock;

public abstract record MdInline(long ByteOffset, int ByteLength);
public sealed record MdText(ReadOnlyMemory<byte> Utf8) : MdInline;
public sealed record MdStrong(IReadOnlyList<MdInline> Children) : MdInline;
public sealed record MdEmphasis(IReadOnlyList<MdInline> Children) : MdInline;
public sealed record MdCode(ReadOnlyMemory<byte> Utf8) : MdInline;
public sealed record MdLink(Uri Url, IReadOnlyList<MdInline> Label) : MdInline;
public sealed record MdImage(Uri Url, string Alt) : MdInline;
```

## Syntax Highlighting

### Requirements

- Language detection from code block language tags (```csharp, ```python, etc.)
- Tokenization into named scopes (keyword, string, comment, type, etc.)
- Mapping scopes to styled colors via a theme
- Support for at least 12–20 common languages in the initial set
- Fast enough for highlighting moderate code blocks (>100 lines) on every frame during streaming
- Optional: plugin-registered grammars for less common languages

### Architecture

```text
Code block text (UTF-8 bytes) + language tag
  ↓
Grammar/scope lookup
  ↓
Tokenizer (produces scope + range pairs)
  ↓
Scope-to-color mapping via SyntaxTheme
  ↓
Styled ranges (range + TextStyle)
  ↓
Rendered cells (TUI) or rich text spans (GUI)
```

### Tokenizer Options

**Option A: Tree-sitter (recommended primary path)**

- Mature, fast, supports 100+ languages
- Used by Zed, Neovim, many editors
- Produces concrete syntax trees with named captures
- Can be used for both highlighting and AST/context extraction for the edit harness
- Supports WASM grammars for dynamic loading
- .NET binding exists (tree_sitter_dotnet) or can go through Rust FFI

```text
Pros:
  - single engine for highlighting + AST + edit context
  - accurate highlighting
  - language ecosystem is huge
  - WASM grammars enable plugin-contributed languages

Cons:
  - setup cost for each grammar
  - overkill if only highlighting is needed
  - Rust/FFI path likely best for performance
```

**Option B: Syntect (Rust, Sublime Text syntaxes)**

- Uses TextMate `.sublime-syntax` files
- Very fast
- Used by `bat`, `delta`, and other Rust CLI tools
- Good highlighting quality
- Rust-only; requires FFI for C# access

```text
Pros:
  - easy to add languages (drop a .sublime-syntax file)
  - fast tokenization
  - well-proven in CLI tools

Cons:
  - less accurate than Tree-sitter for complex languages
  - no AST for edit harness
  - Rust-only
```

**Option C: Roslyn (C#)**

- For C# specifically, Roslyn provides perfect syntax highlighting
- If Omicron primarily targets C# projects, this is natural
- Does not help with other languages (Python, Rust, JS, etc.)

**Option D: Regex-based line tokenizer (fallback/simple)**

- Simplest: use language-appropriate regex patterns
- Good enough for basic highlighting of common patterns
- Limited accuracy for complex syntax
- Could serve as a no-dependency fallback

### Recommended Hybrid Approach

```text
Primary:   Tree-sitter via Rust FFI for all languages where grammar available
Fallback:  Regex-based line tokenizer for languages without Tree-sitter grammar
Special:   Roslyn for C# files when Roslyn is already loaded for other reasons
Plugin:    WASM-based Tree-sitter grammars for plugin-contributed languages
```

### Supported Language Tiers

Tier 1 (ship with core, 10–15 languages):

```text
C#, Python, JavaScript, TypeScript, Rust, Go, Java
YAML, JSON, TOML
Markdown, HTML, CSS, SQL
Shell (bash, PowerShell, zsh)
```

Tier 2 (common but can be later):

```text
C, C++, Ruby, PHP, Swift, Kotlin
Dockerfile, Lua, Haskell, Scala
Protocol Buffers, GraphQL
```

Tier 3 (plugin-registered or community):

```text
Everything else through WASM Tree-sitter grammars
```

## Theme System

Syntax highlighting needs a theme that maps semantic token scopes to colors.

### Scope Model

Use a TextMate-style scope hierarchy for compatibility:

```text
comment, comment.line, comment.block
constant, constant.numeric, constant.string
keyword, keyword.control, keyword.operator
string, string.quoted, string.escaped
entity, entity.name, entity.name.type
variable, variable.parameter
markup, markup.heading, markup.list
```

### Theme Definition

```csharp
public sealed record SyntaxTheme(
    IReadOnlyDictionary<string, SyntaxColor> ScopeColors,
    SyntaxColor DefaultText,
    SyntaxColor Background,
    SyntaxColor LineNumber,
    SyntaxColor Selection);

public sealed record SyntaxColor(
    byte R, byte G, byte B,
    byte? Alpha = null,
    TextDecoration Decoration = TextDecoration.None,
    bool Bold = false,
    bool Italic = false);
```

Themes should be serializable and loadable from files:

```json
{
  "name": "One Dark",
  "scopeColors": {
    "comment": { "r": 92, "g": 99, "b": 112 },
    "keyword": { "r": 198, "g": 120, "b": 221 },
    "string": { "r": 152, "g": 195, "b": 121 },
    "constant.numeric": { "r": 209, "g": 154, "b": 102 },
    "entity.name": { "r": 224, "g": 108, "b": 117 },
    "variable": { "r": 171, "g": 178, "b": 191 }
  }
}
```

### Theme Integration

- TUI maps syntax colors to 16-color, 256-color, or true-color depending on terminal capabilities
- GUI maps syntax colors directly to its rich text model
- Themes can be bundled with Omicron, loaded from files, or contributed by plugins
- A dark and a light default theme should ship

## Integration with Content Block Model

The markdown AST feeds into the content block model (RFC 0002):

```csharp
// From the incremental parser output:
MdCodeBlock("python", utf8CodeBytes)
  ↓
CodeBlock("python", utf8CodeBytes)   // Omicron content block
  ↓
Syntax highlighter produces:
IReadOnlyList<(Range<long> ByteRange, TextStyle Style)>
```

The styled ranges can be rendered as:

- TUI: colored terminal cells in the frame buffer
- GUI: rich text spans in a control

## Integration with Transcript Pipeline

The parsing pipeline in context:

```text
Model stream (UTF-8 chunks)
  ↓
Append to Utf8TextStore (RFC 0003)
  ↓
Incremental markdown parser
  ↓
Update block model (RFC 0004)
  ↓
If code block: request syntax highlighting
  ↓
Layout cache updated with styled line info
  ↓
Viewport renders visible rows with syntax colors
```

Streaming optimization:

- Only re-highlight code blocks whose content changed
- Cache highlighted result by (block ID, language, theme version)
- Invalidate cache when theme changes or block content is edited

## Integration with Edit Harness (RFC 0012)

The markdown AST and Tree-sitter AST serve the edit harness:

- **Code block awareness**: The edit harness can recognize which parts of a conversation contain code vs. natural language
- **AST context curation**: Tree-sitter captures can identify function definitions, class declarations, imports, etc. to include in model context
- **Structural edits**: Symbol-level anchors using Tree-sitter node positions
- **Language detection**: Code blocks with explicit language tags feed into file-edit language expectations

## Reference Implementations

### Zed (Rust)

- `crates/markdown/` — Incremental markdown parser based on pulldown-cmark, produces `MarkdownEvent` stream with byte-range annotations. Supports GFM tables, footnotes, task lists, heading attributes, HTML blocks, mermaid.
- `crates/language/` — Language system with Tree-sitter grammars, `SyntaxMap` for buffer-range-to-highlight mapping, `LanguageRegistry` for grammar lifecycle.
- `crates/language_core/src/grammar.rs` — `Grammar` struct with Tree-sitter `Language`, highlight queries, bracket configs, injection configs.
- `crates/language_core/src/highlight_map.rs` — `HighlightMap` mapping capture IDs to highlight IDs.
- `crates/grammars/` — Built-in grammars (RustEmbed), one `config.toml` per language.
- `crates/syntax_theme/` — `SyntaxTheme` with TextMate-style scope colors.

### ConsoleEx (C#)

- `SharpConsoleUI/Parsing/MarkupParser.cs` — Spectre-compatible markup parser that converts `[color]text[/]` tags directly to cell sequences. Good reference for TUI markup rendering.

### OpenCode (TypeScript)

- `packages/opencode/parsers-config.ts` — Tree-sitter WASM grammar configuration for Python, Rust, Go, C++, Java, TypeScript, JSX, CSS, JSON, YAML, TOML, SQL. Loads `.wasm` grammar files + `.scm` queries from URLs.

### codex (Rust)

- Various prompt templates showing how model output is structured with edit formats. Not a syntax-highlighting reference but shows the system-prompt integration patterns.

## Performance Considerations

- Markdown parsing of streaming chunks: benchmark with 100-byte through 10KB chunks
- Syntax highlighting of a 100-line code block: target <5ms
- Cache highlighted blocks by (block hash, theme version)
- Avoid re-highlighting blocks that haven't changed
- Tree-sitter parsing should happen off the UI thread via task queue
- Theme changes should invalidate all cached highlight data without re-parsing

## Package Boundaries

```text
Omicron.Content
  incremental markdown parser
  markdown AST types
  content block model extensions
  language detection

Omicron.Content.SyntaxHighlighting
  Tree-sitter integration (via FFI if Rust)
  scope/theme model
  highlight cache
  fallback regex tokenizers
  theme loading

Omicron.Content.SyntaxThemes
  bundled themes (dark + light default)
  theme file format and schema
```

`Omicron.Content` and `Omicron.Content.SyntaxHighlighting` depend on `Omicron.Text` (for UTF-8 byte access). The TUI and GUI frontends depend on `Omicron.Content` and `Omicron.Content.SyntaxHighlighting` to render rich content.

## Design Decisions

1. **Markdown parsing is incremental.** Stream input is parsed from the last safe commit point, not reparsed from the beginning.
2. **Tree-sitter is the primary highlight engine.** It offers the best combination of accuracy, speed, language coverage, and AST support for the edit harness.
3. **Tree-sitter goes through Rust FFI.** The C API is stable; a native library wrapping Tree-sitter can be called from C# via csbindgen.
4. **A fallback regex tokenizer exists.** For languages without Tree-sitter grammars and for environments where native dependencies aren't available.
5. **Syntax themes use TextMate scope names.** This maximizes compatibility with existing editor themes.
6. **Highlighting is cached per block.** Only changed blocks are re-highlighted.
7. **Code block language is detected from markdown fences.** No auto-detection in the initial version; explicit ` ```language ` tags only.
8. **Plugins can contribute WASM Tree-sitter grammars.** Following OpenCode's pattern of loading `.wasm` grammar files from URLs.
