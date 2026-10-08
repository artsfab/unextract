using Unextract.Core.Analysis;
using Unextract.Core.Display;
using Unextract.Core.Results;
using Unextract.Core.Zip;

namespace Unextract.Core.Commands;

// AfterResults はテスト用の差し込み口 (docs/TESTING.md#hooks: 結果表示の直後)。製品 CLI からは設定しない。
public sealed record AnalyzeCommandRequest(string ArchivePath, string TargetPath, RunMode Mode, CommandContext Context, Action? AfterResults = null);

// Analysis は Prepare の後の結果 (Prepare の入力エラー・ZIP を開けない場合は null)。PrepareError は Prepare の失敗の原因
// (entries の入力エラーでは null)。PreparationFailure は表示前の構造化された失敗情報。
public sealed record AnalyzeCommandOutcome(ExitStatus Status, AnalysisResult? Analysis, FatalError? PrepareError = null, PrepareFailure? PreparationFailure = null);

// unextract analyze (docs/spec/cli.md#arguments、docs/SPEC.md#execution、docs/spec/cli.md#analyze-output)。完全な非破壊操作で、削除用ハンドルを開かず何も削除しない。
// 削除の能力を型として持たない (IDeletionProbe を受け取らない)。結果は削除の許可証として保存・信用されない (delete は参照しない)。
public static class AnalyzeCommand
{
    public static AnalyzeCommandOutcome Run(AnalyzeCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = request.Context;
        var output = context.Output;
        var error = context.ErrorOutput;
        var notifications = context.Notifications;
        var format = ArchiveFormats.FromPath(request.ArchivePath);

        var (prepared, failure) = Preparation.Run(request.ArchivePath, request.TargetPath, entriesPath: null, request.Mode, context);
        if (prepared is null)
        {
            AnalysisResult? analysis = null;
            if (failure!.TargetFinalPath is { } targetFinalPath)
            {
                // target ルートを開いた後の FATAL (ZIP 全体の事前検査など): 結果表示 (判定済み 0、未判定) に至る。
                analysis = AnalysisResult.BeforeClassification(failure.TotalEntries!.Value, failure.Fatal!);
                if (notifications is null)
                {
                    WriteLines(output, ReportText.Header(request.ArchivePath, targetFinalPath, request.Mode));
                    WriteLines(output, AnalyzeOutput.Format(analysis, request.Mode, format));
                }
            }

            if (notifications is null)
            {
                error.WriteLine(failure.Message);
                error.WriteLine(AnalyzeOutput.FatalClosing);
                request.AfterResults?.Invoke();
            }
            return new AnalyzeCommandOutcome(ExitStatus.Error, analysis, failure.Fatal, failure);
        }

        using (prepared)
        {
            if (notifications is null)
            {
                WriteLines(output, ReportText.Header(request.ArchivePath, prepared.Root.FinalPath, request.Mode));
            }
            else
            {
                notifications.OnPrepared?.Invoke(prepared.Info(request.ArchivePath, request.Mode));
            }

            var contents = context.Contents?.Invoke(prepared.Source) ?? prepared.Source;
            var result = Analyzer.Run(new AnalyzeRequest(
                prepared.Targets,
                contents,
                context.Probe,
                prepared.Root,
                prepared.ArchiveIdentity,
                context.Limits,
                notifications is null ? context.Progress : null,
                request.Mode,
                notifications?.OnAnalysisResult,
                prepared.SessionFor(request.Mode)));

            if (notifications is null)
            {
                WriteLines(output, AnalyzeOutput.Format(result, request.Mode, format));
                WriteLines(error, AnalyzeOutput.FormatFatal(result));
                request.AfterResults?.Invoke();
            }
            return new AnalyzeCommandOutcome(result.Completed ? ExitStatus.Success : ExitStatus.Error, result);
        }
    }

    internal static void WriteLines(TextWriter writer, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            writer.WriteLine(line);
        }
    }
}
