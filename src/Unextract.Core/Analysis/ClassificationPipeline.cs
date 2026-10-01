using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Zip;

namespace Unextract.Core.Analysis;

// 初回分類の入力。Entries は ZIP 事前検証 (ZipPrevalidator) を通過した全エントリ (ZIP 内の順序)。
// Progress は (n, total) で各エントリの判定の前に呼ばれる (Checking n / total)。
// Comparer は削除フェーズの2回目の比較と共有する比較器 (PLAN.md §1)。null なら新しく作る (分類だけを行うテスト用)。
public sealed record ClassificationRequest(
    IReadOnlyList<ValidatedZipEntry> Entries,
    IZipContentProvider Contents,
    IFileSystemProbe Probe,
    TargetRoot Root,
    VolumeFileId ArchiveIdentity,
    Limits Limits,
    Action<int, int>? Progress = null,
    ContentComparer? Comparer = null);

// 初回分類 (SPEC §3 の 4、§6、§7)。--dry-run と通常実行は同じこの処理を通る (SPEC §2、PLAN.md §1)。
public static class ClassificationPipeline
{
    public static AnalysisResult Run(ClassificationRequest request) => new ClassificationRun(request).Execute();
}

// 1回の初回分類。ZIP の順に1エントリずつ判定し、最初の FATAL で打ち切る。
// 比較用ハンドルは各エントリの判定の中で using によって閉じるため、FATAL・例外を含むどの経路でも
// エントリの判定が終わった時点で閉じられている (SPEC §3 の 4、テスト T12)。
internal sealed class ClassificationRun
{
    private const uint FileAttributeReparsePoint = 0x400;

    // 親成分の「想定外の種類」(SPEC §6.1 の表): FILE_ATTRIBUTE_DEVICE を持つ項目は NTFS のディレクトリ項目として扱えない。
    private const uint FileAttributeDevice = 0x40;
    private const uint FileAttributeDirectory = 0x10;

    private readonly ClassificationRequest _request;
    private readonly ContentComparer _comparer;

    public ClassificationRun(ClassificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _comparer = request.Comparer ?? new ContentComparer(request.Limits);
        Resolver = new RealNameResolver(request.Probe, request.Root, request.Entries);
    }

    internal RealNameResolver Resolver { get; }

    public AnalysisResult Execute()
    {
        var entries = _request.Entries;
        var results = new List<EntryResult>(entries.Count);
        var matched = new List<MatchedFile>();

        for (var i = 0; i < entries.Count; i++)
        {
            _request.Progress?.Invoke(i + 1, entries.Count);

            var entry = entries[i];
            var reference = new ZipEntryRef(entry.Entry.Index, entry.Entry.FullName);
            if (entry.IsDirectory)
            {
                results.Add(new EntryResult(reference, Classification.Directory));
                continue;
            }

            var outcome = ClassifyFile(entry);
            if (outcome.FatalKind is { } kind)
            {
                return new AnalysisResult(entries.Count, results, matched, new FatalError(kind, reference, outcome.Detail));
            }

            results.Add(new EntryResult(reference, outcome.Classification, outcome.SkipReason));
            if (outcome.Snapshot is { } snapshot)
            {
                matched.Add(new MatchedFile(reference, snapshot.FinalPath, snapshot));
            }
        }

        return new AnalysisResult(entries.Count, results, matched, null);
    }

