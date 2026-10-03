using Unextract.Core.Analysis;
using Unextract.Core.Entries;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;

namespace Unextract.Core.Commands;

// 拒否対象の最終パスを求めた結果 (SPEC §3.1 の手順4)。求められなければ Error (入力エラー)。
public sealed record TargetLocationPolicyResult(TargetLocationPolicy? Policy, FatalError? Error);

// analyze と delete に共通の実行環境。OpenArchive と Contents はテストでの差し替え用 (既定は ZipArchiveSource.Open とその内容)。
// Progress は (n, total) で各エントリの処理の前に呼ばれる。Output は標準出力、ErrorOutput は標準エラー出力 (SPEC §10.4)。
public sealed record CommandContext(
    IFileSystemProbe Probe,
    Func<TargetLocationPolicyResult> ResolveProtectedLocations,
    Limits Limits,
    TextWriter Output,
    TextWriter ErrorOutput,
    Action<int, int>? Progress = null,
    Func<string, ZipOpenResult>? OpenArchive = null,
    Func<IZipContentProvider, IZipContentProvider>? Contents = null);

internal enum PrepareStage
{
    Archive,
    Entries,
    ProtectedLocations,
    TargetRoot,
    ZipValidation,
    ArchiveIdentity,
    EntriesMatch,
}

// Prepare の失敗。Message は「入力エラー: 」「FATAL: 」を付けた標準エラー出力の1行。
// target ルートを開いた後の FATAL (ZIP 全体の事前検査、ZIP 自身の個体) では、analyze の表示のために TargetFinalPath と全エントリ数を持つ。
internal sealed record PrepareFailure(PrepareStage Stage, string Message, FatalError? Fatal = null, string? TargetFinalPath = null, int TotalEntries = 0);

// Prepare を終えた状態 (SPEC §3.1)。実行全体で保持するハンドルは ZIP と target ルートの2つだけ。Dispose で両方を閉じる。
// Targets は処理対象 (analyze は全エントリ、delete は全エントリまたは --entries で選んだエントリ。ZIP の順)。
internal sealed class Prepared(ZipArchiveSource source, TargetRoot root, IReadOnlyList<ValidatedZipEntry> entries, IReadOnlyList<ValidatedZipEntry> targets, VolumeFileId archiveIdentity)
    : IDisposable
{
    public ZipArchiveSource Source { get; } = source;

    public TargetRoot Root { get; } = root;

    public IReadOnlyList<ValidatedZipEntry> Entries { get; } = entries;

    public IReadOnlyList<ValidatedZipEntry> Targets { get; } = targets;

    public VolumeFileId ArchiveIdentity { get; } = archiveIdentity;

    public bool EntriesSelected { get; init; }

    public void Dispose()
    {
        Root.Dispose();
        Source.Dispose();
    }
}

// Prepare (SPEC §3.1 の手順2〜9。手順1 の引数の検査は CLI)。どの段階の失敗でも target のエントリ (target ルート以外の列挙、
// 比較用・削除用ハンドルのオープン) には触れず、削除は0件である (SPEC §3.5、DEC-26)。
internal static class Preparation
{
    public static (Prepared? Prepared, PrepareFailure? Failure) Run(string archivePath, string targetPath, string? entriesPath, CommandContext context)
    {
        // 手順2: ZIP を FileShare.Read で開いて実行終了まで保持する。
        var opened = (context.OpenArchive ?? ZipArchiveSource.Open)(archivePath);
        if (opened.Source is not { } source)
        {
            return Fail(PrepareStage.Archive, $"FATAL: {opened.Fatal!.Describe()}", opened.Fatal);
        }

        var keep = false;
        try
        {
            // 手順3: entries を読み、形式を検査する。読み終えたら閉じ、以後は参照しない。
            EntriesParseResult? entries = null;
            if (entriesPath is not null)
            {
                entries = EntriesList.Read(entriesPath, context.Limits);
                if (entries.Error is { } entriesError)
                {
                    return Fail(PrepareStage.Entries, $"入力エラー: {entriesError.Describe()}");
                }
            }

            // 手順4: 拒否対象の最終パス。求められなければ安全を確認できないため入力エラー。
            var locations = context.ResolveProtectedLocations();
            if (locations.Policy is not { } policy)
            {
                return Fail(PrepareStage.ProtectedLocations, $"入力エラー: {locations.Error!.Describe()}", locations.Error);
            }

            // 手順5: target ルートの確認と保持。
            var target = TargetRootValidator.Open(context.Probe, targetPath, policy);
            if (target.Root is not { } root)
            {
                return Fail(PrepareStage.TargetRoot, $"入力エラー: {target.Error!.Describe()}", target.Error);
            }

            var keepRoot = false;
            try
            {
                // 手順6: ZIP の全エントリの事前検証。--entries の有無に関係なく全エントリに行う。target に触れない。
                var total = source.EntryCount;
                var prevalidation = ZipPrevalidator.Validate(source.Entries, context.Limits);
                if (prevalidation.Fatal is { } fatal)
                {
                    return Fail(PrepareStage.ZipValidation, $"FATAL: {fatal.Describe()}", fatal, root.FinalPath, total);
                }

                // 手順7: ZIP 自身の個体 (ボリュームシリアルと File ID)。
                var identity = context.Probe.GetFileIdentity(archivePath);
                if (!identity.Succeeded)
                {
                    var identityError = new FatalError(FatalKind.ArchiveIdentityFailed, Detail: identity.Describe());
                    return Fail(PrepareStage.ArchiveIdentity, $"FATAL: {identityError.Describe()}", identityError, root.FinalPath, total);
                }

                // 手順8・9: entries と事前検証を通過したエントリの照合、処理対象の決定。
                var targets = prevalidation.Entries;
                if (entries is not null)
                {
                    var matched = EntriesList.Match(entries.Lines!, prevalidation.Entries);
                    if (matched.Error is { } matchError)
                    {
                        return Fail(PrepareStage.EntriesMatch, $"入力エラー: {matchError.Describe()}");
                    }

                    targets = matched.Selected!;
                }

                keepRoot = true;
                keep = true;
                return (new Prepared(source, root, prevalidation.Entries, targets, identity.Value) { EntriesSelected = entries is not null }, null);
            }
            finally
            {
                if (!keepRoot)
                {
                    root.Dispose();
                }
            }
        }
        finally
        {
            if (!keep)
            {
                source.Dispose();
            }
        }
    }

    private static (Prepared?, PrepareFailure?) Fail(PrepareStage stage, string message, FatalError? fatal = null, string? targetFinalPath = null, int total = 0) =>
        (null, new PrepareFailure(stage, message, fatal, targetFinalPath, total));
}
