namespace Omicron.Core.Diff;

/// <summary>
/// Classic trace-based Myers O(ND) diff strategy.
/// Good for small/medium inputs. Stores full V-array snapshots for each D level.
/// </summary>
internal sealed class TraceMyersDiffStrategy : ITextDiffStrategy
{
    public List<TextDiffEdit>? Compute(int[] oldCodes, int oldLen, int[] newCodes, int newLen)
    {
        int maxD = oldLen + newLen;
        int[] V = new int[2 * maxD + 1];
        int offset = maxD;
        var trace = new List<int[]>();

        for (int D = 0; D <= maxD; D++)
        {
            var snapshot = new int[V.Length];
            Array.Copy(V, snapshot, V.Length);
            trace.Add(snapshot);

            for (int k = -D; k <= D; k += 2)
            {
                int x;
                if (k == -D || (k != D && V[k - 1 + offset] < V[k + 1 + offset]))
                    x = V[k + 1 + offset];
                else
                    x = V[k - 1 + offset] + 1;

                int y = x - k;
                while (x < oldLen && y < newLen && oldCodes[x] == newCodes[y])
                {
                    x++; y++;
                }

                V[k + offset] = x;
                if (x >= oldLen && y >= newLen)
                    return ReconstructPath(trace, D, oldLen, newLen, offset);
            }
        }

        return null;
    }

    private static List<TextDiffEdit> ReconstructPath(
        List<int[]> trace, int maxD, int N, int M, int offset)
    {
        int x = N, y = M;
        var edits = new List<(int oldPos, int newPos, bool isInsert)>();

        for (int D = maxD; D > 0; D--)
        {
            var V = trace[D];
            int k = x - y;

            bool fromDown;
            if (k == -D || (k != D && V[k - 1 + offset] < V[k + 1 + offset]))
                fromDown = true;
            else
                fromDown = false;

            int prevK = fromDown ? k + 1 : k - 1;
            int prevX = V[prevK + offset];
            int prevY = prevX - prevK;

            while (x > prevX && y > prevY) { x--; y--; }
            if (D == 0) break;

            if (x == prevX) { y--; edits.Add((x, y, true)); }
            else { x--; edits.Add((x, y, false)); }
        }

        edits.Reverse();
        return MergeToEdits(edits);
    }

    internal static List<TextDiffEdit> MergeToEdits(List<(int oldPos, int newPos, bool isInsert)> path)
    {
        var result = new List<TextDiffEdit>();
        int i = 0;
        while (i < path.Count)
        {
            var (op, np, isIns) = path[i];
            if (isIns)
            {
                int count = 1;
                while (i + count < path.Count && path[i + count].oldPos == op && path[i + count].isInsert)
                    count++;
                result.Add(new TextDiffEdit(op, 0, np, count));
                i += count;
            }
            else
            {
                int count = 1;
                while (i + count < path.Count && !path[i + count].isInsert && path[i + count].newPos == np)
                    count++;
                result.Add(new TextDiffEdit(op, count, np, 0));
                i += count;
            }
        }

        return result;
    }
}
