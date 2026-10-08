using System.IO.Hashing;
using Unextract.Core.Rar;
using Unextract.Core.Results;
using Unextract.Core.Zip;

namespace Unextract.Core.Tests.Fakes;

// 偽の RAR のエントリ。Name は DLL が返す名前 (ディレクトリは末尾の区切りなし)。既定は Windows で作られた CRC-32 付きのファイル。
internal sealed record FakeRarEntry(string Name, byte[]? Content = null)
{
    public bool IsDirectory { get; init; }

    public long? DeclaredLength { get; init; }

    public RarHostOs HostOs { get; init; } = RarHostOs.Windows;

    public uint? Attributes { get; init; }

    public bool IsSolid { get; init; }

    public bool IsSplit { get; init; }

    public bool IsEncrypted { get; init; }

    public RarHashType HashType { get; init; } = RarHashType.Crc32;

    public uint? Crc32 { get; init; }

    public long DictionarySize { get; init; } = 128 * 1024;

    public uint RedirectionType { get; init; }

    public byte[] Data => Content ?? [];

    public static FakeRarEntry Directory(string name) => new(name) { IsDirectory = true };

    public ZipEntryInfo ToInfo(int index) => new(
        index,
        IsDirectory ? Name + "\\" : Name,
        DeclaredLength ?? Data.LongLength,
        0,
        IsEncrypted,
        Crc32 ?? System.IO.Hashing.Crc32.HashToUInt32(Data),
        new RarEntryMetadata(
            HostOs,
            Attributes ?? (IsDirectory ? 0x10u : 0x20u),
            IsDirectory,
            IsSolid,
            IsSplit,
            HashType,
            DictionarySize,
            RedirectionType));
}

// 偽の RAR のアーカイブソース (DLL 不要)。Calls にソース・セッションの呼び出しを記録する。
internal sealed class FakeRarArchive(params FakeRarEntry[] entries) : IRarArchiveSource
{
    public List<string> Calls { get; } = [];

    public IReadOnlyList<FakeRarEntry> Items { get; } = entries;

    public RarArchiveInfo Archive { get; init; } = new(false, false);

    // OpenSession の失敗 (手順10)。
    public FatalError? SessionFailure { get; init; }

    // 前進の失敗の台本: 前進先の index → 結果。
    public Dictionary<int, RarAdvanceResult> AdvanceFailures { get; } = [];

    // RAR_TEST の失敗の台本: index → DLL のコード名。データは全て渡した後に失敗を返す。
    public Dictionary<int, string> TestFailures { get; } = [];

    // チャンクの長さ (最後の値を繰り返す)。
    public int[] ChunkSizes { get; set; } = [4 * 1024 * 1024];

    // 宣言と異なる実データ (NTFS ストリームの付加や切り詰めの再現)。
    public Dictionary<int, byte[]> ActualData { get; } = [];

    // セッションの Dispose で呼ぶ処理 (閉じる順の確認や close の失敗の注入)。
    public Action? OnSessionDispose { get; set; }

    public Action? OnArchiveDispose { get; set; }

    public FakeRarSession? Session { get; private set; }

    public int EntryCount => Items.Count;

    public IEnumerable<ZipEntryInfo> Entries => Items.Select((e, i) => e.ToInfo(i));

    public static Func<string, Limits, ZipOpenResult> Opener(FakeRarArchive archive) => (_, _) => new ZipOpenResult(archive, null);

    public static Func<string, Limits, ZipOpenResult> Failing(FatalError fatal) => (_, _) => new ZipOpenResult(null, fatal);

    // RAR は pull で読まない (docs/spec/rar.md#session)。
    public IZipEntryContent GetContent(int index) => throw new InvalidOperationException($"RAR の内容を pull で要求しました ({index})");

    public RarSessionOpenResult OpenSession()
    {
        Calls.Add("OpenSession");
        if (SessionFailure is { } failure)
        {
            return new RarSessionOpenResult(null, failure);
        }

        Session = new FakeRarSession(this);
        return new RarSessionOpenResult(Session, null);
    }

    public void Dispose()
    {
        Calls.Add("ArchiveClose");
        OnArchiveDispose?.Invoke();
    }
}

// 偽の内容読み取りセッション。前進はアーカイブの順に単調増加で、通過・直前の未テストのエントリの RAR_SKIP を記録する。
internal sealed class FakeRarSession(FakeRarArchive archive) : IRarReadSession
{
    private int _position = -1;
    private bool _tested;
    private bool _failed;

    public int DisposeCount { get; private set; }

    public RarAdvanceResult Advance(int index)
    {
        archive.Calls.Add($"Advance {index}");
        if (_failed || index <= _position)
        {
            throw new InvalidOperationException($"前進の順序が不正です ({_position} → {index})");
        }

        if (_position >= 0 && !_tested)
        {
            archive.Calls.Add($"Skip {_position}");
        }

        if (archive.AdvanceFailures.TryGetValue(index, out var failure))
        {
            _failed = true;
            return failure;
        }

        for (var passed = _position + 1; passed < index; passed++)
        {
            archive.Calls.Add($"Pass {passed}");
        }

        _position = index;
        _tested = false;
        return RarAdvanceResult.Ok(index);
    }

    public IRarEntryContent GetContent(int index)
    {
        archive.Calls.Add($"GetContent {index}");
        if (_failed || index != _position || _tested)
        {
            throw new InvalidOperationException($"前進先でない内容を要求しました ({index})");
        }

        _tested = true;
        return new Content(archive, index);
    }

    public void Dispose()
    {
        DisposeCount++;
        archive.Calls.Add("SessionClose");
        archive.OnSessionDispose?.Invoke();
    }

    private sealed class Content(FakeRarArchive archive, int index) : IRarEntryContent
    {
        private readonly FakeRarEntry _entry = archive.Items[index];

        public long Length => _entry.DeclaredLength ?? _entry.Data.LongLength;

        public uint? ExpectedCrc32 => _entry.HashType == RarHashType.Crc32 ? _entry.ToInfo(index).Crc32 : null;

        public RarTestResult Test(ContentSink sink)
        {
            archive.Calls.Add($"Test {index}");
            var data = archive.ActualData.TryGetValue(index, out var actual) ? actual : _entry.Data;
            var position = 0;
            var chunk = 0;
            while (position < data.Length)
            {
                var size = Math.Min(archive.ChunkSizes[Math.Min(chunk, archive.ChunkSizes.Length - 1)], data.Length - position);
                archive.Calls.Add($"Chunk {index} {size}");
                if (!sink(data.AsSpan(position, size)))
                {
                    // DLL は中止を ERAR_UNKNOWN で返し、以後のコールバックを呼ばない (docs/RATIONALE.md#rar-dll-usage)。
                    archive.Calls.Add($"Aborted {index}");
                    return RarTestResult.Failed("ERAR_UNKNOWN");
                }

                position += size;
                chunk++;
            }

            return archive.TestFailures.TryGetValue(index, out var code) ? RarTestResult.Failed(code) : RarTestResult.Success;
        }
    }
}
