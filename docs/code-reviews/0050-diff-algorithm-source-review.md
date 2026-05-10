# Code Review 0050: External Diff Algorithm Source Review

Date: 2026-05-08  
Scope: review of `READ_ONLY/Diff` as candidate implementation source for Plan 3 Phase 4 workspace text diffs.

## Source

```text
READ_ONLY/Diff/Diff.cs
READ_ONLY/Diff/README.md
READ_ONLY/Diff/LICENSE
READ_ONLY/Diff/TestCases.cs
```

The implementation is a C# port of Eugene Myers' O(ND) difference algorithm by Matthias Hertel.

License:

```text
BSD 3-Clause License
Copyright (c) 2023, Matthias Hertel
```

This license is permissive, but if code is copied/adapted into Omicron, retain the copyright/license notice in source or repository license documentation.

## What It Provides

`Diff.DiffText(oldText, newText, trimSpace, ignoreSpace, ignoreCase)` returns an array of diff hunks:

```csharp
public struct Item
{
    public int StartA;     // start index in old text lines
    public int StartB;     // start index in new text lines
    public int deletedA;   // number of old lines deleted/replaced
    public int insertedB;  // number of new lines inserted/replaced
}
```

This is enough to generate unified-ish text diffs for workspace transaction previews:

```text
--- path
+++ path
@@ -oldStart,oldCount +newStart,newCount @@
-old line
+new line
```

## Fit for Omicron

This is a better base than a naive line-by-line diff for Phase 4 because it gives stable, compact hunks for moved/inserted/deleted regions using a real Myers LCS-based algorithm.

Recommended use:

- adapt the algorithm into `Omicron.Core/Workspace/TextDiff.cs` or `Omicron.Core/Workspace/MyersDiff.cs`;
- expose an Omicron-shaped API rather than leaking the external class shape directly;
- keep the algorithm internal to the workspace diff implementation for now.

Suggested Omicron API:

```csharp
internal static class MyersTextDiff
{
    public static IReadOnlyList<TextDiffHunk> DiffLines(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines);
}

public sealed record TextDiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<TextDiffLine> Lines);

public enum TextDiffLineKind { Context, Removed, Added }

public sealed record TextDiffLine(TextDiffLineKind Kind, string Text);
```

For Phase 4 MVP, it is also acceptable to keep `TextDiffHunk` internal and return only rendered `TextDiff` text on `WorkspaceFileDiff`.

## Adaptation Notes

The source is older C# style:

- namespace is `my.utils`;
- uses `Hashtable` / `ArrayList`;
- mutable public struct fields;
- method naming is Pascal-ish but not Omicron style for fields;
- uses `Regex.Replace` for optional whitespace normalization;
- text input path splits strings internally.

Recommended modernization if copying:

- use `namespace Omicron.Core.Workspace;`;
- make implementation `internal static` unless public API is needed;
- use `Dictionary<string, int>` instead of `Hashtable`;
- use `List<DiffItem>` instead of `ArrayList`;
- use records/readonly structs for returned operations;
- accept `IReadOnlyList<string>` or `string[]` lines directly to avoid extra split/normalize where caller already has lines;
- keep optional whitespace/case normalization out of Phase 4 unless needed.

## Line Ending Behavior

Original code normalizes by removing `\r` and splitting on `\n`:

```csharp
aText = aText.Replace("\r", "");
Lines = aText.Split('\n');
```

This is consistent enough with current workspace read logic, which normalizes CRLF to LF before splitting. Preserve intentional behavior in tests, especially trailing-newline handling.

## Recommended Phase 4 Use

Implement transaction diff generation as:

1. decode old/new bytes as UTF-8 for non-binary files;
2. normalize CRLF to LF;
3. split into lines;
4. call adapted Myers line diff;
5. render a compact unified-ish diff string.

Do not use this algorithm for binary files. For binary staged changes, set `IsBinary = true` and omit `TextDiff` or use a short metadata message.

## Tests to Port/Add

Port core self-tests from `READ_ONLY/Diff/TestCases.cs` into xUnit around the adapted diff operation:

- all changes;
- all same;
- snake;
- historical repro cases;
- repeated-line case.

Also add Omicron-specific render tests:

- modified file diff includes `--- path`, `+++ path`, `-old`, `+new`;
- added file diff;
- deleted file diff;
- trailing newline behavior;
- repeated lines produce stable hunks.

## Recommendation

Use this as the basis for Phase 4 text diffs. Do not import it verbatim without namespacing/style cleanup and license attribution. Keep it internal and wrapped behind Omicron workspace diff records so it can be replaced later without changing transaction APIs.
