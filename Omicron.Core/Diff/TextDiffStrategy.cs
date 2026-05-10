namespace Omicron.Core.Diff;

/// <summary>
/// Strategy interface for exact line-based diff algorithms.
/// Each strategy produces a minimal edit script from two integer-code arrays.
/// </summary>
internal interface ITextDiffStrategy
{
    /// <summary>
    /// Compute the edit script for the given code arrays.
    /// Returns null if this strategy cannot or declines to handle the input.
    /// </summary>
    List<TextDiffEdit>? Compute(
        int[] oldCodes, int oldLen,
        int[] newCodes, int newLen);
}
