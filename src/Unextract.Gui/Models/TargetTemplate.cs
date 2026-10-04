using System.IO;
using System.Text;

namespace Unextract.Gui.Models;

internal static class TargetTemplate
{
    public const string SameDirectory = "{{archive.dir}}";
    public const string ArchiveDirectory = "{{archive.dir}}\\{{archive.name}}";

    public static string Resolve(string template, string archivePath)
    {
        var resolved = new StringBuilder();
        for (int i = 0; i < template.Length;)
        {
            if (template.AsSpan(i).StartsWith("{{", StringComparison.Ordinal))
            {
                int end = template.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new ArgumentException("テンプレートの変数が閉じていません。");
                string variable = template[i..(end + 2)];
                string value = variable switch
                {
                    "{{archive.dir}}" => Path.GetDirectoryName(archivePath) ?? "",
                    "{{archive.name}}" => Path.GetFileNameWithoutExtension(archivePath),
                    _ => throw new ArgumentException($"未知の変数です: {variable}"),
                };
                if (value.Length == 0) throw new ArgumentException("Archiveの変数が空になります。");
                resolved.Append(value);
                i = end + 2;
            }
            else
            {
                if (template.AsSpan(i).StartsWith("}}", StringComparison.Ordinal))
                    throw new ArgumentException("テンプレートに対応しない閉じ括弧があります。");
                resolved.Append(template[i++]);
            }
        }
        string path = resolved.ToString();
        ValidatePath(path);
        return path;
    }

    // This is input-format checking, not CLI target validation or filesystem identity.
    private static void ValidatePath(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
            throw new ArgumentException("Targetはドライブ文字から始まる絶対パスで指定してください。");
        foreach (string part in path[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..") throw new ArgumentException("Targetに . または .. の成分は指定できません。");
            if (part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
                throw new ArgumentException("TargetにWindowsで無効な名前があります。");
            string stem = part.Split('.')[0].TrimEnd(' ');
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                    stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                    "0123456789\u00B9\u00B2\u00B3".Contains(stem[3])))
                throw new ArgumentException("TargetにWindowsの予約名があります。");
        }
    }

    public static string DuplicateKey(string resolvedPath)
    {
        ValidatePath(resolvedPath);
        var parts = resolvedPath[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return resolvedPath[..2] + "\\" + string.Join('\\', parts);
    }
}