    // SPEC §6.1 の手順1〜8。
    private FileOutcome ClassifyFile(ValidatedZipEntry entry)
    {
        var components = entry.Components;

        // 手順1: 途中の親成分。最初に該当した成分で分類を確定し、その先を読まない。
        DirectoryItem? directoryItem = null;
        for (var depth = 0; depth < components.Count - 1; depth++)
        {
            var lookup = Resolver.Find(Prefix(components, depth), directoryItem, components[depth]);
            switch (lookup.Kind)
            {
                case LookupKind.Failed:
                    return FileOutcome.Fail(lookup.FatalKind!.Value, lookup.Detail);
                case LookupKind.NotFound:
                    // 存在しない、または大小文字だけ違う (序数比較で一致しない)。
                    return FileOutcome.Of(Classification.Missing);
            }

            var item = lookup.Item;
            if (IsReparse(item.Attributes, item.ReparseTag))
            {
                // reparse の判定は他の種類より優先する。reparse 先をたどらない。
                return FileOutcome.Of(Classification.SkippedSpecialFile, SkipReason.ParentReparsePoint);
            }

            if ((item.Attributes & FileAttributeDevice) != 0)
            {
                return FileOutcome.Fail(FatalKind.UnexpectedTargetType, $"属性 0x{item.Attributes:X}");
            }

            if ((item.Attributes & FileAttributeDirectory) == 0)
            {
                // 通常ファイル: その下にファイルは存在し得ない。
                return FileOutcome.Of(Classification.Missing);
            }

            directoryItem = item;
        }

        // 手順2: 最終成分の実名確認。
        var last = components.Count - 1;
        var final = Resolver.Find(Prefix(components, last), directoryItem, components[last]);
        switch (final.Kind)
        {
            case LookupKind.Failed:
                return FileOutcome.Fail(final.FatalKind!.Value, final.Detail);
            case LookupKind.NotFound:
                return FileOutcome.Of(Classification.Missing);
        }

        // 手順3〜8: 比較用ハンドル。判定が終わった時点で閉じる。
        var expectedPath = _request.Root.ExpectedPath(components);
        var opened = _request.Probe.OpenForComparison(expectedPath);
        if (!opened.Succeeded)
        {
            // 存在を確認した後に開けない (共有違反、見つからない、アクセス拒否を含む) は FATAL。
            return FileOutcome.Fail(FatalKind.ComparisonOpenFailed, opened.Describe());
        }

        using var handle = opened.Value;
        return ClassifyOpened(entry, final.Item, expectedPath, handle);
    }

    private FileOutcome ClassifyOpened(ValidatedZipEntry entry, DirectoryItem item, string expectedPath, IComparisonHandle handle)
    {
        // 手順3: File ID が列挙で見つけた項目と一致すること。
        var id = handle.GetVolumeFileId();
        if (!id.Succeeded)
        {
            return InfoFailed(id.Describe());
        }

        if (id.Value.FileId != item.FileId || id.Value.VolumeSerialNumber != _request.Root.Id.VolumeSerialNumber)
        {
            return FileOutcome.Fail(FatalKind.ComparisonFileIdMismatch);
        }

        // 手順4: 最終パスと期待パスの序数比較 (\\?\ 形式のまま)。
        var finalPath = handle.GetFinalPath();
        if (!finalPath.Succeeded)
        {
            return InfoFailed(finalPath.Describe());
        }

        if (!string.Equals(finalPath.Value, expectedPath, StringComparison.Ordinal))
        {
            return FileOutcome.Fail(FatalKind.FinalPathMismatch, $"期待パス {expectedPath}、最終パス {finalPath.Value}");
        }

        // 手順5: §7 の特殊判定。最初に Directory を判定し、ディレクトリなら他の情報を取得しない。
        var standard = handle.GetStandardInformation();
        if (!standard.Succeeded)
        {
            return InfoFailed(standard.Describe());
        }

        if (standard.Value.IsDirectory)
        {
            return FileOutcome.Of(Classification.SkippedSpecialFile, SkipReason.Directory);
        }

        // ディレクトリでない対象では、以降の取得 API が1つでも失敗したら (ERROR_HANDLE_EOF を含む) FATAL。
        // 判定に使う情報を全て取得してから判定する。
        var basic = handle.GetBasicInformation();
        if (!basic.Succeeded)
        {
            return InfoFailed(basic.Describe());
        }

        var tag = handle.GetAttributeTagInformation();
        if (!tag.Succeeded)
        {
            return InfoFailed(tag.Describe());
        }

        var streams = handle.GetStreams();
        if (!streams.Succeeded)
        {
            return InfoFailed(streams.Describe());
        }

        if (SpecialReason(id.Value, standard.Value, basic.Value, tag.Value, streams.Value) is { } reason)
        {
            return FileOutcome.Of(Classification.SkippedSpecialFile, reason);
        }

        // 手順6: サイズが Length と異なれば MODIFIED (ZIP 内容を読まない)。
        if (standard.Value.EndOfFile != entry.Entry.Length)
        {
            return FileOutcome.Of(Classification.Modified);
        }

        // スナップショットの項目は内容比較の前に同じハンドルから取得する (SPEC §8.2)。残りは親 File ID だけ。
        var parent = handle.GetParentFileId();
        if (!parent.Succeeded)
        {
            return InfoFailed(parent.Describe());
        }

        // 手順7: エントリ内容の検証基準による初回比較。
        var content = _request.Contents.GetContent(entry.Entry.Index);
        var compared = _comparer.Compare(content, handle);
        switch (compared.Verdict)
        {
            case ContentVerdict.Fatal:
                return FileOutcome.Fail(compared.FatalKind!.Value, compared.Detail);
            case ContentVerdict.Mismatch:
                return FileOutcome.Of(Classification.Modified);
        }

        // 手順8: 閉じる前に、内容比較の前に取得した値で再検証用スナップショットを記録する。
        var snapshot = new TargetSnapshot(
            id.Value.VolumeSerialNumber,
            id.Value.FileId,
            parent.Value,
            standard.Value.EndOfFile,
            basic.Value.LastWriteTime,
            basic.Value.ChangeTime,
            basic.Value.Attributes,
            standard.Value.NumberOfLinks,
            streams.Value,
            tag.Value.ReparseTag,
            finalPath.Value);
        return FileOutcome.Matched(snapshot);
    }

