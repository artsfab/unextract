using Unextract.Core.Analysis;
using Unextract.Core.Deletion;
using Unextract.Core.Display;
using Unextract.Core.Results;
using Unextract.Core.Target;

namespace Unextract.Core.Commands;

// AwaitingConfirmation と Hooks はテスト用の差し込み口 (docs/TESTING.md#hooks: 確認プロンプトの直前、H1〜H5)。製品 CLI からは設定しない。
// DeletionStarting は逐次処理開始の通知。CLI の最上位の例外処理で、開始前と開始後を区別するために使う。
public sealed record DeleteCommandRequest(
    string ArchivePath,
    string TargetPath,
    RunMode Mode,
    string? EntriesPath,
    bool AssumeYes,
    IDeletionProbe DeletionProbe,
    IConfirmationPrompt Prompt,
    CommandContext Context,
    Action? AwaitingConfirmation = null,
    DeleteHooks? Hooks = null,
    Action? DeletionStarting = null);

// Report は逐次処理の結果 (Prepare の失敗・確認での中止では null)。PrepareError は Prepare の失敗の原因
// (entries の入力エラーでは null。原因は標準エラー出力に書く)。
public sealed record DeleteCommandOutcome(ExitStatus Status, DeleteReport? Report, FatalError? PrepareError = null);

// unextract delete (docs/spec/cli.md#arguments、docs/SPEC.md#execution、docs/spec/filesystem.md#delete-flow、docs/spec/cli.md#delete-output)。Prepare の後に1回だけ確認し (案 A、docs/RATIONALE.md#confirmation)、処理対象を ZIP の順に1件ずつ、
// その時点の target の状態で検証してその場で削除する。analyze の結果は参照しない。全件の事前解析をしない。
public static class DeleteCommand
{
    public static DeleteCommandOutcome Run(DeleteCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = request.Context;
        var output = context.Output;
        var error = context.ErrorOutput;

        // Prepare: どの段階の失敗でも削除0件。確認も出さない。
        var (prepared, failure) = Preparation.Run(request.ArchivePath, request.TargetPath, request.EntriesPath, context);
        if (prepared is null)
        {
            error.WriteLine(failure!.Message);
            error.WriteLine(DeleteOutput.PrepareAborted);
            return new DeleteCommandOutcome(ExitStatus.Error, null, failure.Fatal);
        }

        var deletionStarted = false;
        DeleteReport? report = null;
        var errorLinesWritten = 0;

        void WriteRemainingErrors(DeleteReport result)
        {
            var lines = DeleteOutput.Errors(result);
            while (errorLinesWritten < lines.Count)
            {
                error.WriteLine(lines[errorLinesWritten]);
                // 書き込みが成功した行だけを記録し、報告途中や Dispose の失敗後に再表示しない。
                errorLinesWritten++;
            }
        }

        try
        {
            using (prepared)
            {
                AnalyzeCommand.WriteLines(output, ReportText.Header(request.ArchivePath, prepared.Root.FinalPath, request.Mode));
                output.WriteLine(DeleteOutput.Targets(prepared.Entries.Count, prepared.Targets.Count, prepared.EntriesSelected));

                // 確認 (案 A): Prepare が全て成功した後、最初の target エントリの処理の前に1回だけ。確認待ちの間、個々の target ファイルの
                // ハンドルは開いていない (開いているのは ZIP と target ルートだけ)。--yes は確認だけを省略する。
                if (!request.AssumeYes && !Confirm(request, prepared.Targets.Count(e => !e.IsDirectory)))
                {
                    return new DeleteCommandOutcome(ExitStatus.UserCancelled, null);
                }

                output.WriteLine(ReportText.Heading);
                // 最上位の例外処理にも開始済みであることを通知する。出力・Dispose の失敗でも削除0件とは断定しない。
                deletionStarted = true;
                request.DeletionStarting?.Invoke();
                report = SequentialDeleter.Run(new DeleteRequest(
                    prepared.Targets,
                    context.Contents?.Invoke(prepared.Source) ?? prepared.Source,
                    context.Probe,
                    request.DeletionProbe,
                    prepared.Root,
                    prepared.ArchiveIdentity,
                    context.Limits,
                    request.Mode,
                    context.Progress,
                    result => output.WriteLine(DeleteOutput.Line(result)),
                    request.Hooks));

                AnalyzeCommand.WriteLines(output, DeleteOutput.Summary(report, prepared.Entries.Count - prepared.Targets.Count));
                WriteRemainingErrors(report);

                // STOP、または STOP がなくても DELETE_FAILED が1件以上あればエラー (docs/spec/cli.md#arguments、docs/RATIONALE.md#open-failures)。
                var status = report.Stop is null && report.Count(DeleteStatus.DeleteFailed) == 0 ? ExitStatus.Success : ExitStatus.Error;
                return new DeleteCommandOutcome(status, report);
            }
        }
        catch (Exception ex) when (deletionStarted)
        {
            // 結果・要約の表示、終了時の Dispose も含む。report が無ければ削除件数は不明。
            error.WriteLine($"内部エラー: 想定外の例外が発生しました ({ex.GetType().Name}: {SafeDisplay.Escape(ex.Message)})");
            error.WriteLine(report is null
                ? "逐次処理の途中で中止しました。それまでに削除したファイルは元に戻りません。"
                : $"逐次処理後の報告または終了処理に失敗しました (削除済み {report.Count(DeleteStatus.Deleted)} 件)。削除したファイルは元に戻りません。");
            if (report is not null)
            {
                WriteRemainingErrors(report);
            }
            return new DeleteCommandOutcome(ExitStatus.Error, report);
        }
    }

    // 確認入力は y / Y だけを開始とする。空入力、EOF、その他は中止。非対話で --yes がなければ中止 (docs/spec/cli.md#arguments)。
    public static bool IsConfirmation(string? answer) => answer is "y" or "Y";

    private static bool Confirm(DeleteCommandRequest request, int fileEntries)
    {
        var output = request.Context.Output;
        if (!request.Prompt.IsInteractive)
        {
            output.WriteLine(DeleteOutput.NotInteractive);
            output.WriteLine(DeleteOutput.Cancelled);
            return false;
        }

        request.AwaitingConfirmation?.Invoke();
        if (IsConfirmation(request.Prompt.Ask(DeleteOutput.ConfirmationPrompt(fileEntries, request.Mode))))
        {
            return true;
        }

        output.WriteLine(DeleteOutput.Cancelled);
        return false;
    }
}
