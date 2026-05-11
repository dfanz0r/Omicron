# Report 0002: Tree-Sitter System Analysis for Omicron

**Date:** 2026-05-10
**Purpose:** Inform the design of a single tree-sitter system serving both syntax highlighting and semantic analysis (agent code query/understanding) in Omicron.
**Sources:** READ_ONLY/ folder — tree-sitter integrations in Dirac, KiloCode, OpenTUI, Zed, and oh-my-pi

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Dirac — Semantic Code Understanding System](#2-dirac--semantic-code-understanding-system)
3. [KiloCode — Codebase Indexing & Autocomplete System](#3-kilocode--codebase-indexing--autocomplete-system)
4. [OpenTUI — Syntax Highlighting for Terminal Rendering](#4-opentui--syntax-highlighting-for-terminal-rendering)
5. [Zed Editor — Full Editor Integration](#5-zed-editor--full-editor-integration)
6. [oh-my-pi — Rust-Based Native Parsing](#6-oh-my-pi--rust-based-native-parsing)
7. [Tree-Sitter Query Language Patterns](#7-tree-sitter-query-language-patterns)
8. [Cross-Cutting Architecture](#8-cross-cutting-architecture)
9. [Dual-Use Design: One System for Both Use Cases](#9-dual-use-design-one-system-for-both-use-cases)
10. [Recommendations for Omicron](#10-recommendations-for-omicron)

---

## 1. Executive Summary

Tree-sitter is used across the analyzed codebases for **two distinct but overlapping purposes**:

| Use Case | Found In | What It Does |
|----------|----------|--------------|
| **Syntax Highlighting** | OpenTUI, Zed | Highlights.scm queries → styled text for display |
| **Semantic Analysis** | Dirac, KiloCode, Zed | definition/reference queries → symbol index, code understanding, autocomplete |

The key insight: **both use cases start from the same parse tree** but use different query files. A unified system needs:

1. **Parser management** — WASM-based language parser loading (web-tree-sitter)
2. **Query management** — Multiple query files per language (highlights, definitions, references, etc.)
3. **AST caching** — Incremental parsing for live updates
4. **Result processing** — Two output paths: styled text chunks vs. structured symbol data

### Codebase Comparison

| Aspect | Dirac | KiloCode | OpenTUI | Zed |
|--------|-------|----------|---------|-----|
| **Language** | TypeScript | TypeScript | TypeScript (worker) | Rust |
| **Parser tech** | web-tree-sitter (WASM) | web-tree-sitter (WASM) | web-tree-sitter (WASM, worker) | tree-sitter native |
| **Languages** | 13 | 32 | 4 (extensible) | 25+ |
| **Queries per language** | Definitions + references | Definitions + headers + tags + imports | Highlights + injections | Highlights, brackets, indents, outline, injections, overrides, redactions, runnables, text objects, debugger |
| **Output** | `ParsedDefinition[]` | Formatted text per file | `SimpleHighlight[]` → styled text | Syntax map, outline, indent, fold |
| **Incremental** | Full reparse | Full reparse | Yes (buffer edits) | Yes (buffer edits) |
| **Semantic features** | Symbol references, call graph, function extraction | Codebase index, autocomplete context | Highlighting only | Folding, outlining, bracket matching, indent, syntax tree |
| **Persistence** | Symbol index DB (JSON) | None (ephemeral) | None | Serialized syntax map |
| **Injection** | No | No | Yes (markdown code blocks) | Yes (JS in HTML, etc.) |

---

## 2. Dirac — Semantic Code Understanding System

**Path:** `READ_ONLY/dirac/src/services/tree-sitter/`
**Purpose:** Extract structured code information for agent use — symbol definitions, references, file skeletons, call graphs, symbol replacement.

### Architecture

```
loadRequiredLanguageParsers(files[])
  → Parser.init() (web-tree-sitter WASM)
  → loadLanguage(langName) — loads tree-sitter-{lang}.wasm
  → language.query(queryText) — compiles query against language grammar
  → Parser.setLanguage(language)
  → returns { [ext]: { parser, query } }

parseFile(filePath, languageParsers)
  → parser.parse(fileContent) → AST tree
  → query.captures(tree.rootNode) → captures[]
  → Process captures into ParsedDefinition[]
```

### Key Files

#### `index.ts` — Main entry point
- `parseFile()` — parses a single file and extracts definitions
- `ParsedDefinition` — `{ lineIndex, text, indentation, lineCount?, calls? }`
- Processes captures from tree-sitter query matching
- Groups captures into definition blocks with `definitionNodes` map
- Optional call graph: tracks `name.reference` captures and checks if they're call nodes via `isCallNode()`
- De-duplication by `lastLineAdded`

#### `languageParser.ts` — Parser lifecycle
- Lazy singleton pattern: `initializeParser()` with `initializationPromise`
- `languageCache` Map + `queryCache` Map (keyed by `langName:queryText`) — prevents re-loading
- WASM search paths: `node_modules/tree-sitter-wasms/out/`, `__dirname`, `dist/`
- Supports 13 languages: JS/JSX, TS, TSX, Python, Rust, Go, C/C++, C#, Ruby, Java, PHP, Swift, Kotlin

### Query Structure

Each language has a TypeScript file exporting a raw `.scm` query string. The pattern:

```
;; Captures use a naming convention:
@name.definition.{kind}  — the identifier node (name)
@{definition}.{kind}      — the encompassing definition node
@name.reference           — any identifier usage
@doc                       — preceding documentation comment
```

**Example (TypeScript):**
```scheme
(function_declaration
  name: (identifier) @name.definition.function) @definition.function

(identifier) @name.reference
(property_identifier) @name.reference
(type_identifier) @name.reference
```

### Semantic Analysis Features

1. **File skeleton** — extracts all top-level definitions with signature lines
2. **Function extraction** — finds a function by name and returns its full body (via the `@definition.function` encompassing node)
3. **Symbol reference search** — finds all references to a symbol across files using `@name.reference`
4. **Call graph** — maps which functions call which other functions within definition bodies
5. **Symbol replacement** — replaces a symbol name in all definition + reference locations

### Symbol Index Service (`SymbolIndexService.ts`)
- Creates a persistent JSON index of all symbols in a project
- `FileIndexEntry`: `{ mtime, size, hash, symbols: [{ n, t, k, r }] }`
- Batch scanning with concurrency limit (via `p-limit`)
- Excludes `node_modules`, `.git`, build directories

### Strengths
- Clean separation: parser init → load → parse → capture → process
- `query.captures()` is simpler than manual tree walking
- Call graph analysis on captured nodes is clever
- Query naming convention is extensible

### Weaknesses
- Full reparse on every call (no incremental parsing)
- No syntax highlighting support (queries are definition/reference-only)
- Index is JSON-based — expensive for large projects
- No file watching / live updates

---

## 3. KiloCode — Codebase Indexing & Autocomplete System

**Path:** `READ_ONLY/kilocode/packages/kilo-indexing/src/tree-sitter/`
**Purpose:** Extract code definitions for codebase indexing, used to provide context to AI autocomplete.

### Architecture

```
loadRequiredLanguageParsers(files[], sourceDirectory?)
  → Parser.init() with WASM runtime resolution
  → Loads language WASMs + compiles queries
  → Returns { [ext]: { parser, query } }

parseFile(filePath, languageParsers)
  → parser.parse(fileContent) → tree
  → query.captures(tree.rootNode) → captures[]
  → processCaptures(captures, lines, language) → formatted output string

parseSourceCodeDefinitionsForFile(filePath)
  → Dispatch to tree-sitter or markdown parser
  → Returns formatted "# FileName\nline--line | source line"
```

### Key Differences from Dirac

1. **More languages**: 32 vs 13 (includes CSS, HTML, Lua, Elixir, OCaml, Solidity, TLA+, TOML, Vue, Zig, Scala, etc.)
2. **Markdown support**: Custom parser (`markdownParser.ts`) that mimics tree-sitter `QueryCapture[]` interface for headers
3. **Min component lines**: Configurable threshold (`getMinComponentLines()`) — skips definitions shorter than N lines
4. **HTML/JSX filtering**: Skips HTML element lines in JSX/TSX files (`isNotHtmlElement`)
5. **De-duplication**: Uses `processedLines` Set with `startLine-endLine` keys
6. **Query variety**: Separate queries for definitions, code snippets, import detection, tag queries, root-path context, static context

### Query Categories (from `kilo-vscode/tree-sitter/`)

| Query Type | Purpose | Example Files |
|------------|---------|--------------|
| `code-snippet-queries/*.scm` | Extract relevant code snippets for autocomplete | `python.scm`, `typescript.scm` |
| `import-queries/*.scm` | Detect import statements | `cpp.scm`, `java.scm`, `python.scm`, `typescript.scm` |
| `tag-queries/*.scm` | Extract symbol tags (CTags-compatible) | `tree-sitter-python-tags.scm`, `tree-sitter-rust-tags.scm` |
| `root-path-context-queries/**/*.scm` | Find root-level definitions | `python/function_definition.scm`, `typescript/class_declaration.scm` |
| `static-context-queries/**/*.scm` | Extract relevant types and headers | `typescript-get-toplevel-headers.scm`, `typescript-find-typedecl-given-typeidentifier.scm` |

### Markdown Parser (`markdownParser.ts`)
- Parses ATX headers (`# Header`, `## Header`) and setext headers
- Returns mock `QueryCapture[]` objects compatible with tree-sitter's interface
- Calculates section ranges (header start → next header start)

### Strengths
- Broadest language support among TS-based systems
- Multiple query categories serve different use cases from a single AST
- Markdown parser with compatible interface extends the same processing pipeline
- Configurable thresholds prevent noise

### Weaknesses
- No incremental parsing
- No syntax highlighting
- Formatted text output is fragile for structured consumption
- `require()` calls for `web-tree-sitter` are synchronous Node.js-isms

---

## 4. OpenTUI — Syntax Highlighting for Terminal Rendering

**Path:** `READ_ONLY/opentui/packages/core/src/lib/tree-sitter/`
**Purpose:** Real-time syntax highlighting in a terminal UI environment. Powers code display in OpenCode.

### Architecture

```
TreeSitterClient (main thread)
  ↔ Web Worker (parser.worker.ts)
     → Loads WASM parsers on demand
     → Parses buffer content
     → Runs highlight queries
     → Returns SimpleHighlight[]
  → treeSitterToStyledText() or treeSitterToTextChunks()
  → Rendered via Code renderable
```

### Key Components

#### `client.ts` — TreeSitterClient
- Manages a **Web Worker** for off-thread parsing
- Buffer management: `buffers: Map<number, BufferState>`
- Edit queue per buffer: `ProcessQueue<EditQueueItem>` with debouncing
- Events: `highlights:response`, `buffer:initialized`, `buffer:disposed`
- Worker messages: `initialize`, `setBuffer`, `applyEdits`, `getHighlights`, `disposeBuffer`
- Error recovery: worker auto-restart on crash

#### `parser.worker.ts` — Worker-Side Parsing
- Loads WASM parsers (initially: JavaScript, TypeScript, Markdown, Zig)
- Maintains a `Map<number, Tree>` for incremental parsing
- `applyEdits()` — applies tree-sitter `tree.edit()` for incremental updates
- `getHighlights()` — runs query captures and emits results

#### `types.ts` — Type System
```typescript
export type SimpleHighlight = [number, number, string, HighlightMeta?]
// startOffset, endOffset, groupName, optional metadata

export interface HighlightRange {
  startCol: number
  endCol: number
  group: string
}

export interface HighlightResponse {
  line: number
  highlights: HighlightRange[]
  droppedHighlights: HighlightRange[]
}

export interface FiletypeParserOptions {
  filetype: string
  aliases?: string[]
  queries: {
    highlights: string[]   // URLs to highlights.scm files
    injections?: string[]  // URLs to injections.scm files
  }
  wasm: string             // URL to WASM file
  injectionMapping?: InjectionMapping
}
```

#### `tree-sitter-styled-text.ts` — Highlight to Styled Text
- Converts `SimpleHighlight[]` to `TextChunk[]` with ANSI styles
- Style resolution: `SyntaxStyle.getStyle(group)` maps group names (e.g., `"keyword"`, `"string"`) to terminal colors
- Injection handling: suppresses parent block styles when child injection is active
- Boundary sorting: `start`/`end` offsets with correct ordering for nested highlight ranges

#### `resolve-ft.ts` — Filetype Resolution
- Maps file extensions and filenames to filetypes
- `filetypeForPath()` — uses extension, then basename patterns, then shebang detection

### `Code.ts` — Renderable
- `CodeRenderable` wraps tree-sitter in a renderable component
- `streaming` mode: highlights as content streams in (no re-parse on every chunk)
- `conceal` mode: hides certain nodes (e.g., code fences in markdown)
- `onHighlight` / `onChunks` callbacks for customization
- Highlight snapshot IDs for cache invalidation

### Performance Features
- **Worker-based** — parsing off the main thread
- **Incremental** — `tree.edit()` for live buffer edits
- **Debounced** — edit queue debounces rapid changes
- **Lazy loading** — parsers loaded on first use
- **Empty buffer fast path** — skips parsing for empty content

### Strengths
- **Only system with incremental parsing** — crucial for interactive use
- **Worker isolation** — prevents UI jank from parsing
- **Full pipeline**: filetype → parser → query → styled text
- **Streaming support**: highlights partial content as it arrives
- **Extensible**: parser config from remote URLs or local paths
- **Error-robust**: worker crash restarts cleanly

### Weaknesses
- Only 4 built-in languages (JS, TS, Markdown, Zig) — extensible but not exhaustive
- No semantic analysis (definition/reference queries only)
- WASM/URL-based parser loading requires network or pre-bundled assets
- TypeScript-only (not directly usable in .NET)

---

## 5. Zed Editor — Full Editor Integration

**Path:** `READ_ONLY/zed/crates/language_core/` and `crates/grammars/`
**Purpose:** Complete tree-sitter integration in a Rust-based editor.

### Architecture

```
Grammar (per-language)
  ├── ts_language: tree_sitter::Language
  ├── highlights_config: HighlightsConfig { query, identifier_capture_indices }
  ├── brackets_config: BracketsConfig
  ├── indents_config: IndentConfig
  ├── outline_config: OutlineConfig
  ├── text_object_config: TextObjectConfig
  ├── injection_config: InjectionConfig
  ├── redactions_config: RedactionConfig
  ├── runnable_config: RunnableConfig
  ├── override_config: OverrideConfig
  └── debug_variables_config: DebugVariablesConfig
```

### Query Categories

Zed uses **10 distinct query types per language**:

| Query Type | `.scm` files | Purpose |
|------------|-------------|---------|
| `highlights` | `highlights.scm` | Syntax highlighting (token coloring) |
| `brackets` | `brackets.scm` | Bracket matching and auto-pairing |
| `indents` | `indents.scm` | Indentation logic |
| `outline` | `outline.scm` | Document symbol outline |
| `injections` | `injections.scm` | Language injection (e.g., JS in HTML) |
| `overrides` | `overrides.scm` | Override highlight captures |
| `redactions` | `redactions.scm` | Hide certain syntax nodes |
| `runnables` | `runnables.scm` | Detect runnable code blocks |
| `textobjects` | `textobjects.scm` | Text object selection (vim-style) |
| `debugger` | `debugger.scm` | Debugger variable/scope detection |

### Key Implementation Details

**`grammar.rs`:**
- `Grammar` struct holds all compiled queries + the tree-sitter language
- `GrammarId` with atomic counter for unique IDs
- `HighlightMap` maps highlight capture indices to styled colors
- `HighlightsConfig` stores pre-compiled `Query` + identifier capture indices

**`queries.rs`:**
- `QUERY_FILENAME_PREFIXES` maps filename prefixes to `LanguageQueries` fields
- `LanguageQueries` struct with one `Option<Cow<'static, str>>` per query type
- Queries are loaded from `.scm` files bundled with each language

**`language.rs` — Buffer-level parsing:**
- Maintains a `tree_sitter::Tree` per buffer
- Applies incremental edits via `tree.edit()` and `parser.parse(previous_tree)`
- Computes a `SyntaxMap` from highlights + overrides

**`syntax_map.rs`:**
- Converts parsed tree + highlights into a character-level syntax map
- Used for rendering: each character has a `SyntaxTokenId`
- Supports custom highlight overrides per theme

### Strengths
- **Most comprehensive query set** — 10 query types per language
- **Production-scale** — powers Zed editor's syntax highlighting, folding, indentation, bracket matching, outlining
- **Incremental parsing** — `tree.edit()` with dirty-range tracking
- **Syntax Map** — character-level token mapping for efficient rendering
- **Injection support** — embedded languages (JS in HTML, Markdown in code comments)
- **Language registry** — `LanguageRegistry` manages all loaded languages

### Weaknesses
- Rust-specific (no direct use in .NET/TypeScript)
- Complex — many interconnected systems
- Query loading from filesystem (not URL-based)

---

## 6. oh-my-pi — Rust-Based Native Parsing

**Path:** `READ_ONLY/oh-my-pi/crates/pi-natives/src/`
**Purpose:** Native Rust tree-sitter integration for pi, focused on code summarization.

### Key Files

- `ast.rs` — AST abstraction over tree-sitter trees
- `language/mod.rs` — Language registry
- `language/parsers.rs` — Parser loading from WASM
- `summary.rs` — Code summarization from AST

### Architecture

- Uses the `tree-sitter` Rust crate directly (not WASM)
- Loads parsers as dynamic libraries or built-in
- `ast.rs` provides an AST abstraction layer with:
  - `AstNode` — tree node with children, text, range
  - `WalkEvent` — visitor pattern for tree traversal
  - Pre-order and post-order iteration
- `summary.rs` — generates code summaries by extracting function signatures, class declarations, and their doc comments

### Relevance

- Demonstrates native Rust tree-sitter usage for performance-critical paths
- AST abstraction layer (`AstNode`, `WalkEvent`) is useful for both highlighting and semantic analysis
- Code summarization is a higher-level operation built on tree-sitter queries

---

## 7. Tree-Sitter Query Language Patterns

### 7.1 Syntax Highlighting Queries (OpenTUI/Zed)

Tokenize code into named categories:

```scheme
(keyword) @keyword
(identifier) @variable
(function_declaration name: (identifier) @function)
(string) @string
(comment) @comment
(number) @number
(operator) @operator
(punctuation) @punctuation.delimiter
```

Group naming conventions:
- `@variable`, `@function`, `@method` — identifiers by role
- `@keyword`, `@string`, `@number`, `@comment`, `@operator` — token types
- `@type`, `@type.builtin`, `@constant`, `@constant.builtin` — semantic types
- `@punctuation.delimiter`, `@punctuation.bracket` — syntactic punctuation
- `@string.special`, `@function.method`, `@variable.builtin` — sub-categories

### 7.2 Semantic Analysis Queries (Dirac/KiloCode)

Extract definitions and references:

```scheme
;; Captures with `.definition` and `.name.definition` suffixes
(function_declaration
  name: (identifier) @name.definition.function) @definition.function

(class_declaration
  name: (type_identifier) @name.definition.class) @definition.class

(method_definition
  name: (property_identifier) @name.definition.method) @definition.method

;; References
(identifier) @name.reference
```

Pattern conventions:
- `@name.definition.{kind}` — the identifier node (for getting the name)
- `@{definition}.{kind}` — the encompassing declaration node (for getting the full body range)
- `@name.reference` — any identifier usage
- `@doc` — preceding documentation comments (with `#select-adjacent!` predicate)

### 7.3 Combined Pattern: One Query Serving Both

```scheme
;; Both highlight AND define
(function_declaration
  name: (identifier) @function @name.definition.function) @definition.function

(class_declaration
  name: (type_identifier) @type @name.definition.class) @definition.class

;; Reference only
(identifier) @variable @name.reference
```

With named captures, a single query can serve both purposes — the highlighter uses `@function`, `@type`, `@variable` while the semantic analyzer uses `@name.definition.*`, `@definition.*`, `@name.reference`.

### 7.4 Predicates

```scheme
(#match? @capture "^[A-Z]")
(#eq? @capture "require")
(#is-not? local)
(#strip! @doc "^[\\s\\*/]+|[\\s\\*/]+$")
(#select-adjacent! @doc @definition.function)
```

Used for:
- Pattern matching on captured text (e.g., identify constructors by capitalization)
- Equality checks (e.g., detect `require` calls)
- Local vs. global scope checks
- Documentation cleanup

---

## 8. Cross-Cutting Architecture

### 8.1 Parser Lifecycle

```
1. Initialize runtime (Parser.init() with WASM path)
2. Load language (Language.load("tree-sitter-{lang}.wasm"))
3. Compile queries (language.query(queryText))
4. Create parser instance (new Parser())
5. Set language (parser.setLanguage(language))
6. Parse (parser.parse(text, previousTree?))
7. Query (query.captures(rootNode) or query.matches(rootNode))
8. Process captures
```

### 8.2 Incremental Parsing

Only OpenTUI and Zed implement true incremental parsing:

```
tree.edit(range)  // Update tree with edit
parser.parse(text, previousTree)  // Reuse unchanged nodes
```

This is critical for:
- Live syntax highlighting as user types
- Real-time semantic analysis in editor

### 8.3 Worker/Thread Model

| System | Model | Rationale |
|--------|-------|-----------|
| OpenTUI | Web Worker | Offload parsing from main thread |
| Dirac | Main thread | Batch processing, no UI |
| KiloCode | Main thread | Batch indexing |
| Zed | Async task pool | Responsive editor |

### 8.4 Query Caching

```typescript
// Dirac/KiloCode pattern
const queryCache = new Map<string, Parser.Query>()
// Key: `${langName}:${queryText}`
// Prevents recompiling queries for same language + query text
```

Important: Queries compiled against one language grammar **cannot** be used with another grammar (even for the same query text). Each query is bound to its language's grammar.

### 8.5 Filetype Resolution

```typescript
// OpenTUI: resolve-ft.ts
filetypeForPath(path) → "typescript" | "javascript" | "markdown" | "zig"

// Dirac/KiloCode: switch on file extension
ext → langName mapping

// Zed: LanguageConfig with extensions, filenames, shebangs
```

---

## 9. Dual-Use Design: One System for Both Use Cases

### 9.1 Unified Data Flow

```
Source Code
    │
    ▼
┌─────────────────────────────────────────────┐
│           Language Resolver                  │
│  (extension → langName → parser lookup)     │
└─────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────┐
│              Parser Manager                  │
│  ┌─────────────┐  ┌──────────────────────┐  │
│  │ WASM Loader  │  │ Language Cache       │  │
│  │ (web-ts)     │  │ (Map<lang, Parser>)  │  │
│  └─────────────┘  └──────────────────────┘  │
└─────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────┐
│            Tree-Sitter Parse                 │
│  parser.parse(text, previousTree?) → Tree   │
└─────────────────────────────────────────────┘
    │
    ├──────────────────────┬──────────────────────┐
    ▼                      ▼                      ▼
┌──────────────┐   ┌──────────────┐   ┌──────────────────┐
│ HIGHLIGHTS    │   │ DEFINITIONS   │   │ REFERENCES        │
│ Query (.scm)  │   │ Query (.scm)  │   │ Query (.scm)      │
│               │   │               │   │                   │
│ @keyword      │   │ @name.def.*   │   │ @name.reference   │
│ @string       │   │ @definition.* │   │                   │
│ @comment      │   │ @doc          │   │                   │
│ @function     │   │               │   │                   │
└──────┬───────┘   └──────┬────────┘   └────────┬─────────┘
       │                  │                      │
       ▼                  ▼                      ▼
┌──────────────┐   ┌──────────────┐   ┌──────────────────┐
│ Style Engine │   │ Symbol       │   │ Reference         │
│ (SyntaxStyle │   │ Indexer      │   │ Resolver          │
│  → colors)   │   │ (Persistent) │   │ (Cross-file)      │
└──────┬───────┘   └──────┬────────┘   └────────┬─────────┘
       │                  │                      │
       ▼                  ▼                      ▼
┌────────────────────────────────────────────────────┐
│              Terminal / Agent Output                │
│  Styled text chunks  │  Symbol list  │  References  │
└────────────────────────────────────────────────────┘
```

### 9.2 Key Design Principles

1. **Single parse, multiple queries**: Parse once, run N query files against the same tree. This avoids redundant parsing work.

2. **Query files are modular**: Each .scm file serves one purpose. Languages with both highlighting and analysis needs simply have more .scm files.

3. **Common capture processing pipeline**: Different query types produce different output types, but they all go through the same `captures → process → emit` pipeline.

4. **Lazy parser loading**: Parsers are loaded on first use and cached. Only languages actually encountered are loaded.

5. **Incremental where possible**: For interactive scenarios (live syntax highlighting), use `tree.edit()` to avoid full reparse.

### 9.3 Query File Organization per Language

```
languages/{lang}/
  ├── highlights.scm       → Syntax highlighting (→ styled text)
  ├── definitions.scm      → Definition extraction (→ symbol index)
  ├── references.scm       → Reference capture (→ cross-ref)
  ├── injections.scm       → Language injection (→ embedded lang highlighting)
  ├── brackets.scm         → Bracket matching (→ editor feature)
  ├── indents.scm          → Indentation rules (→ editor feature)
  ├── outline.scm          → Document outline (→ symbol tree)
  ├── textobjects.scm      → Text objects (→ selection)
  ├── runnables.scm        → Runnable detection (→ code execution)
  └── imports.scm          → Import detection (→ dependency graph)
```

### 9.4 Implementation Strategy for Omicron

**Phase 1: Core Infrastructure**
- Parser manager with WASM loading
- Language registry with extension → grammar mapping
- Single file parsing + query execution

**Phase 2: Highlighting Path**
- Highlights query files per language
- `SyntaxStyle` → ANSI color mapping
- `StyledTextChunk` → terminal output (integrates with Plan 0007's `TerminalFrame`)

**Phase 3: Semantic Analysis Path**
- Definitions query → `SymbolIndex`
- References query → cross-reference resolution
- Persistent symbol database
- Agent-facing API: `getDefinitions(path)`, `findReferences(symbol)`, `getFileSkeleton(path)`

**Phase 4: Advanced Features**
- Incremental parsing for live updates
- Injection support (markdown code blocks, template languages)
- Call graph construction
- Code summarization

---

## 10. Recommendations for Omicron

### 10.1 Architecture Decisions

| Decision | Recommendation | Rationale |
|----------|---------------|-----------|
| **Parser technology** | web-tree-sitter (WASM) via existing .NET WASM interop or a dedicated Rust/WASM native host | Portable, no native build per platform |
| **Thread model** | Separate thread/worker for parsing | Avoid blocking the rendering or event loop |
| **Query format** | Standalone `.scm` files per query type per language | Following Zed's model (modular, composable) |
| **Incremental parsing** | Yes, implement via `tree.edit()` | Critical for interactive editing/streaming |
| **Cache layer** | Parser cache + Query cache (keyed by `langName:queryText`) | Avoid re-loading/recompiling |
| **Output format** | Two output types: `StyledTextChunk[]` (highlighting) and `SymbolDescriptor[]` (analysis) | Clean separation of concerns |

### 10.2 Language Support Priority

Based on Omicron's expected use cases (C# development, polyglot code understanding):

| Priority | Languages | Notes |
|----------|-----------|-------|
| **Tier 1** | C#, TypeScript/JavaScript, Python, Markdown | Omicron's own code + common agent targets |
| **Tier 2** | Rust, Go, C/C++, Java | Common OSS projects |
| **Tier 3** | Ruby, PHP, Swift, Kotlin | Popular languages |
| **Tier 4** | Zig, Lua, Elixir, Scala, Solidity | Niche but supported in KiloCode |

### 10.3 Adopt from Each Codebase

| Feature | Source | Why |
|---------|--------|-----|
| **Worker-based parsing** | OpenTUI | Prevents UI jank |
| **Incremental tree edits** | OpenTUI, Zed | Live streaming + interactive use |
| **Multi-query architecture** | Zed | 10 query types per language |
| **Named capture convention** | Dirac | `@name.definition.*` / `@definition.*` / `@name.reference` |
| **Syntax style engine** | OpenTUI | Maps highlight groups to colors |
| **Symbol index persistence** | Dirac | Cross-session code understanding |
| **Markdown parser** | KiloCode | Mock captures for non-tree-sitter content |
| **Injection handling** | OpenTUI, Zed | Language-in-language highlighting |
| **AST abstraction** | oh-my-pi | WalkEvent visitor pattern |
| **Query caching by lang+text** | Dirac, KiloCode | Prevents cross-grammar contamination |

### 10.4 Integration with Omicron's Renderer

The tree-sitter highlighting output (`StyledTextChunk[]`) should integrate with Plan 0007's `TerminalFrame.SetText()`:

```csharp
// Pseudo-code for integration
var highlights = treeSitter.ParseAndHighlight(code, "csharp");
foreach (var chunk in highlights)
{
    var style = syntaxStyle.GetStyle(chunk.Group); // "keyword" → TextStyle
    frame.SetText(row, col, chunk.Text, style);
}
```

### 10.5 Agent-Facing API for Semantic Analysis

```csharp
// Agent-facing semantic API
interface ICodeUnderstandingService
{
    // File-level
    Task<FileSkeleton> GetFileSkeletonAsync(string path);
    Task<FunctionDefinition?> GetFunctionAsync(string path, string functionName);
    Task<List<SymbolDescriptor>> GetDefinitionsAsync(string path);
    
    // Cross-file
    Task<List<SymbolReference>> FindReferencesAsync(string symbol, string? scopePath);
    Task<CallGraph> GetCallGraphAsync(string path, string functionName);
    
    // Search
    Task<List<SymbolDescriptor>> SearchSymbolsAsync(string query, string? scopePath);
}
```

### 10.6 Avoiding Common Pitfalls

1. **Don't reparse on every keystroke**: Debounce edits and use incremental parsing. OpenTUI's debounce + edit queue is a good model.

2. **Don't block the render thread**: Keep parsing in a worker/background task. OpenTUI's worker pattern is mandatory for smooth TUI updates.

3. **Don't mix query result types**: Highlighting queries produce styled offsets; semantic queries produce structured data. Keep them separate.

4. **Don't cache queries across grammars**: A `Query` compiled against TypeScript grammar cannot be used with JavaScript grammar. Key caches by `langName:queryText`.

5. **Don't ignore injection**: Markdown code blocks, JSX expressions, and template languages all need injection support for correct highlighting.

6. **Don't use string-based output for analysis**: Dirac and KiloCode's formatted text output is fragile. Use structured `SymbolDescriptor` objects.

### 10.7 Build vs. Buy Decision

| Approach | Pros | Cons |
|----------|------|------|
| **Wrap web-tree-sitter in .NET** | Leverages existing ecosystem; WASM is portable | Requires WASM interop in .NET |
| **Use tree-sitter Rust crate via FFI** | Highest performance; full native API | Native builds per platform; complex P/Invoke |
| **Host a Node.js/worker process** | Reuses existing TypeScript code; easy to extend | Process overhead; serialization cost |
| **Use tree-sitter C API via P/Invoke** | Direct integration; no JS dependency | Must build WASM parsers to shared libs |

**Recommended approach for Omicron:** Start with web-tree-sitter hosted in a background **worker process** (Node.js or Deno) communicating with Omicron via a simple JSON protocol. This gives the richest ecosystem (all KiloCode + Dirac query files can be reused directly) while keeping parsing off the main .NET thread. If performance becomes critical, the worker can be reimplemented in Rust with the same protocol.

---

*End of Report 0002*
