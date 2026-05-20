namespace Omicron.Core.Diff;

/// <summary>
///     Normalizes TextDiffEdit lists: merges adjacent delete+insert pairs
///     into single replacement edits for cleaner output.
/// </summary>
internal static class TextDiffEditNormalizer
{
    /// <summary>
    ///     Normalize an edit list, running multiple passes until stable.
    ///     Merges adjacent delete+insert and insert+delete pairs into single replacement edits,
    ///     and merges adjacent edits of the same position into replacements.
    /// </summary>
    public static List<TextDiffEdit> Normalize(List<TextDiffEdit> edits)
    {
        if (edits.Count <= 1)
        {
            return edits;
        }

        bool changed;
        do
        {
            changed = false;
            var result = new List<TextDiffEdit>();
            int i = 0;
            while (i < edits.Count)
            {
                if (i + 1 < edits.Count && CanMerge(edits[i], edits[i + 1], out TextDiffEdit merged))
                {
                    result.Add(merged);
                    i += 2;
                    changed = true;
                }
                else if (
                    i + 2 < edits.Count
                    && edits[i].IsInsert
                    && edits[i + 1].IsDelete
                    && edits[i + 2].IsInsert
                    && edits[i].OldStart == edits[i + 1].OldStart
                    && edits[i + 1].OldStart + edits[i + 1].OldCount == edits[i + 2].OldStart
                    && edits[i].NewStart + edits[i].NewCount == edits[i + 1].NewStart
                    && edits[i + 1].NewStart + edits[i + 1].NewCount == edits[i + 2].NewStart
                )
                {
                    // Merge insert+delete+insert into a single replace
                    result.Add(new TextDiffEdit(edits[i].OldStart,
                        edits[i + 1].OldCount,
                        edits[i].NewStart,
                        edits[i].NewCount + edits[i + 2].NewCount));
                    i += 3;
                    changed = true;
                }
                else
                {
                    result.Add(edits[i]);
                    i++;
                }
            }

            edits = result;
        } while (changed && edits.Count > 1);

        return edits;
    }

    private static bool CanMerge(TextDiffEdit a, TextDiffEdit b, out TextDiffEdit merged)
    {
        // delete + insert/replace (delete first, then insert/replace starts at same new position)
        if (a.IsDelete && a.OldStart + a.OldCount == b.OldStart && a.NewStart == b.NewStart)
        {
            merged = new TextDiffEdit(a.OldStart, a.OldCount + b.OldCount, a.NewStart, b.NewCount);
            return true;
        }

        // insert + delete/replace (insert first, then delete/replace starts at same old position)
        if (a.IsInsert && a.OldStart == b.OldStart && a.NewStart + a.NewCount == b.NewStart)
        {
            merged = new TextDiffEdit(a.OldStart, b.OldCount, a.NewStart, a.NewCount + b.NewCount);
            return true;
        }

        merged = default;
        return false;
    }
}
