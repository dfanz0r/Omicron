# Binary File Read Handling in Agent CLI Tools

**Date:** 2026-05-10  
**Scope:** Analysis of how agent CLI tools handle reading binary file formats — detection, classification, format-specific extraction, and display  
**Author:** Pi coding agent

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [What "Binary File Handling" Means for Agent CLIs](#what-binary-file-handling-means-for-agent-clis)
3. [Gemini CLI — Full Classification Pipeline](#1-gemini-cli--full-classification-pipeline)
4. [Dirac — Format-Specific Extraction Engine](#2-dirac--format-specific-extraction-engine)
5. [OpenCode / KiloCode — Basic Binary Detection](#3-opencode--kilocode--basic-binary-detection)
6. [Codex (OpenAI) — Raw Base64 Protocol](#4-codex-openai--raw-base64-protocol)
7. [Claude Code — Minimalist Design](#5-claude-code--minimalist-design)
8. [Pi (Original, pi-mono) — Image-Only Detection](#6-pi-original-pi-mono--image-only-detection)
9. [oh-my-pi (Fork) — Notebook-Only Support](#7-oh-my-pi-fork--notebook-only-support)
10. [Zed — Editor, Not Agent CLI](#8-zed--editor-not-agent-cli)
11. [Hex View / Hex Dump: A Universal Gap](#9-hex-view--hex-dump-a-universal-gap)
12. [Recommended Architecture: Model-Aware Adaptive Binary Handling](#10-recommended-architecture-model-aware-adaptive-binary-handling)
13. [Design Space: What Binary File Handling Could Look Like](#11-design-space-what-binary-file-handling-could-look-like)
14. [User Attachments vs. Tool-Based File Reads](#12-user-attachments-vs-tool-based-file-reads)
15. [Claude Code in the Comparison Tables](#13-claude-code-in-the-comparison-tables)
16. [Cross-Cutting Comparison](#cross-cutting-comparison)
17. [Recommendations](#recommendations)
18. [Source References](#source-references)

---

## Executive Summary

This report examines how **8 agent CLI tools** handle reading files that are not plain text: binary files, images, PDFs, audio, video, and office documents. The tools fall into five categories:

| Approach | Tools | What they do with binary files |
|----------|-------|-------------------------------|
| **Full classification pipeline** | Gemini CLI | Detects 7 file types, returns text/images/PDF/audio/video as appropriate base64 inline data, rejects unknown binary |
| **Format-specific extraction** | Dirac | Has dedicated extractors for PDF, DOCX, XLSX, IPYNB, and images; falls back to charset detection for unknown formats |
| **Basic binary detection** | OpenCode/KiloCode, Pi, oh-my-pi | Detects binary via extension + content heuristics, returns error for unknown binary, handles images as base64 attachments |
| **Minimalist shell-script approach** | Claude Code | Null-byte check in first 8KB; PDFs extracted via `pdftotext` shell command; no image support; no hash anchors |
| **Raw bytes protocol** | Codex | No detection at all — returns all files as base64-encoded bytes; the model decides how to interpret them |

**Key finding:** None of the tools implement a **hex dump / hex view** for binary file inspection. When encountering an unrecognized binary file, they either reject it with an error, return raw base64, or silently corrupt it by reading as UTF-8. There is no in-between mode that lets the model inspect the binary structure.

---

## What "Binary File Handling" Means for Agent CLIs

Agent CLI tools read files in response to model tool calls. When a model requests reading a file, the tool must decide:

1. **Is this file binary or text?** — Content-based detection (null bytes, non-printable chars) or extension-based (`.exe`, `.zip`, etc.)
2. **What kind of binary is it?** — Image, PDF, audio, video, office document, or unknown binary
3. **How should the content be returned to the model?**
   - **Text**: read as UTF-8 string (possibly with line range/truncation)
   - **Image**: read as bytes, base64-encode, return as inline data with MIME type
   - **Audio/Video**: similar to image but with audio/video MIME
   - **Office documents**: format-specific text extraction (PDF text layer, DOCX raw text, XLSX cell content)
   - **Unknown binary**: reject with error, return as base64, or show a hex dump

A well-designed binary handling system:
- Prevents binary garbage from flooding the model's context window
- Enables the model to work with images, PDFs, and audio
- Provides useful error messages when a file can't be read as text
- Optionally gives the model structured insight into binary file contents

---

## 1. Gemini CLI — Full Classification Pipeline

**Source:** `READ_ONLY/gemini-cli/packages/core/src/utils/fileUtils.ts`  
**Read tool:** `packages/core/src/tools/read-file.ts`

### Architecture

Gemini CLI has the most sophisticated binary handling of all the tools studied. It implements a full file classification and content-processing pipeline.

### File Type Detection (`detectFileType`)

```typescript
async function detectFileType(filePath: string): Promise<
  'text' | 'image' | 'pdf' | 'audio' | 'video' | 'binary' | 'svg'
>
```

Detection order:

1. **TypeScript override** — `.ts`, `.mts`, `.cts` extensions are always classified as `'text'` (because the MIME lookup would return `video/MP2T`)
2. **SVG check** — `.svg` → `'svg'` (read as text, but with 1MB size cap)
3. **MIME type lookup** via `mime/lite` package:
   - `image/*` → `'image'`
   - `audio/*` → `'audio'` (verified via content-based binary check to avoid MIME misidentification, e.g., issue #16888)
   - `video/*` → `'video'` (same content verification)
   - `application/pdf` → `'pdf'`
4. **Supported audio formats** — checks `.mp3`, `.wav`, `.aiff`, `.aif`, `.aac`, `.ogg`, `.flac` against a hardcoded map
5. **Known binary extensions** — `BINARY_EXTENSIONS` list from `ignorePatterns.ts`
6. **Content-based binary check** via the `isbinaryfile` npm package
7. **Default** → `'text'`

### Content Processing (`processSingleFileContent`)

Each file type is handled differently:

| Type | Handling |
|------|----------|
| **`'text'`** | Read via `readFileWithEncoding` (BOM-aware), split into lines, apply `start_line`/`end_line` range, truncate to `DEFAULT_MAX_LINES_TEXT_FILE` lines and `MAX_LINE_LENGTH_TEXT_FILE` chars per line |
| **`'svg'`** | Read as text via `readFileWithEncoding`, 1MB size cap |
| **`'image'`** | Read as Buffer, base64-encode, return as `{ inlineData: { data: base64, mimeType } }` |
| **`'pdf'`** | Same as image — base64 with MIME type |
| **`'audio'`** | Same as image — base64 with normalized MIME type (supports mp3, wav, aiff, aac, ogg, flac) |
| **`'video'`** | Same as image — base64 with MIME type |
| **`'binary'`** | Returns error: `"Cannot display content of binary file: {path}"` |

### BOM-Aware Reading (`readFileWithEncoding`)

```typescript
async function readFileWithEncoding(filePath: string): Promise<string>
```

Detects Byte Order Marks for:
- **UTF-8** (EF BB BF)
- **UTF-16 LE** (FF FE, but not FF FE 00 00 which is UTF-32 LE)
- **UTF-16 BE** (FE FF)
- **UTF-32 LE** (FF FE 00 00)
- **UTF-32 BE** (00 00 FE FF)

Strips the BOM and decodes with the appropriate encoding. Falls back to UTF-8 if no BOM is present.

### Size Limits

- **File size**: `MAX_FILE_SIZE_MB` (configurable, default likely ~50MB based on constants)
- **Text lines**: `DEFAULT_MAX_LINES_TEXT_FILE` (~2000)
- **Line length**: `MAX_LINE_LENGTH_TEXT_FILE` (~2000 chars, truncated with `... [truncated]`
- **SVG**: 1MB

### Binary-Specific Dependency

Uses the **`isbinaryfile`** npm package for content-based binary detection. This package checks for null bytes and control characters in a sample of the file.

---

## 2. Dirac — Format-Specific Extraction Engine

**Source:** `READ_ONLY/dirac/src/integrations/misc/extract-text.ts`  
**Read handler:** `src/core/task/tools/handlers/ReadFileToolHandler.ts`

### Architecture

Dirac takes a different approach: instead of classifying files into broad categories, it has **dedicated extraction functions** for specific binary formats and falls back to charset detection for everything else.

### Format Dispatch (`callTextExtractionFunctions`)

```typescript
async function callTextExtractionFunctions(filePath: string): Promise<string>
```

Dispatcher based on file extension:

| Extension | Library | What it does |
|-----------|---------|-------------|
| `.pdf` | `pdf-parse` | Extracts text layer via `pdf(dataBuffer).text` |
| `.docx` | `mammoth` | Extracts raw text via `mammoth.extractRawText({ path: filePath })` |
| `.ipynb` | Custom | Strips cell outputs, returns code + markdown structure via `sanitizeNotebookForLLM` |
| `.xlsx` | `exceljs` | Iterates all sheets/rows/cells, formats dates, formulas, hyperlinks, rich text; capped at 50,000 rows |
| **Default** | `jschardet` + `iconv-lite` | Detects charset encoding, then decodes via iconv |

### Default Path (Unknown Files)

For files without a recognized extension:

1. **Size check**: `fs.stat()` first — rejects files > 20MB
2. **Read**: `fs.readFile()` to get a Buffer
3. **Encoding detection**: `jschardet.detect(fileBuffer)` (charset detection)
4. **Decode**: `iconv.decode(fileBuffer, encoding)`
5. **If charset detection fails**: falls back to `isbinaryfile` check → throws `"Cannot read text for file type: {ext}"`

### Image Handling

Images are handled **separately** before text extraction, in `extractFileContent()`:

```typescript
async function extractFileContent(absolutePath: string, modelSupportsImages: boolean)
```

- Supports: `.png`, `.jpg`, `.jpeg`, `.webp`
- If model supports images: reads as Buffer, creates an `Anthropic.ImageBlockParam` with base64 data
- If model doesn't support images: throws `"Current model does not support image input"`

### Content Truncation

After extraction, all content is passed through `truncateContent()` (400KB limit) to prevent context overflow.

### File Hash Tracking

Dirac computes an FNV-1a hash of the file content and includes it in the output:

```
[File Hash: a1b2c3d4]
Apple§    def process(data):
Brave§    total = 0
```

On subsequent reads, if the hash matches, Dirac returns:
```
no changes have been made to the file since your last read (Hash: a1b2c3d4)
```

### Binary Detection Dependency

Uses the **`isbinaryfile`** npm package (same as Gemini CLI) for content-based binary detection, plus **`jschardet`** for charset detection and **`iconv-lite`** for decoding.

---

## 3. OpenCode / KiloCode — Basic Binary Detection

**Source:** `READ_ONLY/opencode/packages/opencode/src/tool/read.ts`  
**File model:** `packages/opencode/src/file/index.ts`

### Architecture

OpenCode implements a custom `isBinaryFile()` function with two detection strategies, and handles images/PDF as special cases via MIME sniffing.

### Binary Detection (`isBinaryFile`)

```typescript
const isBinaryFile = (filepath: string, bytes: Uint8Array) => {
  // 1. Extension-based detection (fast path)
  const ext = path.extname(filepath).toLowerCase()
  switch (ext) {
    case ".zip": case ".tar": case ".gz": case ".exe":
    case ".dll": case ".so": case ".class": case ".jar":
    case ".war": case ".7z": case ".doc": case ".docx":
    case ".xls": case ".xlsx": case ".ppt": case ".pptx":
    case ".odt": case ".ods": case ".odp": case ".bin":
    case ".dat": case ".obj": case ".o": case ".a":
    case ".lib": case ".wasm": case ".pyc": case ".pyo":
      return true
  }

  // 2. Content-based detection (samples first 4KB)
  if (bytes.length === 0) return false

  let nonPrintableCount = 0
  for (let i = 0; i < bytes.length; i++) {
    if (bytes[i] === 0) return true          // Null byte → binary
    if (bytes[i] < 9 || (bytes[i] > 13 && bytes[i] < 32))
      nonPrintableCount++                     // Control char (except \t,\n,\r)
  }

  return nonPrintableCount / bytes.length > 0.3
}
```

**28 known binary extensions** are hardcoded. Content-based check uses the first 4KB sample (`SAMPLE_BYTES = 4096`).

### Image/PDF Detection

```typescript
const mime = sniffAttachmentMime(sample, AppFileSystem.mimeType(filepath))
const isImage = SUPPORTED_IMAGE_MIMES.has(mime)
```

Images are detected via `sniffAttachmentMime()` which reads magic bytes from the file header, combined with MIME type from the filesystem. Supported image MIME types: `image/jpeg`, `image/png`, `image/gif`, `image/webp`.

PDFs are detected via `isPdfAttachment(mime)`.

### Content Delivery

| File type | Handling |
|-----------|----------|
| **Image** | Read as bytes, base64-encode, return as `attachments` array with `data:image/...;base64,...` URL |
| **PDF** | Same as image — base64 data URI in attachments |
| **Binary** | Returns error: `"Cannot read binary file: {filepath}"` |
| **Text** | Read as UTF-8 with line numbering (`1: content`), line range, truncation (50KB cap, 2000 char line limit) |

### Data Model

The `File.Content` schema explicitly models binary content:

```typescript
export const Content = Schema.Struct({
  type: Schema.Literals(["text", "binary"]),
  content: Schema.String,
  diff: Schema.optional(Schema.String),
  patch: Schema.optional(Patch),
  encoding: Schema.optional(Schema.Literal("base64")),
  mimeType: Schema.optional(Schema.String),
})
```

This schema supports `encoding: "base64"` + `mimeType` for binary content, though the current `read` tool implementation doesn't use this for unknown binaries — it only returns an error.

---

## 4. Codex (OpenAI) — Raw Base64 Protocol

**Source:** `READ_ONLY/codex/codex-rs/app-server/src/request_processors/fs_processor.rs`  
**Protocol:** `app-server-protocol/src/protocol/v2/fs.rs`

### Architecture

Codex takes the simplest approach of all tools studied: **all file reads return raw bytes as base64**. There is no file type detection, no text/binary distinction, and no format-specific extraction at the protocol level.

### Read Implementation

```rust
pub async fn read_file(&self, params: FsReadFileParams)
    -> Result<FsReadFileResponse, JSONRPCErrorError>
{
    let bytes = self.file_system
        .read_file(&params.path, /*sandbox*/ None)
        .await?;
    Ok(FsReadFileResponse {
        data_base64: STANDARD.encode(bytes),
    })
}
```

Every file read returns `{ data_base64: "..." }` — a base64-encoded string of the raw bytes.

### Write Implementation

```rust
pub async fn write_file(&self, params: FsWriteFileParams)
    -> Result<FsWriteFileResponse, JSONRPCErrorError>
{
    let bytes = STANDARD.decode(params.data_base64)?;
    self.file_system
        .write_file(&params.path, bytes, /*sandbox*/ None)
        .await?;
    Ok(FsWriteFileResponse {})
}
```

Write operations similarly accept base64-encoded data.

### Protocol Schema

```
FsReadFileParams { path: AbsolutePathBuf }
FsReadFileResponse { data_base64: String }
```

No MIME type, no encoding hint, no size limit — just raw bytes.

### Who Interprets the Data?

The base64 data is passed to the AI model. The **model** (not the tool) is responsible for:
- Deciding if the content is text or binary
- Decoding base64 to inspect the raw bytes
- Requesting format-specific extraction via shell commands if needed

This puts the burden on the model but gives it maximum flexibility — the model could theoretically implement its own hex inspection or format extraction by requesting shell tools.

### PDF Handling

Codex does **not** have any local PDF processing. There is no PDF-specific tool (unlike the dedicated `view_image` tool for images). PDF handling is entirely delegated to the **model provider's API**:

- For **OpenAI models** (GPT-4o, etc.): Codex has a file upload mechanism in `codex-api/src/files.rs` that uploads files to OpenAI's API via a `sediment://` URI scheme. OpenAI's API supports PDF natively — rendering pages server-side to extract both text and embedded images, just like Gemini CLI.
- For **other providers**: PDFs are returned as raw base64 bytes via the standard `read_file` response. The model must either decode them, request shell-based extraction, or accept that it cannot read the file.

```rust
// File upload to OpenAI API (not PDF-specific, supports any file type)
pub async fn upload_local_file(
    base_url: &str,
    auth: &dyn AuthProvider,
    path: &Path,
) -> Result<UploadedOpenAiFile, OpenAiFileError>
```

Codex's approach is essentially "no local processing, rely entirely on the API." This is the extreme end of the spectrum — minimal tool complexity, maximum reliance on provider capabilities.

---

## 6. Pi (Original, pi-mono) — Image-Only Detection

**Source:** `READ_ONLY/pi-mono/packages/coding-agent/src/core/tools/read.ts`

### Architecture

Pi distinguishes only between **images** and **everything else**. There is no binary file detection whatsoever.

### Read Flow

```
read(path, offset?, limit?)
  → detectImageMimeType(absolutePath)
    ├─ Image detected → read as Buffer → base64 → { ImageContent }
    └─ Not an image → read as Buffer → .toString("utf-8") → { TextContent }
```

### Image Detection

Uses `detectSupportImageMimeTypeFromFile()` from `../../utils/mime.js` to check if the file is a supported image format. If it is:

1. Read the file as Buffer
2. Optionally resize to max 2000×2000 (controlled by `autoResizeImages` option)
3. Base64-encode
4. Return as `{ type: "image", data: "...", mimeType: "..." }`

### Text Handling

If the file is not an image, it's read as text:

```typescript
const buffer = await ops.readFile(absolutePath);
const textContent = buffer.toString("utf-8");
```

**No binary detection** — binary files like `.exe`, `.zip`, or `.wasm` will be read as UTF-8 text, producing garbage characters that flood the model's context window.

### Truncation

Text content is truncated by either:
- **Lines**: `DEFAULT_MAX_LINES` (configurable)
- **Bytes**: `DEFAULT_MAX_BYTES` (configurable)

With a continuation notice: `[Showing lines X-Y of Z. Use offset=N to continue.]`

### Image Note

If the current model doesn't support image inputs, Pi prepends:
```
[Current model does not support images. The image will be omitted from this request.]
```

---

## 7. oh-my-pi (Fork) — Notebook-Only Support

**Source:** `READ_ONLY/oh-my-pi/packages/coding-agent/src/edit/read-file.ts`  
**Read tool:** `packages/coding-agent/src/tools/read.ts`

### Architecture

oh-my-pi (the fork) has a similar read implementation to the original Pi, with one addition: **Jupyter notebook support**.

### Read Flow

The `readEditFileText` function in `edit/read-file.ts`:

```typescript
export async function readEditFileText(absolutePath: string, path: string): Promise<string> {
  if (isNotebookPath(absolutePath))
    return await readEditableNotebookText(absolutePath, path);
  return await Bun.file(absolutePath).text();
}
```

### Notebook Support

- Detects `.ipynb` files via `isNotebookPath()`
- `readEditableNotebookText()` — reads the notebook JSON, extracts code cells and markdown cells, presents them in an editable text format
- `serializeEditedNotebookText()` — converts the annotated text format back to a valid `.ipynb` JSON

### Main Read Tool

The primary `read` tool in `src/tools/read.ts` (separate from `edit/read-file.ts`) has the same architecture as Pi's read tool:
- Image detection via MIME type → base64 inline data
- Everything else → `Bun.file(abspath).text()`
- **No binary file detection**

### Key Difference from Pi (original)

Both Pi and oh-my-pi lack binary file detection. The only difference is that oh-my-pi has notebook support in its edit pipeline. For general file reading, both tools will silently attempt to read binary files as UTF-8.

---

## 8. Zed — Editor, Not Agent CLI

**Source:** `READ_ONLY/zed/crates/language/src/file_content.rs`

Zed is primarily a GUI editor, not an agent CLI. Its binary handling is through standard editor mechanisms:
- The `file_content.rs` module handles file content loading for the buffer system
- Binary files in Zed open in a hex editor view via the `hex` crate
- The agent integration (assistant panel) delegates to the editor's existing file handling

Zed's hex editor functionality is notable: as a GUI editor, it can show a proper hex dump view. But this is not exposed as a tool for the agent to call — it's a user-facing editor feature.

---

## 9. Hex View / Hex Dump: A Universal Gap

The most important finding of this investigation: **none of the 7 tools implements a hex dump / hex view for binary file inspection.**

### Current Behavior for Unknown Binary Files

| Tool | When reading an unknown binary file |
|------|-------------------------------------|
| **Gemini CLI** | Error: `"Cannot display content of binary file: {path}"` — no structured view |
| **Dirac** | Tries charset detection → if that fails, throws `"Cannot read text for file type: {ext}"` |
| **OpenCode/KiloCode** | Error: `"Cannot read binary file: {filepath}"` — no structured view |
| **Codex** | Returns raw base64 string — model must decode mentally |
| **Pi (original)** | Silently reads as UTF-8 → garbage characters |
| **oh-my-pi** | Silently reads as UTF-8 → garbage characters |

### What a Hex View Could Look Like

A hex dump/hex view tool (or mode) for an agent CLI would present binary content in a structured format:

```
Offset  | Hex Bytes                           | ASCII
--------|-------------------------------------|--------------
000000  | 7f 45 4c 46 02 01 01 00 00 00 00  | .ELF........
00000c  | 00 00 00 00 00 03 00 3e 00 01 00  | ........>...
000018  | 00 00 40 00 00 00 00 00 40 00 00  | ..@.....@...
```

This would allow the model to:
- Inspect file headers and magic bytes (ELF, PE, Mach-O, ZIP magic, etc.)
- Read embedded strings in binary files
- Examine binary data structures at specific offsets
- Debug file format issues

### Why No Tool Has Implemented This

1. **Token cost**: A hex dump consumes many more tokens than a simple error message. A 1KB binary file would produce ~2-3KB of hex dump text.
2. **Model capability**: Modern LLMs can decode base64 and reason about binary data, reducing the need for a structured view.
3. **Scope**: Agent CLIs are designed for code editing, not binary analysis. Binary files are typically excluded from agent workflows.
4. **Shell fallback**: Models can always use `execute_command` with `xxd`, `od`, `hexdump`, or `python -c` to inspect binary files.

---

## 10. Recommended Architecture: Model-Aware Adaptive Binary Handling

None of the tools studied implement a truly **model-aware** binary handling system — one that adapts its behavior based on the specific model/provider's capabilities. The following architecture is recommended as a flexible, best-effort solution that works across as many models as possible.

### Core Principle

Different models and API providers have different capabilities for handling non-text file types. A useful distinction separates four capability levels:

| Level | Capability | What it means | Example models |
|-------|-----------|---------------|----------------|
| **A** | **Vision support** | Model can read image inputs (JPG/PNG screenshots, photos, charts) as base64 inline data | GPT-4o, Gemini, Claude 3.5 Sonnet, Llama 3.2 Vision, Grok, Mistral Vision |
| **B** | **PDF upload support** | API/app accepts a PDF file directly (raw bytes with `application/pdf` MIME type) | GPT-4o (via file upload API), Gemini, Claude, Grok (via attachment_search tool) |
| **C** | **Native PDF vision/document support** | System extracts PDF text **and** renders/analyzes page images, layout, charts, tables, scans | Gemini, GPT-4o, Claude |
| **D** | **Tool-based PDF support** | Model itself may not ingest PDFs directly; a retrieval/OCR/document-search tool parses the PDF first | Grok (attachment_search), Mistral (Document AI / OCR), Cohere (agents/tools for PDF) |

**Key insight:** These are cumulative but not equivalent. A model can have vision (**A**) without native PDF support (**C**) — Claude has vision but requires the server-side API to render PDFs. Another model may offer PDF upload (**B**) via tool-based retrieval (**D**) without true vision-level PDF understanding. And many open-source vision models (Llama, local models) fall into **A-only**: they accept image inputs but have no built-in PDF rendering — every PDF page must be converted to an image by the client.

| Provider / family | Vision? | Native/direct PDF support? | Notes |
|-------------------|:-------:|:-------------------------:|-------|
| **OpenAI GPT-4o and later** | Yes | Yes | PDF parsing with text and page images requires vision-capable models (GPT-4o+). ([OpenAI docs][1]) |
| **Anthropic Claude active models** | Yes | Yes | All active Claude models support PDF processing via direct API access and Google Vertex AI. ([Claude docs][2]) |
| **Google Gemini multimodal models** | Yes | Yes | Native vision processes PDFs; all Gemini multimodal models support it. ([Google AI docs][3]) |
| **xAI Grok** | Yes | Partly / tool-based | Uses `attachment_search` tool for document reasoning; images are JPG/PNG only. ([xAI docs][4]) |
| **Mistral** | Yes (vision models) | Via Document AI / OCR | Document parsing/OCR goes through Document AI; Mistral OCR handles PDFs/images. ([Mistral docs][5]) |
| **Meta Llama vision models** | Yes | Usually no direct PDF input | PDF handling requires converting pages to images or using external parser/RAG. ([Llama docs][6]) |
| **Cohere Command A Vision** | Yes | Mostly vision/document-image workflow | PDF support may be platform/tool dependent; PDF extractor example uses agents/tools. ([Cohere docs][7]) |
| **DeepSeek, GPT-3.5, Claude 3 Haiku, etc.** | No | No | Text-only — no image or document inputs accepted.

[1]: https://developers.openai.com/api/docs/guides/file-inputs
[2]: https://platform.claude.com/docs/en/build-with-claude/pdf-support
[3]: https://ai.google.dev/gemini-api/docs/document-processing
[4]: https://docs.x.ai/developers/files
[5]: https://docs.mistral.ai/studio-api/conversations/vision
[6]: https://www.llama.com/docs/how-to-guides/vision-capabilities
[7]: https://docs.cohere.com/docs/command-a-vision

### Architecture Overview

The decision flow must account for **four separable capabilities**, not just one:

1. **Vision support** — can the model consume `base64` inline data with `image/*` MIME type?
2. **PDF upload support** — does the API accept raw PDF bytes with `application/pdf` MIME type?
3. **Native PDF vision** — does the API render PDF pages server-side (text + layout + embedded images)?
4. **Tool-based document support** — does the environment provide a retrieval/OCR tool the model can call?

These are **independent axes**. A model can support:
- **Vision only** (Llama 3.2 Vision) — images work, PDFs must be client-converted to images
- **Vision + native PDF** (Claude, GPT-4o, Gemini) — both work natively
- **Vision + tool-based PDF** (Grok, Mistral) — images work directly, PDFs go through a retrieval/OCR tool
- **Neither** (DeepSeek, GPT-3.5) — everything must be extracted or hex-dumped client-side

The per-file-type tables below define the exact decision logic for each format.

### Per-File-Type Decision Matrix

#### Text Files (including SVG, Markdown, Code)
Always read as UTF-8. No model-dependent decisions needed beyond offering offset/limit for truncation.

#### Images (PNG, JPG, GIF, WebP)

| Model supports images? | What to do |
|----------------------|------------|
| **Yes** | Return as base64 inline data with MIME type. Optionally resize to fit model limits (e.g., 2000×2000). |
| **No** | Return image metadata (format, dimensions, file size). **Do not attempt to read raw image bytes as text** — binary pixel data is meaningless. If OCR is available (tesseract.js), optionally extract visible text. Fall back to: `"Image file: {path} [{width}x{height}, {format}, {size}]"` or offer a hex view for structural inspection. |

#### PDF Documents

| Model supports PDF natively? | What to do |
|-----------------------------|------------|
| Model capability | What to do | Example models this applies to |
|-----------------|------------|-------------------------------|
| **Native PDF vision** (C) | Return as base64 inline data with `application/pdf` MIME type. The model's API renders it server-side, preserving both text and embedded images (including scanned pages, charts, tables). | Gemini, GPT-4o+, Claude |
| **Vision only** (A) | Render each PDF page to an image (PNG/JPEG) via `pdftoppm`, `muPDF`, `pdf.js`, or `ImageMagick`. Send as a sequence of image blocks. Text layer is embedded in the rendered images. | Llama 3.2 Vision, Mistral Vision, local models |
| **Tool-based document support** (A + D) | Let the model use the platform's built-in document tool (retrieval/OCR). For client-side fallback, extract text via `pdf-parse` or `marker` and warn that layout/images were lost. | Grok (`attachment_search`), Mistral (Document AI), Cohere |
| **Text-only** (none) | Extract text locally via `pdf-parse` or `marker`. **Warning:** all images, layout, charts, and formatting are lost. The result is plain text only. | DeepSeek, GPT-3.5, Claude 3 Haiku, Llama 3 (non-vision) |

**Practical rule of thumb for PDFs:**
- **Simple text PDFs** (reports, articles, code listings): local text extraction works well for any model.
- **Scanned PDFs, forms, invoices, slides, handwriting, math, tables, layout-sensitive docs**: use a model/platform with **native PDF vision** (Gemini, GPT-4o+, Claude) or convert pages to images for a vision model. Local text extraction will miss everything not in the text layer.
- **Open-source/local vision models**: assume **image input only**. Convert each PDF page to images, optionally OCR the text, then pass both to the model.

**Best-effort local extraction:**
```typescript
// Tiered PDF extraction strategy:
async function extractPdfContent(filePath: string, model: ModelInfo): Promise<ExtractedContent> {
  // Tier 1: Native PDF vision → pass raw bytes, let API render
  if (model.capabilities.nativePdfVision) {
    return { type: 'inlineData', mimeType: 'application/pdf', data: base64 };
  }

  // Tier 2: Vision model (but no native PDF) → render pages to images
  if (model.capabilities.vision) {
    const images = await renderPdfToImages(filePath);  // pdftoppm, muPDF, ImageMagick
    return { type: 'images', pages: images };
  }

  // Tier 3: Tool-based document support → use platform tool if available
  if (model.capabilities.toolBasedDocs) {
    // Let the model call the platform's document tool;
    // fall through to Tier 4 for client-side fallback
  }

  // Tier 4: Text-only → extract text layer (layout/images lost)
  const text = await extractPdfText(filePath);  // pdf-parse
  return {
    type: 'text',
    content: text,
    warning: 'PDF text layer extracted locally. Embedded images, charts, and layout are not preserved.',
  };
}
```

**Note on "Vision only vs native PDF vision"** — sending a PDF as images (Tier 2) is functionally different from native PDF support (Tier 1). Rendered images lose selectable text, internal hyperlinks, and the original document structure. However, for scanned documents and image-heavy PDFs, this is often the best available option for models that support images but not PDFs natively.

#### Audio Files (MP3, WAV, FLAC, etc.)

| Model supports audio? | What to do |
|----------------------|------------|
| **Yes** (Gemini, GPT-4o) | Return as base64 inline data with audio MIME type. |
| **No** | Return file metadata (duration, sample rate, format, codec). Offer to transcribe via whisper.cpp or similar if available. Fall back to `"Audio file: {path}"` with format info. Raw audio bytes are not meaningful to display as hex. |

#### Other Binary Files (ZIP, EXE, WASM, etc.)

| Model scenario | What to do |
|---------------|------------|
| **General** | Return error: `"Cannot read binary file: {path}"` with file size and extension. |
| **Hex inspection** | Offer hex dump mode showing first N bytes in `xxd`-style format (address \| hex bytes \| ASCII). This is the **universal fallback** for any binary file — it works regardless of model capabilities because it's just text. Useful for header inspection, magic byte analysis, debugging. |
| **Model can use shell tools** | Let the model use `execute_command` with `xxd`, `od`, `hexdump`, or `python3 -c` for custom binary inspection. This is often more flexible than a built-in hex view for large files. |

### Provider Capability Registry

To make model-aware decisions, the system should query a structured model registry rather than hardcoding capabilities per-model. The **[models.dev](https://models.dev/api.json)** registry provides a canonical, up-to-date `modalities` field for every model:

```json
{
  "gemini-2.5-flash": {
    "modalities": { "input": ["text", "image", "video", "audio", "pdf"], "output": ["text"] }
  },
  "claude-sonnet-4-6": {
    "modalities": { "input": ["text", "image", "pdf"], "output": ["text"] }
  },
  "gpt-5-mini": {
    "modalities": { "input": ["text", "image"], "output": ["text"] }
  }
}
```

The presence of specific strings in `modalities.input` maps directly to the capability levels:

| modalities.input contains | Capability level | What it means |
|--------------------------|-----------------|---------------|
| `"image"` | **A** — Vision support | Model accepts base64 `image/*` inline data |
| `"pdf"` | **B/C** — PDF upload & native vision | Model's API renders PDFs server-side (text + layout + images) |
| `"audio"` | Audio support | Model accepts base64 audio input |
| `"video"` | Video support | Model accepts base64 video input |
| only `"text"` | None | Text-only; binary must be extracted or hex-dumped client-side |

This avoids hardcoding. At runtime, the read tool can fetch or cache the model's entry from `models.dev/api.json` and derive its file-handling strategy purely from the `modalities.input` array.

For cases where a model supports PDF via a different mechanism (e.g., Grok's `attachment_search` tool, Mistral's Document AI), the registry entry may not reflect tool-based document support. Those cases require additional provider-specific metadata.

Example TypeScript interface for the registry:

```typescript
interface ModelCapabilities {
  // Four independent capability axes for document/binary handling
  capabilities: {
    vision: boolean;           // Level A — "image" in modalities.input
    pdfUpload: boolean;        // Level B — "pdf" in modalities.input
    nativePdfVision: boolean;  // Level C — "pdf" in modalities.input (same flag; all PDF-capable models do native vision)
    toolBasedDocs: boolean;    // Level D — provider-specific metadata (not in models.dev)
    audio: boolean;            // "audio" in modalities.input
    video: boolean;            // "video" in modalities.input
  };
  limits: {
    maxImageSize?: number;     // e.g., 20MB for GPT-4o
    maxPdfSize?: number;       // e.g., 100MB for Gemini
    maxImageCount?: number;    // Max images per request (important for PDF→image conversion)
    maxFileSize?: number;      // General file upload limit
  };
  fileUpload?: {               // For providers with separate file upload APIs
    enabled: boolean;
    maxFiles: number;
    supportedMimeTypes: string[];
  };
  pdfHandling?: {              // Guidance for client-side PDF strategy
    recommended: 'native' | 'render-to-images' | 'extract-text' | 'tool-based';
    maxPages?: number;         // Max pages for render-to-images fallback
  };
}

const MODEL_CAPABILITIES: Record<string, ModelCapabilities> = {
  // Derived from models.dev modalities + provider-specific metadata:


```typescript
interface ModelCapabilities {
  // Four independent capability axes for document/binary handling
  capabilities: {
    vision: boolean;           // Level A — accepts base64 image/* inline data
    pdfUpload: boolean;        // Level B — accepts application/pdf MIME type
    nativePdfVision: boolean;  // Level C — renders PDF server-side (text + layout + images)
    toolBasedDocs: boolean;    // Level D — platform has a retrieval/OCR tool for docs
    audio: boolean;            // Supports audio input
    video: boolean;            // Supports video input
  };
  limits: {
    maxImageSize?: number;     // e.g., 20MB for GPT-4o
    maxPdfSize?: number;       // e.g., 100MB for Gemini
    maxImageCount?: number;    // Max images per request (important for PDF→image conversion)
    maxFileSize?: number;      // General file upload limit
  };
  fileUpload?: {               // For providers with separate file upload APIs
    enabled: boolean;
    maxFiles: number;
    supportedMimeTypes: string[];
  };
  pdfHandling?: {              // Guidance for client-side PDF strategy
    recommended: 'native' | 'render-to-images' | 'extract-text' | 'tool-based';
    maxPages?: number;         // Max pages for render-to-images fallback
  };
}

const MODEL_CAPABILITIES: Record<string, ModelCapabilities> = {
  'gemini-2.5-pro': {
    capabilities: {
      vision: true,
      pdfUpload: true,
      nativePdfVision: true,   // Renders PDF server-side with full layout+images
      toolBasedDocs: false,
      audio: true,
      video: true,
    },
    limits: { maxPdfSize: 100 * 1024 * 1024 },
    pdfHandling: { recommended: 'native' },
  },
  'gpt-4o': {
    capabilities: {
      vision: true,
      pdfUpload: true,         // Via file upload API
      nativePdfVision: true,   // GPT-4o+ parse PDF with text + page images
      toolBasedDocs: false,
      audio: true,
      video: false,
    },
    limits: { maxImageSize: 20 * 1024 * 1024 },
    fileUpload: { enabled: true, maxFiles: 20, supportedMimeTypes: ['application/pdf'] },
    pdfHandling: { recommended: 'native' },
  },
  'claude-sonnet-4': {
    capabilities: {
      vision: true,
      pdfUpload: true,         // Claude API accepts PDF directly
      nativePdfVision: true,   // Server-side PDF rendering (text + images)
      toolBasedDocs: false,
      audio: false,
      video: false,
    },
    limits: { maxImageSize: 10 * 1024 * 1024 },
    pdfHandling: { recommended: 'native' },
  },
  'llama-3.2-90b-vision': {
    capabilities: {
      vision: true,
      pdfUpload: false,        // No native PDF input
      nativePdfVision: false,
      toolBasedDocs: false,
      audio: false,
      video: false,
    },
    limits: { maxImageSize: 5 * 1024 * 1024 },
    pdfHandling: { recommended: 'render-to-images', maxPages: 20 },
  },
  'grok-3': {
    capabilities: {
      vision: true,
      pdfUpload: true,         // Via file attachment
      nativePdfVision: false,  // Uses attachment_search tool internally
      toolBasedDocs: true,     // Document reasoning via retrieval tool
      audio: false,
      video: false,
    },
    pdfHandling: { recommended: 'tool-based' },
  },
  'mistral-large': {
    capabilities: {
      vision: true,
      pdfUpload: false,        // No direct PDF input
      nativePdfVision: false,
      toolBasedDocs: true,     // Document AI / OCR tool available
      audio: false,
      video: false,
    },
    pdfHandling: { recommended: 'tool-based' },
  },
  'deepseek-v3': {
    capabilities: {
      vision: false,
      pdfUpload: false,
      nativePdfVision: false,
      toolBasedDocs: false,
      audio: false,
      video: false,
    },
    pdfHandling: { recommended: 'extract-text' },
  },
};
```

### Content Return Strategy

The return format should adapt based on both the file type and the model:

```typescript
type ReadFileResult = {
  // Always present
  displayText: string;

  // For text files: the content
  text?: string;

  // For supported binary types when model can consume them:
  inlineData?: { mimeType: string; data: string };  // base64

  // For unsupported binary types that were locally extracted:
  extractedText?: string;
  extractionWarning?: string;  // e.g., "Images in PDF were not extracted"

  // For unsupported binary with no extraction:
  binaryInfo?: {
    size: number;
    mimeType: string;
    hexPreview?: string;  // First 64 bytes in hex dump format
  };
};
```

### Implementation Recommendations

1. **Centralize capability detection** in a `ModelCapabilities` service rather than hardcoding per-tool logic
2. **Always perform local text extraction** for PDF/DOCX/XLSX as a fallback, not as the primary path when native support exists
3. **Log what strategy was used** so the model knows how the file was processed (e.g., "PDF rendered server-side by Gemini API" vs "PDF text extracted locally — embedded images omitted")
4. **Offer a `force` parameter** on the read tool so the model can override automatic strategy selection and request raw bytes, hex dump, or specific extraction format
5. **Cache capability lookups** per model ID to avoid repeated checks
6. **Respect token budgets** — local extraction and hex dumps can produce large outputs; apply the same truncation rules as text reads

---

## 12. User Attachments vs. Tool-Based File Reads

A critical distinction in agent CLI tools is the **entry point** for file content:

1. **User-provided file attachments** — files the user explicitly includes in their prompt (via `@file`, drag-and-drop, file picker, or CLI arguments)
2. **Model-initiated tool calls** — files the agent model decides to read by calling `read_file`, `read`, or similar tools

These two paths often use **different pipelines** with different trade-offs, even within the same tool.

### Key Differences

| Aspect | User attachments | Tool-based reads |
|--------|-----------------|------------------|
| **Purpose** | Include file content in the *prompt* for context | Return file content as a *tool result* for the model to act on |
| **Formatting** | Minimal — raw content or inline data | Structured — line numbers, hash anchors, truncation notices, continuation hints |
| **Truncation** | Often none or minimal (user explicitly chose the file) | Always applied (offset/limit, max lines, max bytes) |
| **Binary handling** | More permissive — images/PDFs converted to inline data for the model | More restrictive — binary files error out or require explicit handling |
| **Size limits** | Typically higher (user knows what they're attaching) | Lower defaults (model might read many files) |
| **Line ranges** | Not applicable (full file) | Supported via offset/limit |
| **Hash anchors** | Not used | Used by oh-my-pi, Dirac for editing |

### Per-Tool Breakdown

#### Gemini CLI

**User attachments:** Handled via `@file` at-commands in `atCommandProcessor.ts`. Files are processed through `processSingleFileContent()` — the same function used by the `read_file` tool. However, attachments bypass line-range parameters and truncation limits since the user explicitly requested the full file. Images, PDFs, and audio are returned as inline data directly in the prompt.

**Tool-based reads:** The `read_file` tool uses the same `processSingleFileContent()` function but with additional:
- Line range support (`start_line`, `end_line`)
- Truncation to `DEFAULT_MAX_LINES_TEXT_FILE` lines
- JIT context discovery appended to output
- Telemetry (MIME type, programming language, line count)

#### Dirac

**User attachments:** Handled via `@mention` syntax in `mentions/index.ts`. Files referenced by the user are read via `extractTextFromFile()` which goes through the full format-specific pipeline (pdf-parse for PDFs, mammoth for DOCX, exceljs for XLSX, etc.). Binary files return `"(Binary file, unable to display content)"`. The content is wrapped in `<file_content path="...">` XML tags.

**Tool-based reads:** The `read_file` tool handler additionally:
- Applies hash anchors to every line (`hashLines()`)
- Tracks file content hash for change detection (`[File Hash: ...]`)
- Supports line ranges
- Adds file telemetry
- Has a 50KB full-read size limit

#### OpenCode / KiloCode

**User attachments:** Handled by the web app's `prompt-input/files.ts`. Uses `attachmentMime()` which:
- Accepts images (PNG, JPG, GIF, WebP) as `image/*`
- Accepts PDFs as `application/pdf`
- Converts text-like MIME types (JSON, XML, YAML, TOML) to `text/plain`
- **Rejects** unknown binaries (returns `undefined`)
- Uses content sampling (4KB) + null byte/non-printable ratio check

**Tool-based reads:** The `read` tool has its own independent `isBinaryFile()` function with the same heuristics, but instead of silently rejecting, it throws a hard error: `"Cannot read binary file: {filepath}"`. It also supports line offsets, line numbering, and 50KB truncation.

#### Codex (OpenAI)

**User attachments:** Codex's protocol doesn't fundamentally distinguish between user-provided and tool-called file content at the filesystem level. User-provided files in the chat/composer are typically uploaded via the **OpenAI file upload API** (`sediment://` URIs, handled in `codex-api/src/files.rs`). These uploaded files get a server-side URI that the model can reference.

**Tool-based reads:** The `read_file` tool returns raw base64 bytes. The model must decode and interpret them. There's no truncation, no line ranges, no formatting.

#### Pi (original, pi-mono)

**User attachments:** Handled via `@file` CLI arguments in `file-processor.ts`. This function:
- Detects images via MIME type → reads as Buffer → optionally resizes → returns as `ImageContent`
- Everything else → reads as UTF-8 text and appends to a text block wrapped in `<file name="...">` tags
- No binary detection — binary files silently produce garbage

**Tool-based reads:** The `read` tool similarly detects images but for text applies truncation (by lines or bytes), offset/limit, and continuation notices. No binary detection either.

#### oh-my-pi (fork)

Same pattern as Pi, with the addition of Jupyter notebook support in the edit pipeline's `readEditFileText()`.

### Summary Table

| Tool | User attachment handling | Tool-based read handling | Shared code? |
|------|------------------------|------------------------|:-----------:|
| **Gemini CLI** | `processSingleFileContent()` + at-command processing | Same function with truncation + line ranges + JIT context | **Yes** (same function) |
| **Dirac** | `extractTextFromFile()` in mentions pipeline | Same `extractTextFromFile()` + hash anchors + file hash tracking | **Partial** (base extraction shared) |
| **OpenCode** | `attachmentMime()` in web app | `isBinaryFile()` in read tool + line numbering + truncation | **No** (independent impl) |
| **Codex** | File upload API (`sediment://` URIs) | Raw base64 bytes via `read_file` | **No** (different paths) |
| **Pi** | `processFileArguments()` in CLI | `createReadTool()` with truncation | **No** (independent impl) |
| **oh-my-pi** | Same as Pi | `createReadToolDefinition()` + notebook support | **No** |

### Key Takeaway

The most coherent design is **Gemini CLI's** approach where both paths share the same file processing function but apply different post-processing (truncation, line ranges, formatting) based on context. Dirac partially shares the extraction layer but adds anchor/hash logic only on the tool path. OpenCode, Pi, and oh-my-pi maintain completely separate pipelines, which risks inconsistent behavior (e.g., a file that's accepted as an attachment but rejected by the read tool, or vice versa).

This architecture combines the best aspects of all studied tools:
- **Gemini CLI's** approach for models with native PDF/multimodal support (pass raw bytes, let API render)
- **Dirac's** format-specific extractors for models without native support (pdf-parse, mammoth, exceljs)
- **Codex's** raw base64 approach as a universal fallback (file upload APIs)
- A **capability registry** to automatically select the right strategy per model/provider

---

## Cross-Cutting Comparison

### Binary Detection Methods

| Tool | Extension-based | Null byte check | Non-printable ratio | External package | MIME sniffing | Charset detection |
|------|:--------------:|:--------------:|:-------------------:|:----------------:|:-------------:|:-----------------:|
| **Gemini CLI** | ✅ `BINARY_EXTENSIONS` | via `isbinaryfile` | via `isbinaryfile` | `isbinaryfile` | `mime/lite` | BOM UTF-8/16/32 |
| **Dirac** | ❌ (format dispatch) | via `isbinaryfile` | via `isbinaryfile` | `isbinaryfile` | ❌ | `jschardet` + `iconv-lite` |
| **OpenCode** | ✅ 28 extensions | ✅ custom | ✅ custom, >30% | ❌ | `sniffAttachmentMime` | ❌ |
| **Codex** | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Pi** | ❌ | ❌ | ❌ | ❌ | `detectImageMimeType` | ❌ |
| **oh-my-pi** | ❌ | ❌ | ❌ | ❌ | `detectImageMimeType` | ❌ |

### Format-Specific Extraction

| Format | Gemini CL | Dirac | OpenCode | Codex | Pi | oh-my-pi |
|--------|:---------:|:-----:|:--------:|:-----:|:--:|:--------:|
| **Images** | base64 inline | base64 block | base64 URI | raw base64 | base64 inline | base64 inline |
| **PDF** | base64 inline | pdf-parse | sniffed as file | raw base64 | ❌ | ❌ |
| **SVG** | text (1MB cap) | ❌ | ❌ | raw base64 | ❌ | ❌ |
| **Audio** | base64 inline (6 formats) | ❌ | ❌ | raw base64 | ❌ | ❌ |
| **Video** | base64 inline | ❌ | ❌ | raw base64 | ❌ | ❌ |
| **DOCX** | ❌ | mammoth | ❌ | raw base64 | ❌ | ❌ |
| **XLSX** | ❌ | exceljs | ❌ | raw base64 | ❌ | ❌ |
| **IPYNB** | ❌ | sanitized | ❌ | raw base64 | ❌ | ✅ editable |
| **Hex dump** | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |

### Size and Safety Limits

| Tool | Max file size | Max lines | Max line length | Max bytes | Content truncation |
|------|:-------------:|:---------:|:---------------:|:---------:|:-----------------:|
| **Gemini CLI** | ~50MB | 2000 | 2000 chars | — | Per-line truncation |
| **Dirac** | 20MB | — | — | 50KB (full read) | 400KB after extraction |
| **OpenCode** | — | 2000 | 2000 chars | 50KB | Line + byte truncation |
| **Codex** | — | — | — | — | None (base64 only) |
| **Pi** | — | configurable | — | configurable | Line + byte truncation |
| **oh-my-pi** | — | configurable | — | configurable | Line + byte truncation |

---

## 11. Design Space: What Binary File Handling Could Look Like

Based on the analysis of all 7 tools, here is the complete design space for binary file handling in agent CLIs:

### Level 0: No Detection (Pi, oh-my-pi)
- Read all files as UTF-8
- Binary files produce garbage in context
- Simplest to implement, worst user experience

### Level 1: Reject Binary (OpenCode)
- Detect binary via extension + content heuristics
- Return error message for binary files
- Images handled as special case
- No format-specific extraction

### Level 2: Format-Specific Extraction (Dirac)
- Separate extractors for PDF, DOCX, XLSX, IPYNB, images
- Charset detection + iconv fallback for unknown formats
- Content truncation to prevent context overflow
- File hash tracking for change detection

### Level 3: Full Classification + Multimodal (Gemini CLI)
- 7-way file type classification
- BOM-aware text reading (UTF-8/16/32)
- Images, PDF, audio, video → base64 inline data
- SVG → text with size cap
- Unknown binary → clear error message
- MIME type normalization

### Level 4: Raw Bytes Protocol (Codex)
- All files return as base64-encoded bytes
- No classification at the protocol level
- Maximum flexibility for the model
- No protection against context overflow

### Level 5: Hex View (Not Implemented Anywhere)
- Binary files displayed as structured hex dump
- ASCII representation alongside hex bytes
- Configurable offset/limit for large files
- Optional: magic byte annotations

---

## Recommendations

### If building binary file handling into an agent CLI:

1. **Always detect binary files before reading as text.** Use a combination of:
   - Null byte check (fast, reliable)
   - Non-printable character ratio (>30% is a common threshold)
   - Extension blacklist for known binary formats
   - The `isbinaryfile` npm package is a good off-the-shelf option

2. **Classify into at least 3 categories:**
   - **Text** — read as UTF-8 with line range and truncation
   - **Image** — base64 with MIME type (for vision-capable models)
   - **Binary** — error or hex dump (never silently read as UTF-8)

3. **Add BOM-aware reading** if supporting text files with non-UTF-8 BOM encodings (Gemini CLI's approach is the gold standard).

4. **Add format-specific extractors** if your use case includes documents (PDF, DOCX, XLSX). Dirac's approach is the best model here — separate handlers per format with a charset-detection fallback.

5. **Consider a hex view mode.** For binary file inspection, a hex dump option would let models:
   - Examine file headers (magic bytes, ELF/PE headers)
   - Read embedded strings
   - Debug encoding issues
   - Inspect serialized data formats
   
   A pragmatic approach: treat it as a special read mode (e.g., `read --binary file.bin --format hex`) rather than the default for all binary reads.

6. **Always set size limits.** A 50KB default max for full text reads, with line-range continuation, is a well-proven pattern used by multiple tools.

7. **Log file type telemetry.** Gemini CLI logs MIME type, programming language, and line count for every read — valuable for understanding usage patterns.

---

---

## 5. Claude Code — Minimalist Design

**Source:** `READ_ONLY/open-claude-code/v2/src/tools/read.mjs`, `edit.mjs`, `write.mjs`, `multi-edit.mjs`, `notebook-edit.mjs`

### Architecture

Claude Code takes the most **minimalist** approach of any tool studied. It has no hash anchors, no format-specific extractors, no model-capability awareness, and no binary classification beyond a simple null-byte check.

### File Types and Their Handling

| Type | Detection | Handling |
|------|-----------|----------|
| **Text** | Default (after binary check passes) | Read as UTF-8, line numbering (`cat -n` style), 2000-line default limit with offset/line continuation |
| **PDF** | Extension-based (`.pdf` suffix) | Shells out to `pdftotext` (poppler-utils). Falls back to message: `"[PDF file at {path} — pdftotext not available. Install poppler-utils for PDF support.]"` |
| **Binary** (non-PDF) | Null byte check in first 8KB | Error: `"{filePath} appears to be a binary file. Cannot display binary content."` |
| **Image** | Not detected at all | Falls through to null-byte check → binary error |
| **Directory** | `fs.statSync().isDirectory()` | Error: `"{path} is a directory, not a file. Use Bash with ls to list directory contents."` |

### PDF Handling

Claude Code's PDF handling is uniquely **shell-based**:

```javascript
function readPdf(filePath, pages) {
    const { execSync } = require('child_process');
    const pageArg = pages
        ? `-f ${pages.split('-')[0]} -l ${pages.split('-').pop()}`
        : '-f 1 -l 20';  // Default: first 20 pages
    const text = execSync(
        `pdftotext ${pageArg} "${filePath}" - 2>/dev/null`,
        { encoding: 'utf-8', timeout: 30000, maxBuffer: 1024 * 1024 }
    );
    return text || `[PDF file at ${filePath} — could not extract text. Use a PDF viewer.]`;
}
```

Key aspects:
- Uses **`pdftotext`** from poppler-utils — must be installed on the system
- Supports page ranges via the `pages` parameter (e.g., `"1-5"`)
- Default: first 20 pages only (limits context usage)
- Shell timeout: 30 seconds, max buffer: 1MB
- If `pdftotext` is not installed, returns a helpful message: `"Install poppler-utils for PDF support"`

This is the **only tool studied that uses a shell command for PDF extraction** (Dirac uses Node libraries like `pdf-parse` and `mammoth`; Gemini CLI and Codex rely on model APIs). It provides no image or audio handling at all.

### Binary Detection

```javascript
function isBinary(buffer) {
    const len = Math.min(buffer.length, 8192);
    for (let i = 0; i < len; i++) {
        if (buffer[i] === 0) return true;
    }
    return false;
}
```

Extremely simple: reads first 8KB, checks for any null byte. This is **less sophisticated** than OpenCode's approach (which also checks non-printable character ratio) and much simpler than Gemini CLI's MIME-based classification.

### Editing

Claude Code's editing approach is pure string replacement (no hash anchors):

- **`Edit` tool**: `{ file_path, old_string, new_string, replace_all? }` — exact string replacement with uniqueness check
- **`MultiEdit` tool**: Array of edits applied atomically (validated first, then written)
- **`NotebookEdit` tool**: `.ipynb` cell insert/replace/delete with JSON manipulation
- **Requires `Read` before `Edit`/`Write`**: The `read.mjs` module exports a `Set` of read file paths that the edit and write tools check. If a file hasn't been read, edit/write reject it.

### No User Attachment Pipeline

There is no separate user-attachment handling pipeline in the v2 source code. The UI layer (`repl.mjs`, `app.mjs`, `ink-app.mjs`) does not implement file drag-and-drop, `@file` mentions, or clipboard paste handling. Files enter the context exclusively through the model's `Read` tool calls.
## 13. Claude Code in the Comparison Tables

### Cross-Cutting Comparison (Updated)

| Tool | Extension-based | Null byte check | Non-printable ratio | External package | MIME sniffing | Charset detection |
|------|:--------------:|:--------------:|:-------------------:|:----------------:|:-------------:|:-----------------:|
| **Gemini CLI** | ✅ `BINARY_EXTENSIONS` | via `isbinaryfile` | via `isbinaryfile` | `isbinaryfile` | `mime/lite` | BOM UTF-8/16/32 |
| **Dirac** | ❌ (format dispatch) | via `isbinaryfile` | via `isbinaryfile` | `isbinaryfile` | ❌ | `jschardet` + `iconv-lite` |
| **Claude Code** | ❌ | ✅ custom (8KB scan) | ❌ | ❌ | ❌ | ❌ |
| **OpenCode** | ✅ 28 extensions | ✅ custom | ✅ custom, >30% | ❌ | `sniffAttachmentMime` | ❌ |
| **Codex** | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Pi** | ❌ | ❌ | ❌ | ❌ | `detectImageMimeType` | ❌ |
| **oh-my-pi** | ❌ | ❌ | ❌ | ❌ | `detectImageMimeType` | ❌ |

### Format-Specific Extraction (Updated)

| Format | Gemini CLI | Dirac | Claude Code | OpenCode | Codex | Pi | oh-my-pi |
|--------|:---------:|:-----:|:----------:|:--------:|:-----:|:--:|:--------:|
| **Images** | base64 inline | base64 block | ❌ | base64 URI | raw base64 | base64 inline | base64 inline |
| **PDF** | base64 inline | pdf-parse | pdftotext | sniffed as file | raw base64 | ❌ | ❌ |
| **SVG** | text (1MB cap) | ❌ | ❌ | ❌ | raw base64 | ❌ | ❌ |
| **Audio** | base64 inline (6 formats) | ❌ | ❌ | ❌ | raw base64 | ❌ | ❌ |
| **Video** | base64 inline | ❌ | ❌ | ❌ | raw base64 | ❌ | ❌ |
| **DOCX** | ❌ | mammoth | ❌ | ❌ | raw base64 | ❌ | ❌ |
| **XLSX** | ❌ | exceljs | ❌ | ❌ | raw base64 | ❌ | ❌ |
| **IPYNB** | ❌ | sanitized | ❌ | ❌ | raw base64 | ❌ | ✅ editable |
| **Hex dump** | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |

---

## Source References

All source material is in `READ_ONLY/`:

| Tool | Paths |
|------|-------|
| **Gemini CLI** | `gemini-cli/packages/core/src/utils/fileUtils.ts`, `tools/read-file.ts` |
| **Dirac** | `dirac/src/integrations/misc/extract-text.ts`, `extract-file-content.ts`, `core/task/tools/handlers/ReadFileToolHandler.ts` |
| **OpenCode/KiloCode** | `opencode/packages/opencode/src/tool/read.ts`, `packages/opencode/src/file/index.ts` |
| **Codex** | `codex/codex-rs/app-server/src/request_processors/fs_processor.rs`, `app-server-protocol/src/protocol/v2/fs.rs`, `codex-api/src/files.rs`, `core/src/tools/handlers/view_image.rs` |
| **Claude Code** | `open-claude-code/v2/src/tools/read.mjs`, `edit.mjs`, `write.mjs`, `multi-edit.mjs`, `notebook-edit.mjs` |
| **Pi (original)** | `pi-mono/packages/coding-agent/src/core/tools/read.ts` |
| **oh-my-pi** | `oh-my-pi/packages/coding-agent/src/tools/read.ts`, `edit/read-file.ts` |
| **Zed** | `zed/crates/language/src/file_content.rs` |
