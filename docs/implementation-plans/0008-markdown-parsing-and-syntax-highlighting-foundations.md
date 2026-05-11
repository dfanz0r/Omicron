# Implementation Plan 0008: Markdown Parsing and Syntax Highlighting Foundations

Status: Proposed
Target: Enable incremental markdown parsing, code block language detection, and syntax highlighting for streaming LLM output in the TUI.

## Purpose

Plan 7 and 7.1 built a plain-text transcript viewport. Plan 8 adds rich content rendering: markdown blocks, code fences with language tags, and syntax-highlighted code cells. This transforms the TUI from a plain text stream into a readable agent interface with structured output.

This plan is intentionally **rendering-pipeline only**. It does not add a full GUI or plugin panel system. It feeds styled cells into the `TerminalFrame` built in Plan 7.

## Primary RFCs

- RFC 0013 — Content Parsing, Markdown Rendering, and Syntax Highlighting
- RFC 0002 — UI Abstractions, Plugins, Commands, and Permissions (ContentBlock model)
- RFC 0004 — Transcript Viewport, Scrollback, Layout, and Input (rich content in blocks)
- RFC 0009 — Implementation Roadmap (Phase Set 2b)

## Reference Analysis

`docs/reports/0001-tui-renderer-codebase-analysis.md` — relevant findings:

- **Tree-sitter** is used by OpenTUI for syntax highlighting but is Zig-based and heavy. Plan 8 correctly starts with regex tokenizers as a lighter fallback-first strategy.
- **XenoAtom.Terminal.UI** uses cell-buffer + diff rendering with separate styling per cell — Plan 8's `SyntaxToken` → `TextStyle` → `RenderCell` pipeline fits this architecture.
- **No existing codebase** in the READ_ONLY set implements incremental markdown parsing for streaming output — Plan 8's incremental parser is novel in this space.

## Depends On

- Plan 7 (`Utf8TextStore`, `TerminalFrame`, `TextStyle`, `RenderCell`, `DifferentialRenderer`)
- Plan 7.1 (`TranscriptStore`, `TranscriptBlock`, `TranscriptLayoutCache`, `WrappedLineInfo`)
- Plan 5 (`ContentBlock` model — `MarkdownContentBlock`, `CodeContentBlock`, `PlainTextContentBlock`)
- Plan 8.5 (`TreeSitterClient`, `ParserManager`, `QueryManager` — for tree-sitter highlighting queries)

## Non-Goals

- No full GUI rich text control (Phase Set 5a)
- No WASM grammar loading
- No plugin-contributed language grammars
- No incremental parsing of non-markdown content (JSON, XML, etc.)
- No WYSIWYG markdown editing
- No inline diff rendering inside code blocks

## Guiding Principles

1. **Incremental, not batch.** Streaming LLM output is parsed chunk by chunk without full reparse.
2. **Safe commit points.** The parser knows where the last complete block ended and resumes from there.
3. **UTF-8 throughout.** The parser operates on `ReadOnlySpan<byte>` or `ReadOnlyMemory<byte>`, not strings.
4. **Highlighting is cached per block.** Only changed blocks are re-tokenized.
5. **Fallback first.** Start with regex-based tokenizers for common languages; Tree-sitter is a future optimization.

---

## Proposed Layout

```text
Omicron.Core/Parsing/
  IncrementalMarkdownParser.cs
  MarkdownAst.cs
  MarkdownDelta.cs
  MarkdownSafeCommitPoint.cs
  LanguageDetector.cs

Omicron.Core/Parsing/Highlighting/
  ISyntaxHighlighter.cs
  RegexSyntaxHighlighter.cs
  SyntaxTheme.cs
  SyntaxColor.cs
  SyntaxToken.cs
  HighlightCache.cs
  TextMateScopeMapper.cs

Omicron.Core/Parsing/Highlighting/Languages/
  CSharpRegexTokenizer.cs
  PythonRegexTokenizer.cs
  JavaScriptRegexTokenizer.cs
  TypeScriptRegexTokenizer.cs
  RustRegexTokenizer.cs
  GoRegexTokenizer.cs
  JavaRegexTokenizer.cs
  YamlRegexTokenizer.cs
  JsonRegexTokenizer.cs
  TomlRegexTokenizer.cs
  SqlRegexTokenizer.cs
  ShellRegexTokenizer.cs
  MarkdownRegexTokenizer.cs
  HtmlRegexTokenizer.cs
  CssRegexTokenizer.cs

Omicron.Core/Rendering/Transcript/
  RichTranscriptLayoutCache.cs   // extends Plan 7.1 layout cache with styles
```

