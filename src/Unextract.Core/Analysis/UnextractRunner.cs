using Unextract.Core.Display;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;

namespace Unextract.Core.Analysis;

// 削除の確認 (SPEC §2)。Ask の戻り値 null は EOF。
public interface IConfirmationPrompt
{
    bool IsInteractive { get; }

    string? Ask(string prompt);
}

// 削除フェーズの入力。Comparer は初回分類で使ったものと同じインスタンス (PLAN.md §1)。
// Progress は (n, total) で各対象の処理の前に呼ばれる (Deleting n / total)。
// Mode は初回分類と同じ実行全体のモード。分岐は SPEC §8.3 の手順3 だけ (SPEC §15.4)。
public sealed record DeletionRequest(
    IReadOnlyList<MatchedFile> Candidates,
    IZipContentProvider Contents,
    ContentComparer Comparer,
    Action<int, int>? Progress = null,
    RunMode Mode = RunMode.Strict);

// 削除フェーズ (SPEC §3 の 6、§8.3、§8.4)。実装は Unextract.Core.Deletion.DeletionPhase。
public interface IDeletionPhase
{
    DeletionReport Delete(DeletionRequest request);
}

// テスト用の差し込み口 (PLAN.md §4)。製品 CLI からは設定しない。
public sealed class RunHooks
{
    // 初回分類の結果を表示した直後 (解析完了直後)。FATAL・dry-run でも呼ぶ。
    public Action? AfterAnalysis { get; init; }

    // 確認プロンプトを出す直前 (確認待ち)。--yes や非対話でプロンプトを出さない場合は呼ばない。
    public Action? AwaitingConfirmation { get; init; }
}

// Output は解析結果の一覧と削除フェーズの結果、ErrorOutput は入力エラー・FATAL の原因・停止の原因とエラーで終わる理由 (SPEC §10)。
// Contents は ZIP の内容の取得元 (null なら Archive)。テストで呼び出しを記録するために差し替える。
// Mode は実行全体のモード (SPEC §15.1)。初回分類と削除フェーズに同じ値を渡す。
public sealed record RunRequest(
    ZipArchiveSource Archive,
    string ArchivePath,
    string TargetPath,
    bool DryRun,
    bool AssumeYes,
    IFileSystemProbe Probe,
    TargetLocationPolicy Policy,
    Limits Limits,
    IConfirmationPrompt Prompt,
    IDeletionPhase DeletionPhase,
    TextWriter Output,
    TextWriter ErrorOutput,
    Action<int, int>? Progress = null,
    Action<int, int>? DeletionProgress = null,
    IZipContentProvider? Contents = null,
    RunHooks? Hooks = null,
    RunMode Mode = RunMode.Strict);

// InputError は target の入力エラー (§2)。Analysis は ZIP 事前検証以降の結果。ReportLines は初回分類の表示 (標準出力の分)。
public sealed record RunOutcome(
    ExitStatus Status,
    FatalError? InputError,
    AnalysisResult? Analysis,
    IReadOnlyList<string> ReportLines,
    DeletionReport? Deletion);

