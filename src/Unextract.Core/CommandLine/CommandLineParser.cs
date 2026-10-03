using Unextract.Core.Analysis;

namespace Unextract.Core.CommandLine;

public enum CommandKind
{
    Analyze,
    Delete,
}

// EntriesPath と AssumeYes は delete だけ (analyze では常に null / false)。
public sealed record CommandLineOptions(CommandKind Command, string ArchivePath, string TargetPath, RunMode Mode, string? EntriesPath, bool AssumeYes);

public sealed record CommandLineParseResult(CommandLineOptions? Options, string? Error)
{
    public static CommandLineParseResult Ok(CommandLineOptions options) => new(options, null);

    public static CommandLineParseResult Fail(string error) => new(null, error);
}

// unextract analyze <archive.zip> --target <dir> [--fast]
// unextract delete  <archive.zip> --target <dir> [--fast] [--entries <file>] [--yes|-y]   (docs/spec/cli.md#arguments)
// 副作用のない純粋関数。パスの存在や種類は検査しない。旧形式 (サブコマンドなし) と --dry-run は入力エラーとし、互換動作を設けない (docs/RATIONALE.md#confirmation)。
public static class CommandLineParser
{
    public static readonly IReadOnlyList<string> UsageLines =
    [
        "使い方: unextract analyze <archive.zip> --target <dir> [--fast]",
        "        unextract delete <archive.zip> --target <dir> [--fast] [--entries <file>] [--yes|-y]",
    ];

    public const string DryRunRemoved = "--dry-run は廃止しました。削除せずに結果を確認するには unextract analyze を使ってください。";

    public const string SubcommandRequired =
        "サブコマンド (analyze または delete) を指定してください。旧形式 (unextract <archive.zip> --target <dir>) は廃止しました。";

    public static CommandLineParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // --dry-run はどの位置にあっても入力エラー (サブコマンドの有無より先に案内する)。
        if (args.Contains("--dry-run"))
        {
            return CommandLineParseResult.Fail(DryRunRemoved);
        }

        // サブコマンドは小文字の完全一致だけ。無い (旧形式を含む)、不明、大小文字違いは入力エラー。
        CommandKind command;
        switch (args.Count > 0 ? args[0] : null)
        {
            case "analyze":
                command = CommandKind.Analyze;
                break;
            case "delete":
                command = CommandKind.Delete;
                break;
            default:
                return CommandLineParseResult.Fail(SubcommandRequired);
        }

        string? archive = null;
        string? target = null;
        string? entries = null;
        var fast = false;
        var yes = false;

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--target":
                    if (target is not null)
                    {
                        return CommandLineParseResult.Fail("--target が複数回指定されています");
                    }

                    if (!HasValue(args, i))
                    {
                        return CommandLineParseResult.Fail("--target の値がありません");
                    }

                    target = args[++i];
                    break;

                case "--fast":
                    if (fast)
                    {
                        return CommandLineParseResult.Fail("--fast が複数回指定されています");
                    }

                    fast = true;
                    break;

                case "--entries":
                    if (command == CommandKind.Analyze)
                    {
                        return CommandLineParseResult.Fail("analyze では --entries を指定できません");
                    }

                    if (entries is not null)
                    {
                        return CommandLineParseResult.Fail("--entries が複数回指定されています");
                    }

                    if (!HasValue(args, i))
                    {
                        return CommandLineParseResult.Fail("--entries の値がありません");
                    }

                    entries = args[++i];
                    break;

                case "--yes":
                case "-y":
                    if (command == CommandKind.Analyze)
                    {
                        return CommandLineParseResult.Fail($"analyze では {arg} を指定できません");
                    }

                    if (yes)
                    {
                        return CommandLineParseResult.Fail("--yes が複数回指定されています");
                    }

                    yes = true;
                    break;

                default:
                    if (arg.StartsWith('-'))
                    {
                        return CommandLineParseResult.Fail($"不明なオプションです: {arg}");
                    }

                    if (arg.Length == 0)
                    {
                        return CommandLineParseResult.Fail("ZIP のパスが空です");
                    }

                    if (archive is not null)
                    {
                        return CommandLineParseResult.Fail("ZIP は1つだけ指定できます");
                    }

                    archive = arg;
                    break;
            }
        }

        if (archive is null)
        {
            return CommandLineParseResult.Fail("ZIP のパスがありません");
        }

        if (target is null)
        {
            return CommandLineParseResult.Fail("--target は必須です");
        }

        return CommandLineParseResult.Ok(new CommandLineOptions(command, archive, target, fast ? RunMode.Fast : RunMode.Strict, entries, yes));
    }

    // オプションの値は次の引数。無い、空、- で始まる場合は値なしとする。
    private static bool HasValue(IReadOnlyList<string> args, int i) =>
        i + 1 < args.Count && args[i + 1].Length > 0 && !args[i + 1].StartsWith('-');
}