---

## Phase A: Incremental Markdown Parser

### Goals
Parse streaming UTF-8 markdown into a block-level AST. Support CommonMark + GFM extensions. Resume from the last safe commit point on each chunk.

### Deliverables

#### A1. `MarkdownAst` records
```csharp
namespace Omicron.Core.Parsing;

public abstract record MdBlock(long ByteOffset, int ByteLength);

public sealed record MdParagraph(long ByteOffset, int ByteLength, IReadOnlyList<MdInline> Inlines) : MdBlock(ByteOffset, ByteLength);
public sealed record MdHeading(long ByteOffset, int ByteLength, int Level, IReadOnlyList<MdInline> Inlines) : MdBlock(ByteOffset, ByteLength);
public sealed record MdCodeBlock(long ByteOffset, int ByteLength, string? Language, ReadOnlyMemory<byte> Utf8Code) : MdBlock(ByteOffset, ByteLength);
public sealed record MdBlockQuote(long ByteOffset, int ByteLength, IReadOnlyList<MdBlock> Children) : MdBlock(ByteOffset, ByteLength);
public sealed record MdListBlock(long ByteOffset, int ByteLength, bool IsOrdered, IReadOnlyList<MdListItem> Items) : MdBlock(ByteOffset, ByteLength);
public sealed record MdListItem(long ByteOffset, int ByteLength, IReadOnlyList<MdBlock> Children);
public sealed record MdTable(long ByteOffset, int ByteLength, IReadOnlyList<MdTableRow> Rows) : MdBlock(ByteOffset, ByteLength);
public sealed record MdTableRow(IReadOnlyList<MdTableCell> Cells);
public sealed record MdTableCell(IReadOnlyList<MdInline> Inlines);
public sealed record MdThematicBreak(long ByteOffset, int ByteLength) : MdBlock(ByteOffset, ByteLength);

public abstract record MdInline(long ByteOffset, int ByteLength);
public sealed record MdText(long ByteOffset, int ByteLength, ReadOnlyMemory<byte> Utf8) : MdInline(ByteOffset, ByteLength);
public sealed record MdStrong(long ByteOffset, int ByteLength, IReadOnlyList<MdInline> Children) : MdInline(ByteOffset, ByteLength);
public sealed record MdEmphasis(long ByteOffset, int ByteLength, IReadOnlyList<MdInline> Children) : MdInline(ByteOffset, ByteLength);
public sealed record MdCode(long ByteOffset, int ByteLength, ReadOnlyMemory<byte> Utf8) : MdInline(ByteOffset, ByteLength);
public sealed record MdLink(long ByteOffset, int ByteLength, ReadOnlyMemory<byte> UrlUtf8, IReadOnlyList<MdInline> Label) : MdInline(ByteOffset, ByteLength);
public sealed record MdStrikethrough(long ByteOffset, int ByteLength, IReadOnlyList<MdInline> Children) : MdInline(ByteOffset, ByteLength);
```

- All byte offsets are relative to the start of the markdown text buffer.
- `ReadOnlyMemory<byte>` preserves UTF-8 without string allocation.

#### A2. `MarkdownSafeCommitPoint`
```csharp
namespace Omicron.Core.Parsing;

public readonly record struct MarkdownSafeCommitPoint(long ByteOffset, int BlockCount);
```

A commit point means: "all blocks before `BlockCount` are complete and will not change with future input." The last block may still be open (e.g., an ongoing paragraph or unclosed code fence).

#### A3. `MarkdownDelta`
```csharp
namespace Omicron.Core.Parsing;

public sealed record MarkdownDelta(
    IReadOnlyList<MdBlock> NewOrModifiedBlocks,
    int ReplacedBlockCount,    // how many previous blocks from the tail were invalidated
    MarkdownSafeCommitPoint NewCommitPoint);
```

