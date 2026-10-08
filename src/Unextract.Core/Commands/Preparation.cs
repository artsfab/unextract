using Unextract.Core.Analysis;
using Unextract.Core.Entries;
using Unextract.Core.Rar;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;

namespace Unextract.Core.Commands;

// 拒否対象の最終パスを求めた結果 (docs/SPEC.md#prepare の手順4)。求められなければ Error (入力エラー)。
public sealed record TargetLocationPolicyResult(TargetLocationPolicy? Policy, FatalError? Error);

// analyze と delete に共通の実行環境。OpenArchive と Contents はテストでの差し替え用 (既定は ZipArchiveSource.Open とその内容)。
// OpenRarArchive は RAR (docs/spec/rar.md#format) を開く関数で、CLI が UnRAR.dll の読み込み元を決めて渡す (Core は DLL に依存しない)。
// Progress は (n, total) で各エントリの処理の前に呼ばれる。Output は標準出力、ErrorOutput は標準エラー出力 (docs/spec/cli.md#streams)。
// Notifications を設定した経路は人間向け出力と Progress を使わず、同期通知と構造化 outcome を返す。
public sealed record CommandContext(
    IFileSystemProbe Probe,
    Func<TargetLocationPolicyResult> ResolveProtectedLocations,
    Limits Limits,
    TextWriter Output,
    TextWriter ErrorOutput,
    Action<int, int>? Progress = null,
    Func<string, ZipOpenResult>? OpenArchive = null,
    Func<IZipContentProvider, IZipContentProvider>? Contents = null,
    CommandNotifications? Notifications = null,
    Func<string, Limits, ZipOpenResult>? OpenRarArchive = null);

public enum PrepareStage
{
    Archive,
    Entries,
    ProtectedLocations,
    TargetRoot,
    ZipValidation,
    ArchiveIdentity,
    EntriesMatch,

    // Strict の RAR の内容読み取り用の open (手順10)。
    ContentSession,
}

// Prepare の失敗。Message は「入力エラー: 」「FATAL: 」を付けた標準エラー出力の1行。
// target ルートを開いた後の FATAL (ZIP 全体の事前検査、ZIP 自身の個体) では、analyze の表示のために TargetFinalPath と全エントリ数を持つ。
public sealed record PrepareFailure(PrepareStage Stage, string Message, FatalError? Fatal = null, string? TargetFinalPath = null, int? TotalEntries = null, EntriesError? EntriesError = null)
{
    // FatalError は target の入力エラーにも使うため、原因型や表示文字列から区分を推測しない。
    public bool IsFatal => Stage is PrepareStage.Archive or PrepareStage.ZipValidation or PrepareStage.ArchiveIdentity or PrepareStage.ContentSession;
}

// Prepare を終えた状態 (docs/SPEC.md#prepare)。実行全体で保持するハンドルは ZIP と target ルートの2つだけ (Strict の RAR では
// 内容読み取りのセッションを加える)。Session は Prepared が唯一所有し、Analyzer・Deleter は借用する。
// Dispose はセッション → target ルート → アーカイブの順に閉じる (前の close が例外を投げても後の close を行う)。
// Targets は処理対象 (analyze は全エントリ、delete は全エントリまたは --entries で選んだエントリ。ZIP の順)。
internal sealed class Prepared(IArchiveSource source, TargetRoot root, IReadOnlyList<ValidatedZipEntry> entries, IReadOnlyList<ValidatedZipEntry> targets, VolumeFileId archiveIdentity,
    IRarReadSession? session = null)
    : IDisposable
{
    public IRarReadSession? Session { get; } = session;

    public IArchiveSource Source { get; } = source;

    public TargetRoot Root { get; } = root;

    public IReadOnlyList<ValidatedZipEntry> Entries { get; } = entries;

    public IReadOnlyList<ValidatedZipEntry> Targets { get; } = targets;

    public VolumeFileId ArchiveIdentity { get; } = archiveIdentity;

    public bool EntriesSelected { get; init; }

    public PreparedCommandInfo Info(string archivePath, RunMode mode) =>
        new(archivePath, Root.FinalPath, mode, Entries.Count, Targets.Count, EntriesSelected);

    public void Dispose()
    {
        try
        {
            Session?.Dispose();
        }
        finally
        {
            try
            {
                Root.Dispose();
            }
            finally
            {
                Source.Dispose();
            }
        }
    }

    // 要求の組立て時の確認 (docs/spec/rar.md#session)。Strict の RAR ならセッションあり、ZIP と Fast ならセッションなし。崩れていたら製品の誤配線。
    public IRarReadSession? SessionFor(RunMode mode)
    {
        var expected = mode == RunMode.Strict && Source is IRarArchiveSource;
        if (expected != (Session is not null))
        {
            throw new InvalidOperationException("アーカイブの形式・モードと内容読み取りのセッションの組み合わせが不正です。");
        }

        return Session;
    }
}

