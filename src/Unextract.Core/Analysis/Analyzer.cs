using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;

namespace Unextract.Core.Analysis;

// analyze のエントリ処理の入力。Entries は ZIP 事前検証 (ZipPrevalidator) を通過した全エントリ (ZIP 内の順序)。
// Progress は (n, total) で各エントリの判定の前に呼ばれる (Checking n / total)。Mode は実行全体のモード (docs/SPEC.md#modes)。
// OnResult は Classify のハンドルを閉じた後、DIRECTORY を含めて ZIP 順に呼ぶ。FATAL 原因は通知しない。
public sealed record AnalyzeRequest(
    IReadOnlyList<ValidatedZipEntry> Entries,
    IZipContentProvider Contents,
    IFileSystemProbe Probe,
    TargetRoot Root,
    VolumeFileId ArchiveIdentity,
    Limits Limits,
    Action<int, int>? Progress = null,
    RunMode Mode = RunMode.Strict,
    Action<EntryResult>? OnResult = null);

// analyze のエントリ処理 (docs/SPEC.md#execution、docs/spec/filesystem.md#classification、docs/spec/filesystem.md#special-files)。完全な非破壊操作で、比較用ハンドルだけを使う。
// 削除の能力を型として持たない (IDeletionProbe を受け取らない。docs/ARCHITECTURE.md#dependencies)。削除候補・スナップショットを作らない。
public static class Analyzer
{
    public static AnalysisResult Run(AnalyzeRequest request) => new AnalyzeRun(request).Execute();
}

// 1回の analyze。ZIP の順に1エントリずつ判定し、最初の FATAL で打ち切る。
// 比較用ハンドルは各エントリの判定の中で using によって閉じるため、FATAL・例外を含むどの経路でも
// エントリの判定が終わった時点で閉じられている (テスト T12、A06)。
internal sealed class AnalyzeRun
{
    private readonly AnalyzeRequest _request;
    private readonly ContentComparer _comparer;
    private readonly TargetResolver _target;

    public AnalyzeRun(AnalyzeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _comparer = new ContentComparer(request.Limits);
        _target = new TargetResolver(request.Probe, request.Root, request.Entries);
    }

    internal RealNameResolver Resolver => _target.Names;

    internal ContentComparer Comparer => _comparer;

    public AnalysisResult Execute()
    {
        var entries = _request.Entries;
        var results = new List<EntryResult>(entries.Count);

        for (var i = 0; i < entries.Count; i++)
        {
            _request.Progress?.Invoke(i + 1, entries.Count);

            var entry = entries[i];
            var reference = new ZipEntryRef(entry.Entry.Index, entry.Entry.FullName);
            var expectedPath = _request.Root.ExpectedPath(entry.Components);
            EntryResult result;
            if (entry.IsDirectory)
            {
                result = new EntryResult(reference, expectedPath, Classification.Directory, Length: 0);
            }
            else
            {
                var outcome = Classify(entry, expectedPath);
                if (outcome.Failure is { } failure)
                {
                    return new AnalysisResult(entries.Count, results, new FatalError(failure.Kind, reference, failure.Detail, failure.Step, failure.Win32Error));
                }

                result = new EntryResult(reference, expectedPath, outcome.Classification, outcome.SkipReason, entry.Entry.Length);
            }

            results.Add(result);
            // Classify 内の using が終了してから通知する。FATAL 原因は結果として通知しない。
            _request.OnResult?.Invoke(result);
        }

        return new AnalysisResult(entries.Count, results, null);
    }

    // docs/spec/filesystem.md#resolution の実名解決とanalyze分類。deleteの事前判定と削除順序は同文書のdelete-flow。
    private Outcome Classify(ValidatedZipEntry entry, string expectedPath)
    {
        // 手順1・2: 親成分と最終成分の実名確認。
        var resolution = _target.Resolve(entry);
        switch (resolution.Kind)
        {
            case ResolutionKind.Failed:
                return Outcome.Fail(resolution.FatalKind!.Value, resolution.Detail, EntryStep.Resolve, resolution.Win32Error);
            case ResolutionKind.Missing:
                return Outcome.Of(Classification.Missing);
            case ResolutionKind.ParentReparse:
                return Outcome.Of(Classification.SkippedSpecialFile, SkipReason.ParentReparsePoint);
        }

        // 手順4: 比較用ハンドル。判定が終わった時点で閉じる。
        var opened = _request.Probe.OpenForComparison(expectedPath);
        if (!opened.Succeeded)
        {
            // 存在を確認した後に開けない (共有違反、見つからない、アクセス拒否を含む) は FATAL (docs/RATIONALE.md#open-failures、docs/RATIONALE.md#open-failures)。
            return Outcome.Fail(FatalKind.ComparisonOpenFailed, opened.Describe(), EntryStep.Open, opened.Error);
        }

        using var handle = opened.Value;

        // 手順4・5: File ID・ボリュームシリアルが列挙で見つけた項目と一致すること、最終パスと期待パスの序数一致。
        var baseline = new IdentityBaseline(new VolumeFileId(_request.Root.Id.VolumeSerialNumber, resolution.Item.FileId), null, expectedPath);
        var identity = HandleInspector.VerifyIdentity(handle, baseline);
        if (identity.Failure is { } failure)
        {
            return new Outcome(default, null, failure);
        }

        // 手順6・7: docs/spec/filesystem.md#special-files の特殊判定とサイズ。
        var inspection = HandleInspector.Inspect(handle, identity.Id, entry.Entry.Length, _request.ArchiveIdentity, stopOnDeletePending: false);
        switch (inspection.Kind)
        {
            case InspectionKind.Failed:
                return new Outcome(default, null, inspection.Failure);
            case InspectionKind.SkippedSpecialFile:
                return Outcome.Of(Classification.SkippedSpecialFile, inspection.SkipReason);
            case InspectionKind.Modified:
                return Outcome.Of(Classification.Modified);
        }

        // 手順8: 内容検証 (Strict と Fast の唯一の分岐。Strict は同じハンドルから読んで1回だけ比較する)。
        var compared = _comparer.Verify(_request.Mode, _request.Contents, entry.Entry.Index, handle);
        return compared.Verdict switch
        {
            ContentVerdict.Fatal => Outcome.Fail(compared.FatalKind!.Value, compared.Detail, EntryStep.Compare, compared.Win32Error),
            ContentVerdict.Mismatch => Outcome.Of(Classification.Modified),
            ContentVerdict.NotRead => Outcome.Of(Classification.SameSize),
            _ => Outcome.Of(Classification.Matched),
        };
    }

    private readonly record struct Outcome(Classification Classification, SkipReason? SkipReason, HandleFailure? Failure)
    {
        public static Outcome Of(Classification classification, SkipReason? reason = null) => new(classification, reason, null);

        public static Outcome Fail(FatalKind kind, string? detail, EntryStep step, int? win32Error = null) => new(default, null, new HandleFailure(kind, detail, step, win32Error));
    }
}