#### A4. `IncrementalMarkdownParser`
```csharp
namespace Omicron.Core.Parsing;

public sealed class IncrementalMarkdownParser
{
    /// <summary>Append new UTF-8 bytes and return the delta.</summary>
    public MarkdownDelta Append(ReadOnlySpan<byte> utf8);

    /// <summary>All blocks parsed so far.</summary>
    public IReadOnlyList<MdBlock> AllBlocks { get; }

    /// <summary>Last safe commit point.</summary>
    public MarkdownSafeCommitPoint CommitPoint { get; }

    public void Clear();
}
```

Algorithm:
1. Append bytes to an internal `Utf8TextStore` or `ArrayBufferWriter<byte>`.
2. If the last block is a fenced code block with an unclosed fence, buffer until ` ``` ` is found.
3. If the last block is a paragraph or list item, reparse from the last commit point to end.
4. Otherwise, parse only new bytes starting from the last commit point.
5. Compare new tail blocks to old tail blocks; emit delta.
6. Update commit point to the last complete block boundary.

**MVP simplification:** Instead of a full incremental state machine, implement a "resume from last safe line" strategy:
- Track the last byte offset where a block definitively ended (double newline after paragraph, close of list, close of code fence, etc.).
- On append, scan backward from the end to find the last safe boundary.
- Reparse only from that boundary.
- For most streaming LLM output, this means only the last paragraph is reparsed.

Parsing strategy:
- Use a fast line-oriented scanner.
- Detect block types by line prefixes:
  - `#` → heading
  - ` ``` ` → fenced code block
  - `>` → blockquote
  - `- `, `* `, `+ `, `1. ` → list
  - `---`, `***`, `___` → thematic break
  - `|` → table row (GFM)
  - otherwise → paragraph or continuation

Inline parsing:
- Scan for `` ` ``, `**`, `*`, `~~`, `[` inside paragraphs.
- Build inline spans with byte ranges.

#### A5. `LanguageDetector`
```csharp
namespace Omicron.Core.Parsing;

public static class LanguageDetector
{
    public static string? FromFenceTag(ReadOnlySpan<byte> tag);
    public static string? FromFileExtension(string extension);
    public static string? FromShebang(ReadOnlySpan<byte> firstLine);
}
```

Fence tag mapping: `csharp`, `cs` → `csharp`; `python`, `py` → `python`; `javascript`, `js` → `javascript`; `typescript`, `ts` → `typescript`; `rust`, `rs` → `rust`; `go` → `go`; `java` → `java`; `yaml`, `yml` → `yaml`; `json` → `json`; `toml` → `toml`; `sql` → `sql`; `bash`, `sh`, `zsh`, `powershell`, `ps1` → `shell`; `markdown`, `md` → `markdown`; `html` → `html`; `css` → `css`.

### Tests
- `Parser_Empty_ReturnsNoBlocks`
- `Parser_Paragraph_SingleBlock`
- `Parser_Heading_DetectsLevel`
- `Parser_FencedCodeBlock_DetectsLanguage`
- `Parser_Incremental_AppendToParagraph_OnlyReparsesLastBlock`
- `Parser_Incremental_CompleteCodeBlock_EmitsClosedBlock`
- `Parser_Incremental_SafeCommitPoint_AdvancesOnCompleteBlock`
- `Parser_BlockQuote_NestsParagraphs`
- `Parser_List_DetectsOrderedAndUnordered`
- `Parser_Table_GfmTable`
- `Parser_Inline_StrongEmphasisCode`
- `Parser_Inline_Link`
- `LanguageDetector_FromFenceTag_MapsCsharp`
- `LanguageDetector_FromFileExtension_MapsPy`
- `LanguageDetector_FromShebang_MapsPython`

### Acceptance Criteria
- `IncrementalMarkdownParser` handles CommonMark blocks: paragraph, heading, fenced code, blockquote, list, thematic break.
- GFM extensions: table, strikethrough, autolinks.
- Incremental append only reparses the last incomplete block in common cases.
- Safe commit point advances after every complete block.
- Language detection maps common fence tags and file extensions.

---

## Phase B: Regex Fallback Highlighting Engine (MVP)

### Goals
Tokenize code blocks into styled spans using regex-based tokenizers. Support a TextMate-style theme system. Cache highlights per block.

**Note:** This is the MVP fallback path. Tree-sitter (Phase F) is the intended primary long-term path for accurate, multi-language highlighting. Regex tokenizers are deliberately simple and will be superseded by tree-sitter for languages where grammars are available.

