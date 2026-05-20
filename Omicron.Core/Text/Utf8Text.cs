namespace Omicron.Core.Text
{
    /// <summary>
    /// Factory helpers for Omicron-owned UTF-8 types.
    /// </summary>
    public static class Utf8Text
    {
        /// <summary>
        /// Creates a new <see cref="Utf8Builder"/> with default (heap-backed) storage.
        /// </summary>
        public static Utf8Builder CreateBuilder()
        {
            return new Utf8Builder(false);
        }

        /// <summary>
        /// Creates a new <see cref="Utf8Builder"/> with optional thread-static scratch storage.
        /// Pass <c>true</c> for short-lived, non-nested builder usage on the same thread.
        /// </summary>
        public static Utf8Builder CreateBuilder(bool useThreadStaticScratch)
        {
            return new Utf8Builder(useThreadStaticScratch);
        }
    }
}
