using System.Text;

namespace Unextract.Gui.Models;

// Transfer format of the --entries file: raw FullNames, UTF-8 without BOM, one per line, LF-terminated.
// The limit mirrors the CLI's whole-file limit (docs/spec/cli.md#entries); the CLI keeps its own check.
internal static class EntriesFormat
{
    public const long MaxBytes = 128L * 1024 * 1024;
    // Invalid UTF-16 must fail rather than be silently replaced, which would change the approved FullName.
    public static readonly UTF8Encoding Encoding = new(false, throwOnInvalidBytes: true);

    // Returns false when a name cannot be written to UTF-8 exactly. Includes each line's LF.
    public static bool TryMeasure(IEnumerable<string> names, out long bytes)
    {
        bytes = 0;
        try
        {
            foreach (string name in names) bytes += Encoding.GetByteCount(name) + 1;
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