### Deliverables

#### B1. `SyntaxToken`
```csharp
namespace Omicron.Core.Parsing.Highlighting;

public readonly record struct SyntaxToken(
    int ByteOffset,
    int ByteLength,
    string Scope);   // e.g., "keyword", "string", "comment", "entity.name.type"
```

#### B2. `ISyntaxHighlighter`
```csharp
namespace Omicron.Core.Parsing.Highlighting;

public interface ISyntaxHighlighter
{
    string Language { get; }
    IReadOnlyList<SyntaxToken> Tokenize(ReadOnlySpan<byte> utf8Code);
}
```

#### B3. `RegexSyntaxHighlighter`
A base class for regex-based tokenizers:
```csharp
namespace Omicron.Core.Parsing.Highlighting;

public abstract class RegexSyntaxHighlighter : ISyntaxHighlighter
{
    public abstract string Language { get; }

    protected abstract IReadOnlyList<(Regex Pattern, string Scope)> Patterns { get; }

    public virtual IReadOnlyList<SyntaxToken> Tokenize(ReadOnlySpan<byte> utf8Code)
    {
        var text = Encoding.UTF8.GetString(utf8Code); // MVP: decode to string for regex
        var tokens = new List<SyntaxToken>();
        // Run patterns in priority order; resolve overlaps by earliest start, then longest match, then priority
        // ...
        return tokens;
    }
}
```

**MVP note:** Decoding to `string` is acceptable for MVP because code blocks in agent output are typically <500 lines. The hot path is the transcript store, not the tokenizer. Document this as a known allocation and plan to move to `Regex` over `ReadOnlySpan<char>` or compiled source generators later.

Priority rules for overlapping matches:
1. Earliest start byte wins.
2. If same start, longest match wins.
3. If same start and length, earlier pattern in the list wins.

#### B4. Per-language regex tokenizers
Each language gets a small class inheriting `RegexSyntaxHighlighter`:

