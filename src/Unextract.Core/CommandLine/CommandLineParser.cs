namespace Unextract.Core.CommandLine;

public sealed record CommandLineOptions(string ArchivePath, string TargetPath, bool DryRun, bool AssumeYes);

public sealed record CommandLineParseResult(CommandLineOptions? Options, string? Error)
{
    public static CommandLineParseResult Ok(CommandLineOptions options) => new(options, null);

    public static CommandLineParseResult Fail(string error) => new(null, error);
}

// unextract <archive.zip> --target <dir> [--dry-run] [--yes|-y] (SPEC §2)。
// 副作用のない純粋関数。パスの存在や種類は検査しない。
public static class CommandLineParser
{
    public const string Usage = "使い方: unextract <archive.zip> --target <dir> [--dry-run] [--yes|-y]";

    public static CommandLineParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? archive = null;
        string? target = null;
        var dryRun = false;
        var yes = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--target":
                    if (target is not null)
                    {
                        return CommandLineParseResult.Fail("--target が複数回指定されています");
                    }

                    if (i + 1 >= args.Count || args[i + 1].StartsWith('-') || args[i + 1].Length == 0)
                    {
                        return CommandLineParseResult.Fail("--target の値がありません");
                    }

                    target = args[++i];
                    break;

                case "--dry-run":
                    if (dryRun)
                    {
                        return CommandLineParseResult.Fail("--dry-run が複数回指定されています");
                    }

                    dryRun = true;
                    break;

                case "--yes":
                case "-y":
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

        return CommandLineParseResult.Ok(new CommandLineOptions(archive, target, dryRun, yes));
    }
}
