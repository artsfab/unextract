using System.IO;
using Unextract.Gui.Models;

namespace Unextract.Gui.Services;

internal sealed class CliLocation
{
    public string ExecutablePath { get; }

    public CliLocation(string baseDirectory)
    {
        ExecutablePath = Path.GetFullPath(Path.Combine(baseDirectory, "cli", "unextract.exe"));
    }

    public CliAvailability Check()
    {
        bool available = File.Exists(ExecutablePath);
        return new CliAvailability(ExecutablePath, available, available
            ? "同梱CLIを使用します。"
            : "同梱CLIが見つかりません。配布フォルダー全体を展開し、cli\\unextract.exe を配置してください。解析・削除を開始できません。");
    }
}