Common patterns to implement per language:
- **Comments:** line comments (`//`, `#`, `--`, `;`), block comments (`/* */`, `(* *)`, `{- -}`)
- **Strings:** double-quoted, single-quoted, interpolated, verbatim, heredoc
- **Numbers:** integers, floats, hex, binary, scientific notation
- **Keywords:** language-reserved keywords
- **Types:** built-in types, common type naming conventions (PascalCase in C#/Java)
- **Operators:** arithmetic, comparison, logical, assignment
- **Punctuation:** braces, parentheses, brackets, commas, dots, colons
- **Attributes/annotations:** `@` in C#, `#[` in Rust, decorators in Python

MVP language list (Tier 1 per RFC 0013):

**Full regex tokenizers (12 languages):**
| Language | File |
|----------|------|
| C# | `CSharpRegexTokenizer.cs` |
| Python | `PythonRegexTokenizer.cs` |
| JavaScript | `JavaScriptRegexTokenizer.cs` |
| TypeScript | `TypeScriptRegexTokenizer.cs` |
| Rust | `RustRegexTokenizer.cs` |
| Go | `GoRegexTokenizer.cs` |
| Java | `JavaRegexTokenizer.cs` |
| YAML | `YamlRegexTokenizer.cs` |
| JSON | `JsonRegexTokenizer.cs` |
| TOML | `TomlRegexTokenizer.cs` |
| SQL | `SqlRegexTokenizer.cs` |
| Shell | `ShellRegexTokenizer.cs` |

**Plain-text fallback (3 languages — marked for future tokenizer work):**
| Language | Behavior |
|----------|----------|
| Markdown | Render as plain text with structural hints (headers bolded, code fences tagged). No regex tokenization needed because Markdown is the container format. |
| HTML | Plain text with simple tag colorization only if trivial; otherwise default text style. |
| CSS | Plain text fallback. Full CSS tokenizer deferred. |

Rationale: HTML and CSS have complex nested grammars where naive regex tokenizers produce misleading results. Markdown is the host format, not a code language to highlight. These three can be upgraded to full tokenizers in a future plan without affecting the architecture.

#### B5. `SyntaxTheme`
```csharp
namespace Omicron.Core.Parsing.Highlighting;

public sealed record SyntaxTheme(
    string Name,
    IReadOnlyDictionary<string, SyntaxColor> ScopeColors,
    SyntaxColor DefaultText,
    SyntaxColor Background,
    SyntaxColor LineNumber);

public readonly record struct SyntaxColor(
    byte R, byte G, byte B,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false);
```

Scope hierarchy resolution:
- If exact scope exists (`keyword.control`), use it.
- Else try parent scope (`keyword`).
- Else use `DefaultText`.

#### B6. `TextMateScopeMapper`
```csharp
namespace Omicron.Core.Parsing.Highlighting;

public static class TextMateScopeMapper
{
    public static SyntaxColor Resolve(SyntaxTheme theme, string scope);
}
```

#### B7. `HighlightCache`
```csharp
namespace Omicron.Core.Parsing.Highlighting;

public sealed class HighlightCache
{
    public IReadOnlyList<SyntaxToken>? Get(ReadOnlyMemory<byte> utf8Code, string language, string themeName);
    public void Set(ReadOnlyMemory<byte> utf8Code, string language, string themeName, IReadOnlyList<SyntaxToken> tokens);
}
```

- Key: `(hash of utf8 bytes, language, themeName)`
- Use a small LRU cache (e.g., 256 entries).
- Hash can be `XxHash32` or `System.HashCode` over the bytes.

#### B8. Bundled themes
Provide two built-in JSON theme files (loaded as embedded resources):
- `themes/one-dark.json`
- `themes/one-light.json`

Example `one-dark.json`:
```json
{
  "name": "One Dark",
  "defaultText": { "r": 171, "g": 178, "b": 191 },
  "background": { "r": 40, "g": 44, "b": 52 },
  "lineNumber": { "r": 92, "g": 99, "b": 112 },
  "scopeColors": {
    "comment": { "r": 92, "g": 99, "b": 112, "italic": true },
    "keyword": { "r": 198, "g": 120, "b": 221 },
    "string": { "r": 152, "g": 195, "b": 121 },
    "constant.numeric": { "r": 209, "g": 154, "b": 102 },
    "entity.name": { "r": 224, "g": 108, "b": 117 },
    "variable": { "r": 171, "g": 178, "b": 191 }
  }
}
```

### Tests
- `Tokenizer_CSharp_Keywords`
- `Tokenizer_CSharp_Strings`
- `Tokenizer_CSharp_Comments`
- `Tokenizer_Python_FStrings`
- `Tokenizer_Rust_Lifetimes`
- `Tokenizer_Json_Structures`
- `Tokenizer_Sql_Keywords`
- `Theme_Resolve_ExactScope`
- `Theme_Resolve_ParentScope`
- `Theme_Resolve_Fallback`
- `Cache_Hit_ReturnsTokens`
- `Cache_Miss_ComputesAndStores`
- `Cache_ThemeChange_Invalidates`

### Acceptance Criteria
- All 15 Tier-1 languages have regex tokenizers with tests.
- `SyntaxTheme` resolves scopes by exact → parent → default fallback.
- `HighlightCache` avoids re-tokenizing unchanged code blocks.
- Tokenization of a 100-line code block completes in <10 ms.

---

## Phase C: TUI Integration

### Goals
Wire the markdown parser and syntax highlighter into the transcript viewport so `AssistantMessageBlock` content renders with styles.

### Deliverables

#### C1. `RichTranscriptLayoutCache`
Extend Plan 7.1's `TranscriptLayoutCache` to produce styled wrapped lines.

```csharp
namespace Omicron.Core.Rendering.Transcript;

public readonly record struct StyledWrappedLineInfo(
    BlockId BlockId,
    long ByteStart,
    int ByteLength,
    int CellStart,
    int CellWidth,
    bool IsContinuation,
    int LogicalLineIndex,
    IReadOnlyList<StyleRun> StyleRuns);

public readonly record struct StyleRun(int CellStart, int CellWidth, TextStyle Style);
```

`StyleRuns` annotate segments of the line with foreground/background colors and attributes.

#### C2. Block-to-frame rendering pipeline
For each visible `WrappedLineInfo`:
1. Determine the block type.
2. If the block is a `MdCodeBlock`:
   - Use `LanguageDetector.FromFenceTag` to get the language.
   - Tokenize the code bytes via `ISyntaxHighlighter`.
   - Map tokens to `TextStyle` via `SyntaxTheme`.
   - Render each token into the frame with the correct color.
3. If the block is a `MdHeading`:
   - Render with bold + brighter foreground.
4. If the block is `MdParagraph` with inline spans:
   - Render `MdStrong` as bold.
   - Render `MdEmphasis` as italic.
   - Render `MdCode` inline with a subtle background color.
   - Render `MdStrikethrough` with dim color.
5. If plain text (no markdown detected):
   - Render with default style.

#### C3. Code block decorations
Render a thin border or header row above code blocks:
```text
┌─ csharp ──────────────────────┐
│ public class Foo {            │
│     public int Bar;           │
│ }                             │
└───────────────────────────────┘
```

MVP: use box-drawing characters (`─`, `│`, `┌`, `┐`, `└`, `┘`) with a muted color. If terminal width is tight, skip the left/right borders and just render the language tag on the first line.

#### C4. Markdown in streaming assistant output
Integration with `AgentSession` streaming:
- `AssistantTextDeltaEvent` appends raw UTF-8 to `TranscriptStore`.
- `TranscriptViewportWidget` calls `IncrementalMarkdownParser.Append(delta)`.
- The parser returns `MarkdownDelta`.
- `RichTranscriptLayoutCache` invalidates replaced blocks.
- Render loop reads styled lines and draws them.

Performance considerations:
- Do not reparse the entire transcript on every 50-byte delta.
- Only reparse from the last safe commit point.
- Only retokenize code blocks whose content changed.
- Reuse `HighlightCache`.

### Tests
- `RichLayout_PlainText_DefaultStyle`
- `RichLayout_Heading_BoldBright`
- `RichLayout_CodeBlock_TokenizedColors`
- `RichLayout_InlineCode_SubtleBackground`
- `RichLayout_Strong_Bold`
- `RichLayout_Emphasis_Italic`
- `RichLayout_MixedInlines_CorrectStyles`
- `RichLayout_CodeBlockHeader_ShowsLanguage`
- `RichLayout_StreamingDelta_OnlyReparsesTail`

### Acceptance Criteria
- Assistant output containing markdown is rendered with block-level styles (headings, code blocks, quotes).
- Code blocks inside markdown are syntax-highlighted for Tier-1 languages.
- Inline styles (bold, italic, inline code) are visible in paragraphs.
- Streaming performance: 20 chunks/sec with highlighting does not drop frames.
- Theme change triggers cache invalidation and re-render.

---

## Performance Targets

| Scenario | Target |
|----------|--------|
| Incremental parse 1 KB chunk | <2 ms |
| Tokenize 100-line C# block | <10 ms |
| Highlight cache hit | <0.1 ms |
| Render 60 visible rows with styles | <8 ms |
| Theme change + full re-render | <50 ms |

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Regex tokenizers are inaccurate for complex languages | Document as MVP fallback; Tree-sitter is the future primary path. |
| Incremental parser bugs produce flickering blocks | Extensive delta tests; safe commit point guards. |
| Decoding code blocks to string for regex is allocation-heavy | Limit to blocks <10k lines; cache aggressively. |
| Markdown parser is slow on large pasted content | Reparse only from safe commit; large pastes are rare in streaming. |
| Theme file loading fails | Ship embedded defaults; fail closed to built-in dark theme. |
| Tree-sitter worker adds process overhead | Benchmark against in-process WASM; worker is optional for MVP. |
| Missing Node.js on target machine | Document Node.js as runtime dep; ship standalone worker as fallback. |

---

## Definition of Done

- `IncrementalMarkdownParser` parses CommonMark + GFM blocks incrementally with safe commit points.
- `LanguageDetector` maps fence tags, extensions, and shebangs.
- Regex-based syntax highlighters exist for all 15 Tier-1 languages with tests.
- `SyntaxTheme` supports TextMate-style scope resolution with dark and light bundled themes.
- `HighlightCache` prevents re-tokenization of unchanged blocks.
- Transcript viewport renders markdown blocks with headings, code blocks, quotes, lists, and inline styles.
- Code blocks render with syntax-highlighted tokens and a language header.
- Streaming deltas only invalidate and reparse affected tail blocks.
- Tree-sitter highlighting is provided by Plan 8.5.
- All tests pass with 0 warnings.
