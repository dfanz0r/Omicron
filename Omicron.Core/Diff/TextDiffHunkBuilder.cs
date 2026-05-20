namespace Omicron.Core.Diff;

/// <summary>
///     Builds hunks from an edit script and original lines.
/// </summary>
internal static class TextDiffHunkBuilder
{
    public static IReadOnlyList<TextDiffHunk> BuildHunks(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines,
        TextDiffResult diff,
        int contextLines = 3)
    {
        if (!diff.HasChanges)
        {
            return Array.Empty<TextDiffHunk>();
        }

        var hunks = new List<TextDiffHunk>();
        int editIdx = 0;

        while (editIdx < diff.Edits.Count)
        {
            TextDiffEdit firstEdit = diff.Edits[editIdx];

            // Determine hunk bounds with context
            int hunkOldStart = Math.Max(0, firstEdit.OldStart - contextLines);
            int hunkNewStart = Math.Max(0, firstEdit.NewStart - contextLines);

            // Collect all edits that fall within merged context range
            var hunkEdits = new List<TextDiffEdit>
            {
                firstEdit
            };
            editIdx++;

            while (editIdx < diff.Edits.Count)
            {
                TextDiffEdit prev = hunkEdits[^1];
                TextDiffEdit next = diff.Edits[editIdx];

                int gapOld = next.OldStart - (prev.OldStart + prev.OldCount);
                int gapNew = next.NewStart - (prev.NewStart + prev.NewCount);

                if (gapOld <= 2 * contextLines && gapNew <= 2 * contextLines)
                {
                    hunkEdits.Add(next);
                    editIdx++;
                }
                else
                {
                    break;
                }
            }

            // Calculate hunk end bounds
            TextDiffEdit lastEdit = hunkEdits[^1];
            int hunkOldEnd = Math.Min(oldLines.Count,
                lastEdit.OldStart + lastEdit.OldCount + contextLines);
            int hunkNewEnd = Math.Min(newLines.Count,
                lastEdit.NewStart + lastEdit.NewCount + contextLines);

            // Build hunk lines by walking old and new ranges
            int li = hunkOldStart,
                lj = hunkNewStart;
            int hei = 0; // index into hunkEdits
            var hunkLines = new List<TextDiffLine>();

            while (li < hunkOldEnd || lj < hunkNewEnd)
            {
                // Determine if current positions are within an edit
                bool inEdit =
                    hei < hunkEdits.Count
                    && li >= hunkEdits[hei].OldStart
                    && li < hunkEdits[hei].OldStart + hunkEdits[hei].OldCount;

                bool inNewEdit =
                    hei < hunkEdits.Count
                    && lj >= hunkEdits[hei].NewStart
                    && lj < hunkEdits[hei].NewStart + hunkEdits[hei].NewCount;

                if (inEdit || inNewEdit)
                {
                    TextDiffEdit edit = hunkEdits[hei];

                    // Emit all removed lines for this edit
                    for (int r = 0; r < edit.OldCount; r++)
                    {
                        hunkLines.Add(new TextDiffLine(TextDiffLineKind.Removed, li + 1, null, oldLines[li]));
                        li++;
                    }

                    // Emit all added lines for this edit
                    for (int a = 0; a < edit.NewCount; a++)
                    {
                        hunkLines.Add(new TextDiffLine(TextDiffLineKind.Added, null, lj + 1, newLines[lj]));
                        lj++;
                    }

                    hei++;
                }
                else
                {
                    // Context line (same in both)
                    if (li < oldLines.Count && lj < newLines.Count)
                    {
                        hunkLines.Add(new TextDiffLine(TextDiffLineKind.Context, li + 1, lj + 1, oldLines[li]));
                        li++;
                        lj++;
                    }
                    else
                    {
                        // Shouldn't happen, but guard
                        if (li < hunkOldEnd)
                        {
                            li++;
                        }

                        if (lj < hunkNewEnd)
                        {
                            lj++;
                        }
                    }
                }
            }

            int oldCount = hunkOldEnd - hunkOldStart;
            int newCount = hunkNewEnd - hunkNewStart;

            hunks.Add(new TextDiffHunk(hunkOldStart + 1, oldCount, hunkNewStart + 1, newCount, hunkLines));
        }

        return hunks;
    }
}
