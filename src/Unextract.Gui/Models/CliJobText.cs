namespace Unextract.Gui.Models;

// Process-level diagnostics for a failed job. CLI-supplied strings are converted for display only;
// no code or message here decides state.
internal static class CliJobText
{
    public static string Describe(CliJobResult result)
    {
        var lines = new List<string>
        {
            result.StartState switch
            {
                CliStartState.BeforeStartFailure => "CLIを起動できませんでした。",
                CliStartState.NotStarted => "OSがCLIプロセスを開始しませんでした。",
                CliStartState.Unknown => "CLIの起動成否を確認できません。",
                _ => "CLIを実行しました。",
            },
        };
        if (result.ProcessError is not null) lines.Add(DisplayText.Escape(result.ProcessError));
        if (result.ExitConfirmed) lines.Add("終了コード: " + result.ExitCode);
        else if (result.StartState == CliStartState.Started) lines.Add("CLIの実終了を確認できません。");
        if (result.StartState == CliStartState.Started && !(result.StdoutEof && result.StderrEof))
            lines.Add("出力を最後まで受信できていません。");
        var output = result.Output;
        if (output.Issue == ProtocolIssue.IncompatibleVersion)
            lines.Add("非互換な出力: " + DisplayText.Escape(output.IssueMessage ?? ""));
        else if (output.Issue == ProtocolIssue.InvalidOutput)
            lines.Add("出力異常: " + DisplayText.Escape(output.IssueMessage ?? ""));
        if (output.Result is { } r)
        {
            lines.Add($"result: outcome={DisplayText.Escape(r.Outcome)} exit_code={r.ExitCode}");
            if (result.ExitConfirmed && result.ExitCode != r.ExitCode)
                lines.Add($"終了コード({result.ExitCode})とresult.exit_code({r.ExitCode})が一致しません。終了コードを優先します。");
            if (r.Error is { } e)
            {
                var detail = new List<string> { $"stage={e.Stage}" };
                if (e.Step is not null) detail.Add($"step={e.Step}");
                detail.Add($"code={e.Code}");
                if (e.EntryIndex is not null) detail.Add($"entry_index={e.EntryIndex}");
                if (e.Win32Error is not null) detail.Add($"win32_error={e.Win32Error}");
                lines.Add("error: " + DisplayText.Escape(string.Join(" ", detail)));
                if (e.EntryName is not null) lines.Add("エントリ: " + DisplayText.Escape(e.EntryName));
                if (e.Message is not null) lines.Add(DisplayText.Escape(e.Message));
            }
        }
        else if (output.Issue == ProtocolIssue.None && result.ExitConfirmed)
            lines.Add("resultを受信できませんでした。");
        if (result.Stderr.Length != 0)
        {
            lines.Add("標準エラー出力（診断用）:");
            lines.AddRange(result.Stderr.Replace("\r", "").Split('\n').Select(DisplayText.Escape));
        }
        return string.Join("\n", lines);
    }
}