    // SPEC §7 の特殊判定 (Directory は判定済み)。どれか1つでも該当すれば SKIPPED_SPECIAL_FILE。
    private SkipReason? SpecialReason(
        VolumeFileId id,
        StandardInformation standard,
        BasicInformation basic,
        AttributeTagInformation tag,
        IReadOnlyList<StreamEntry> streams)
    {
        if (IsReparse(basic.Attributes | tag.Attributes, tag.ReparseTag))
        {
            return SkipReason.ReparsePoint;
        }

        if (standard.NumberOfLinks >= 2)
        {
            return SkipReason.HardLink;
        }

        if (streams.Any(s => s.Name != StreamEntry.DefaultDataStream))
        {
            return SkipReason.AlternateDataStream;
        }

        if (id == _request.ArchiveIdentity)
        {
            return SkipReason.ArchiveItself;
        }

        if (FileAttributeRules.IsSpecial(basic.Attributes))
        {
            return SkipReason.Attributes;
        }

        return null;
    }

    private static FileOutcome InfoFailed(string detail) => FileOutcome.Fail(FatalKind.TargetInfoFailed, detail);

    private static bool IsReparse(uint attributes, uint reparseTag) =>
        (attributes & FileAttributeReparsePoint) != 0 || reparseTag != 0;

    private static string[] Prefix(IReadOnlyList<string> components, int count)
    {
        var prefix = new string[count];
        for (var i = 0; i < count; i++)
        {
            prefix[i] = components[i];
        }

        return prefix;
    }

    private readonly record struct FileOutcome(
        Classification Classification,
        SkipReason? SkipReason,
        TargetSnapshot? Snapshot,
        FatalKind? FatalKind,
        string? Detail)
    {
        public static FileOutcome Of(Classification classification, SkipReason? reason = null) =>
            new(classification, reason, null, null, null);

        public static FileOutcome Matched(TargetSnapshot snapshot) =>
            new(Classification.Matched, null, snapshot, null, null);

        public static FileOutcome Fail(FatalKind kind, string? detail = null) => new(default, null, null, kind, detail);
    }
}