// 実行の流れ (SPEC §3)。--dry-run と通常実行は、確認・削除フェーズに入る前まで (初回分類と結果表示) 同じ処理を通り、
// その後だけが分岐する (SPEC §2、PLAN.md §1 の削除許可ゲート)。
public static class UnextractRunner
{
    public static RunOutcome Run(RunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var output = request.Output;
        var error = request.ErrorOutput;

        // §3 の手順1: target ルートの確認と保持 (ZIP は呼び出し側が開いて保持している)。
        var opened = TargetRootValidator.Open(request.Probe, request.TargetPath, request.Policy);
        if (opened.Root is not { } root)
        {
            error.WriteLine($"入力エラー: {opened.Error!.Describe()}");
            error.WriteLine("削除開始前に中止しました。削除0件。");
            return new RunOutcome(ExitStatus.Error, opened.Error, null, [], null);
        }

        using (root)
        {
            // 初回分類と削除直前の2回目の比較は、同じ比較器を共有する (PLAN.md §1)。
            var contents = request.Contents ?? request.Archive;
            var comparer = new ContentComparer(request.Limits);
            var analysis = Analyze(request, root, contents, comparer);
            var report = AnalysisReport.Format(analysis, request.Mode);
            foreach (var line in report)
            {
                output.WriteLine(line);
            }

            foreach (var line in AnalysisReport.FormatFatal(analysis))
            {
                error.WriteLine(line);
            }

            request.Hooks?.AfterAnalysis?.Invoke();

            // ここまでが --dry-run と通常実行で共通。以下が削除許可ゲート。FATAL があれば開かない。
            if (!analysis.Completed)
            {
                return new RunOutcome(ExitStatus.Error, null, analysis, report, null);
            }

            if (request.DryRun)
            {
                output.WriteLine("--dry-run のため削除しません。");
                return new RunOutcome(ExitStatus.Success, null, analysis, report, null);
            }

            var candidates = analysis.DeletionCandidates;
            if (candidates.Count == 0)
            {
                output.WriteLine("削除候補はありません。");
                return new RunOutcome(ExitStatus.Success, null, analysis, report, null);
            }

            if (!request.AssumeYes && !Confirm(request, candidates.Count))
            {
                output.WriteLine("中止しました。削除0件。");
                return new RunOutcome(ExitStatus.UserCancelled, null, analysis, report, null);
            }

            var deleted = request.DeletionPhase.Delete(new DeletionRequest(candidates, contents, comparer, request.DeletionProgress, request.Mode));
            foreach (var line in AnalysisReport.FormatDeletion(deleted))
            {
                output.WriteLine(line);
            }

            foreach (var line in AnalysisReport.FormatDeletionErrors(deleted))
            {
                error.WriteLine(line);
            }

            // 停止、または停止がなくても DELETE_FAILED が1件以上あればエラー (SPEC §2、DEC-18)。
            var status = deleted.Stop is null && deleted.Failed.Count == 0 ? ExitStatus.Success : ExitStatus.Error;
            return new RunOutcome(status, null, analysis, report, deleted);
        }
    }

    // 確認入力は y / Y だけを開始とする。空入力、EOF、その他は中止。非対話で --yes がなければ中止 (SPEC §2)。
    public static bool IsConfirmation(string? answer) => answer is "y" or "Y";

    // §3 の 3〜4: ZIP 事前検証 → ZIP 自身の個体情報 → 初回分類。
    private static AnalysisResult Analyze(RunRequest request, TargetRoot root, IZipContentProvider contents, ContentComparer comparer)
    {
        var total = request.Archive.EntryCount;
        var prevalidation = ZipPrevalidator.Validate(request.Archive.Entries, request.Limits);
        if (prevalidation.Fatal is { } fatal)
        {
            // 事前検証の FATAL では target の項目に触れない。
            return AnalysisResult.BeforeClassification(total, fatal);
        }

        var identity = request.Probe.GetFileIdentity(request.ArchivePath);
        if (!identity.Succeeded)
        {
            return AnalysisResult.BeforeClassification(total, new FatalError(FatalKind.ArchiveIdentityFailed, Detail: identity.Describe()));
        }

        return ClassificationPipeline.Run(new ClassificationRequest(
            prevalidation.Entries,
            contents,
            request.Probe,
            root,
            identity.Value,
            request.Limits,
            request.Progress,
            comparer,
            request.Mode));
    }

    private static bool Confirm(RunRequest request, int count)
    {
        if (!request.Prompt.IsInteractive)
        {
            request.Output.WriteLine("標準入力が対話的でなく --yes も無いため、確認できません。");
            return false;
        }

        request.Hooks?.AwaitingConfirmation?.Invoke();

        // Fast では [y/N] の直前の行に警告を出す (SPEC §10、PLAN.md §4 の「Fast モード」)。確認プロンプトと同じ経路で渡す。
        var warning = request.Mode == RunMode.Fast ? AnalysisReport.FastWarning + Environment.NewLine : string.Empty;
        return IsConfirmation(request.Prompt.Ask($"{warning}{count} 件のファイルを削除します。よろしいですか? [y/N] "));
    }
}