// Prepare (docs/SPEC.md#prepare の手順2〜10。手順1 の引数検査と必要なログ作成は CLI)。どの段階の失敗でも target のエントリ (target ルート以外の列挙、
// 比較用・削除用ハンドルのオープン) には触れず、削除は0件である (docs/SPEC.md#zero-deletions、docs/RATIONALE.md#current-state)。
internal static class Preparation
{
    public static (Prepared? Prepared, PrepareFailure? Failure) Run(string archivePath, string targetPath, string? entriesPath, RunMode mode, CommandContext context)
    {
        // 手順2: アーカイブを FileShare.Read で開いて実行終了まで保持する。形式は拡張子だけで決める (docs/spec/rar.md#format)。
        // RAR は署名・DLL・一覧用の open・ボリュームのフラグ・列挙 (列挙中の上限を含む) をこの関数の中で行う。
        var format = ArchiveFormats.FromPath(archivePath);
        var opened = format == ArchiveFormat.Rar
            ? (context.OpenRarArchive ?? throw new InvalidOperationException("RAR を開く関数が設定されていません。"))(archivePath, context.Limits)
            : (context.OpenArchive ?? ZipArchiveSource.Open)(archivePath);
        if (opened.Source is not { } source)
        {
            var openError = opened.Fatal! with { Format = format };
            return Fail(PrepareStage.Archive, $"FATAL: {openError.Describe()}", openError);
        }

        var rar = source as IRarArchiveSource;
        if ((format == ArchiveFormat.Rar) != (rar is not null))
        {
            source.Dispose();
            throw new InvalidOperationException("アーカイブの形式と開いたソースが一致しません。");
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
                    return Fail(PrepareStage.Entries, $"入力エラー: {entriesError.Describe()}", entriesError: entriesError);
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
                // RAR は RAR 固有の受理規則の後に、ZIP と共通の名前・構造・上限の検査を行う (docs/spec/rar.md#listing)。
                var total = source.EntryCount;
                if (rar is not null && RarPrevalidator.Validate(rar.Archive, source.Entries, context.Limits) is { } rarError)
                {
                    var rarFatal = rarError with { Format = format };
                    return Fail(PrepareStage.ZipValidation, $"FATAL: {rarFatal.Describe()}", rarFatal, root.FinalPath, total);
                }

                var prevalidation = ZipPrevalidator.Validate(source.Entries, context.Limits);
                if (prevalidation.Fatal is { } prevalidationError)
                {
                    var fatal = prevalidationError with { Format = format };
                    return Fail(PrepareStage.ZipValidation, $"FATAL: {fatal.Describe()}", fatal, root.FinalPath, total);
                }

                // 手順7: ZIP 自身の個体 (ボリュームシリアルと File ID)。
                var identity = context.Probe.GetFileIdentity(archivePath);
                if (!identity.Succeeded)
                {
                    var identityError = new FatalError(FatalKind.ArchiveIdentityFailed, Detail: identity.Describe(), Win32Error: identity.Error, Format: format);
                    return Fail(PrepareStage.ArchiveIdentity, $"FATAL: {identityError.Describe()}", identityError, root.FinalPath, total);
                }

                // 手順8・9: entries と事前検証を通過したエントリの照合、処理対象の決定。
                var targets = prevalidation.Entries;
                if (entries is not null)
                {
                    var matched = EntriesList.Match(entries.Lines!, prevalidation.Entries, format);
                    if (matched.Error is { } matchError)
                    {
                        return Fail(PrepareStage.EntriesMatch, $"入力エラー: {matchError.Describe()}", total: total, entriesError: matchError);
                    }

                    targets = matched.Selected!;
                }

                // 手順10: Strict の RAR だけ、内容読み取り用に開いて保持する。open だけでヘッダーは読まない (docs/spec/rar.md#session)。
                IRarReadSession? session = null;
                if (rar is not null && mode == RunMode.Strict)
                {
                    var sessionOpened = rar.OpenSession();
                    if (sessionOpened.Session is not { } opened10)
                    {
                        var sessionError = (sessionOpened.Fatal ?? throw new InvalidOperationException("セッションを開けない原因がありません。")) with { Format = format };
                        return Fail(PrepareStage.ContentSession, $"FATAL: {sessionError.Describe()}", sessionError, root.FinalPath, total);
                    }

                    session = opened10;
                }

                keepRoot = true;
                keep = true;
                return (new Prepared(source, root, prevalidation.Entries, targets, identity.Value, session) { EntriesSelected = entries is not null }, null);
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

    private static (Prepared?, PrepareFailure?) Fail(PrepareStage stage, string message, FatalError? fatal = null, string? targetFinalPath = null, int? total = null, EntriesError? entriesError = null) =>
        (null, new PrepareFailure(stage, message, fatal, targetFinalPath, total, entriesError));
}
