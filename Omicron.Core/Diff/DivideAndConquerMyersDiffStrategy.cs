namespace Omicron.Core.Diff;

/// <summary>
///     Divide-and-conquer Myers diff strategy.
///     Finds the middle snake and recurses, avoiding full trace storage.
/// </summary>
internal sealed class DivideAndConquerMyersDiffStrategy : ITextDiffStrategy
{
    public List<TextDiffEdit>? Compute(int[] oldCodes, int oldLen, int[] newCodes, int newLen)
    {
        var edits = new List<TextDiffEdit>();
        BuildEdits(oldCodes, newCodes, 0, oldLen, 0, newLen, edits);
        return edits;
    }

    private static void BuildEdits(
        int[] oldArr,
        int[] newArr,
        int oldLo,
        int oldHi,
        int newLo,
        int newHi,
        List<TextDiffEdit> edits)
    {
        // Trim common prefix
        while (oldLo < oldHi && newLo < newHi && oldArr[oldLo] == newArr[newLo])
        {
            oldLo++;
            newLo++;
        }

        // Trim common suffix
        while (oldLo < oldHi && newLo < newHi && oldArr[oldHi - 1] == newArr[newHi - 1])
        {
            oldHi--;
            newHi--;
        }

        int oldLen = oldHi - oldLo;
        int newLen = newHi - newLo;

        if (oldLen == 0 && newLen == 0)
        {
            return;
        }

        if (oldLen == 0)
        {
            edits.Add(new TextDiffEdit(oldLo, 0, newLo, newLen));
            return;
        }

        if (newLen == 0)
        {
            edits.Add(new TextDiffEdit(oldLo, oldLen, newLo, 0));
            return;
        }

        (int midOld, int midNew) = FindMiddleSnake(oldArr, newArr, oldLo, oldHi, newLo, newHi);

        // Clamp midpoint to valid range
        midOld = Math.Clamp(midOld, oldLo, oldHi);
        midNew = Math.Clamp(midNew, newLo, newHi);

        // Safety: if no progress on either side, treat as replacement
        if ((midOld == oldLo && midNew == newLo) || (midOld == oldHi && midNew == newHi))
        {
            edits.Add(new TextDiffEdit(oldLo, oldLen, newLo, newLen));
            return;
        }

        BuildEdits(oldArr, newArr, oldLo, midOld, newLo, midNew, edits);
        BuildEdits(oldArr, newArr, midOld, oldHi, midNew, newHi, edits);
    }

    /// <summary>
    ///     Find the middle snake using Myers' bidirectional algorithm.
    ///     Forward and backward passes each step by 2, preserving the parity of D.
    /// </summary>
    private static (int midOld, int midNew) FindMiddleSnake(
        int[] oldArr,
        int[] newArr,
        int oldLo,
        int oldHi,
        int newLo,
        int newHi)
    {
        int oldLen = oldHi - oldLo;
        int newLen = newHi - newLo;
        int maxD = (oldLen + newLen + 1) / 2;
        int delta = oldLen - newLen;
        int kMax = maxD + Math.Abs(delta);
        int vSize = 2 * kMax + 5; // ample margin
        int offset = kMax + 2; // center at k=0

        int[] fwdV = new int[vSize];
        int[] revV = new int[vSize];
        // Initialize with -1 (unreachable)
        for (int i = 0; i < vSize; i++)
        {
            fwdV[i] = -1;
            revV[i] = -1;
        }

        fwdV[1 + offset] = oldLo; // D=0, k=1 in forward coordinates
        revV[1 + offset] = oldHi; // D=0, k=1 in reverse coordinates

        for (int D = 0; D <= maxD; D++)
        {
            // Forward pass — diagonals have parity of D
            for (int k = -D; k <= D; k += 2)
            {
                int kIdx = k + offset;

                int x;
                if (k == -D || (k != D && fwdV[k - 1 + offset] < fwdV[k + 1 + offset]))
                {
                    x = fwdV[k + 1 + offset]; // vertical move (from k+1)
                }
                else
                {
                    x = fwdV[k - 1 + offset] + 1; // horizontal move (from k-1)
                }

                int y = x - k;
                int x0 = x,
                    y0 = y;

                // Follow diagonal snake
                while (x < oldHi && y < newHi && oldArr[x] == newArr[y])
                {
                    x++;
                    y++;
                }

                fwdV[kIdx] = x;

                // Check overlap with reverse pass (when D and delta have opposite parity)
                if ((delta & 1) == 1) // delta is odd
                {
                    int revK = k - delta;
                    int revIdx = revK + offset;
                    if (revIdx >= 0 && revIdx < vSize && revV[revIdx] != -1 && revV[revIdx] <= x)
                    {
                        return (x0, y0);
                    }
                }
            }

            // Backward pass
            for (int k = -D; k <= D; k += 2)
            {
                int kIdx = k + offset;

                int x;
                if (k == -D || (k != D && revV[k - 1 + offset] > revV[k + 1 + offset]))
                {
                    x = revV[k - 1 + offset] - 1; // horizontal reverse (from k-1)
                }
                else
                {
                    x = revV[k + 1 + offset]; // vertical reverse (from k+1)
                }

                int y = x - k;

                // Follow diagonal backward
                while (x > oldLo && y > newLo && oldArr[x - 1] == newArr[y - 1])
                {
                    x--;
                    y--;
                }

                revV[kIdx] = x;

                // Check overlap with forward pass (when delta is even)
                if ((delta & 1) == 0) // delta is even
                {
                    int fwdK = k + delta;
                    int fwdIdx = fwdK + offset;
                    if (fwdIdx >= 0 && fwdIdx < vSize && fwdV[fwdIdx] != -1 && fwdV[fwdIdx] >= x)
                    {
                        return (x, y);
                    }
                }
            }
        }

        // Return clamped bounds as fallback
        return (Math.Clamp(oldLo, oldLo, oldHi), Math.Clamp(newLo, newLo, newHi));
    }
}
