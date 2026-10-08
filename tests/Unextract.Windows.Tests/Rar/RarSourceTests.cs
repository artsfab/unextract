using System.Text.RegularExpressions;
using Unextract.Core.Rar;
using Unextract.Core.Results;
using Unextract.Core.Zip;
using Unextract.Windows.Rar;

namespace Unextract.Windows.Tests.Rar;

// テスト U30〜U45: 製品の RAR のソース・セッション・コールバック (Unextract.Windows/Rar) を、台本で動く偽の DLL (FakeUnrarApi) で確かめる。
// DLL 不要。実物の DLL による試験 (受理・拒否の実物、往復、中断後の close) は置き換えない (docs/TESTING.md#rar)。
public class RarSourceTests
{
    private static ZipOpenResult Open(string path, FakeUnrarApi api, Limits? limits = null) =>
        RarArchiveSource.Open(path, limits ?? Limits.Default, () => FakeRarFiles.Loaded(api));

    private static RarArchiveSource OpenSource(FakeUnrarApi api, Limits? limits = null)
    {
        var path = FakeRarFiles.Write(TestFixture.CreateDirectory());
        var opened = Open(path, api, limits);
        Assert.Null(opened.Fatal);
        return Assert.IsType<RarArchiveSource>(opened.Source);
    }

    // U30: 署名 (先頭8バイト) を DLL のロードより先に確かめる。SFX・短いファイル・別形式は ARCHIVE_NOT_RAR。
    [Theory]
    [InlineData("rar5", true)]
    [InlineData("rar4", true)]
    [InlineData("sfx", false)]
    [InlineData("short", false)]
    [InlineData("empty", false)]
    [InlineData("zip", false)]
    public void U30_Signature_CheckedBeforeLibrary(string kind, bool accepted)
    {
        byte[] content = kind switch
        {
            "rar5" => [.. FakeRarFiles.Rar5Signature, 1, 2],
            "rar4" => [.. FakeRarFiles.Rar4Signature, 1, 2],
            "sfx" => [0x4D, 0x5A, 0, 0, .. FakeRarFiles.Rar5Signature],
            "short" => FakeRarFiles.Rar5Signature[..6],
            "empty" => [],
            "zip" => [0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var path = FakeRarFiles.Write(TestFixture.CreateDirectory(), "x.rar", content);
        var loads = 0;

        var opened = RarArchiveSource.Open(path, Limits.Default, () =>
        {
            loads++;
            return new UnrarLoadResult(null, FatalError.RarLibraryUnavailable(RarLibraryFailure.NotFound, @"C:\x\UnRAR64.dll"));
        });

        Assert.Null(opened.Source);
        Assert.Equal(accepted ? FatalKind.RarLibraryUnavailable : FatalKind.ArchiveNotRar, opened.Fatal!.Kind);
        Assert.Equal(accepted ? 1 : 0, loads);
        AssertReleased(path);
    }

    // U30: 開いた後の署名の読み取りの失敗 (他のハンドルが先頭をバイト範囲ロック中) は ARCHIVE_UNREADABLE。DLL をロードしない (docs/spec/rar.md#format)。
    [Fact]
    public void U30_SignatureReadFailure_IsUnreadable_WithoutLoading()
    {
        var path = FakeRarFiles.Write(TestFixture.CreateDirectory(), "x.rar", [.. FakeRarFiles.Rar5Signature, 1, 2]);
        var loads = 0;

        using (var locker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            locker.Lock(0, 8);
            try
            {
                var opened = RarArchiveSource.Open(path, Limits.Default, () => { loads++; return FakeRarFiles.Loaded(new FakeUnrarApi()); });

                Assert.Null(opened.Source);
                Assert.Equal(FatalKind.ArchiveUnreadable, opened.Fatal!.Kind);
                Assert.False(string.IsNullOrEmpty(opened.Fatal.Detail));
                Assert.Equal(0, loads);
            }
            finally
            {
                locker.Unlock(0, 8);
            }
        }

        AssertReleased(path);
    }

    // U31: アーカイブを開けない (存在しない) は ARCHIVE_OPEN_FAILED。DLL をロードしない。
    [Fact]
    public void U31_MissingArchive_IsOpenFailed_WithoutLoading()
    {
        var path = Path.Combine(TestFixture.CreateDirectory(), "missing.rar");
        var loads = 0;

        var opened = RarArchiveSource.Open(path, Limits.Default, () => { loads++; return FakeRarFiles.Loaded(new FakeUnrarApi()); });

        Assert.Equal(FatalKind.ArchiveOpenFailed, opened.Fatal!.Kind);
        Assert.Equal(0, loads);
    }

    // U32: 一覧用の open は RAR_OM_LIST_INCSPLIT で、保持中のハンドルの最終パス (\\?\ 付き) を渡す。保持中は他者の書き込み・削除を拒否する。
    [Fact]
    public void U32_ListOpen_UsesIncludingSplitMode_AndFinalPathOfHeldHandle()
    {
        var api = new FakeUnrarApi(FakeHeader.File("a.txt", [1, 2, 3]));
        var path = FakeRarFiles.Write(TestFixture.CreateDirectory());

        using (var source = Assert.IsType<RarArchiveSource>(Open(path, api).Source))
        {
            Assert.Equal(["Open 2", "Read 2 0", "Skip 2 0", "Read 2 1", "Close 2"], api.Calls);
            Assert.StartsWith(@"\\?\", api.OpenedPaths[0], StringComparison.Ordinal);
            Assert.Equal(Path.GetFullPath(path), api.OpenedPaths[0][4..], StringComparer.OrdinalIgnoreCase);
            Assert.Equal(0, api.OpenHandles);
            Assert.ThrowsAny<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
            Assert.Throws<InvalidOperationException>(() => source.GetContent(0));
        }

        AssertReleased(path);
    }

    // U33: ボリュームのフラグは open 直後に ARCHIVE_MULTI_VOLUME。ヘッダーを読まず、一覧用のハンドルを閉じる。
    [Fact]
    public void U33_VolumeFlag_IsRejectedBeforeEnumeration()
    {
        var api = new FakeUnrarApi(FakeHeader.File("a.txt", [1])) { ArchiveFlags = UnrarNative.ArchiveVolume | UnrarNative.ArchiveSolid };
        var path = FakeRarFiles.Write(TestFixture.CreateDirectory());

        var opened = Open(path, api);

        Assert.Equal(FatalKind.ArchiveMultiVolume, opened.Fatal!.Kind);
        Assert.Null(opened.Fatal.Entry);
        Assert.Equal(["Open 2", "Close 2"], api.Calls);
        AssertReleased(path);
    }

    // U34: open の失敗。パスワードの要求 (ヘッダーの暗号化) は ARCHIVE_ENCRYPTED、それ以外は ARCHIVE_OPEN_FAILED (DLL のコード名を詳細に)。
    [Fact]
    public void U34_OpenFailures()
    {
        var plain = new FakeUnrarApi();
        plain.OpenFailures[UnrarNative.OpenModeListIncludingSplit] = 15;
        var opened = Open(FakeRarFiles.Write(TestFixture.CreateDirectory()), plain);
        Assert.Equal(FatalKind.ArchiveOpenFailed, opened.Fatal!.Kind);
        Assert.Equal("ERAR_EOPEN", opened.Fatal.Detail);

        var password = new FakeUnrarApi { RequestPasswordOnOpen = true };
        password.OpenFailures[UnrarNative.OpenModeListIncludingSplit] = UnrarNative.ErarMissingPassword;
        var encrypted = Open(FakeRarFiles.Write(TestFixture.CreateDirectory()), password);
        Assert.Equal(FatalKind.ArchiveEncrypted, encrypted.Fatal!.Kind);
        Assert.Equal("ERAR_MISSING_PASSWORD、パスワードの要求を拒否", encrypted.Fatal.Detail);
    }

    // U35: DLL の値の変換。ディレクトリは名前に \ を付ける。作成元 OS・ハッシュの種類・辞書 (KiB)・フラグ・展開サイズ (long に収まらない値は最大値)。
    [Fact]
    public void U35_EntryConversion()
    {
        var api = new FakeUnrarApi(
            FakeHeader.Directory(@"d"),
            new FakeHeader(@"d\u.txt") { HostOs = UnrarNative.HostUnix, Attributes = 0x81A4, HashType = UnrarNative.HashBlake2, DictionarySizeKiB = 1_048_577, UnpackedSize = 5, Crc = 7 },
            new FakeHeader("s.txt") { Flags = UnrarNative.HeaderSplitBefore | UnrarNative.HeaderSolid | UnrarNative.HeaderEncrypted, HostOs = 0, RedirectionType = 3 },
            new FakeHeader("big") { UnpackedSize = ulong.MaxValue, HashType = 9 },
            new FakeHeader("after") { Flags = UnrarNative.HeaderSplitAfter, HashType = UnrarNative.HashNone })
        {
            ArchiveFlags = UnrarNative.ArchiveSolid | UnrarNative.ArchiveEncryptedHeaders,
        };

        using var source = OpenSource(api);

        Assert.Equal(new RarArchiveInfo(true, true), source.Archive);
        var e = source.Entries.ToList();
        Assert.Equal([@"d\", @"d\u.txt", "s.txt", "big", "after"], e.Select(x => x.FullName));
        Assert.Equal(Enumerable.Range(0, 5), e.Select(x => x.Index));
        Assert.Equal(new RarEntryMetadata(RarHostOs.Windows, 0x10, true, false, false, RarHashType.Crc32, 128 * 1024, 0), e[0].Rar);
        Assert.Equal(new RarEntryMetadata(RarHostOs.Unix, 0x81A4, false, false, false, RarHashType.Blake2, 1_048_577L * 1024, 0), e[1].Rar);
        Assert.Equal(5, e[1].Length);
        Assert.Equal(7u, e[1].Crc32);
        Assert.Equal(new RarEntryMetadata(RarHostOs.Other, 0x20, false, true, true, RarHashType.Crc32, 128 * 1024, 3), e[2].Rar);
        Assert.True(e[2].IsEncrypted);
        Assert.Equal(long.MaxValue, e[3].Length);
        Assert.Equal(RarHashType.None, e[3].Rar!.HashType);
        Assert.True(e[4].Rar!.IsSplit);
    }

    // U36: 列挙の失敗。ヘッダーの読み取り・RAR_SKIP の失敗は ARCHIVE_UNREADABLE。一覧用のハンドルは閉じる。
    [Theory]
    [InlineData("read")]
    [InlineData("skip")]
    public void U36_EnumerationFailure_IsUnreadable(string kind)
    {
        var api = new FakeUnrarApi(FakeHeader.File("a.txt", [1]), FakeHeader.File("b.txt", [2]));
        if (kind == "read")
        {
            api.ReadFailures[(UnrarNative.OpenModeListIncludingSplit, 1)] = 12;
        }
        else
        {
            api.SkipFailures[(UnrarNative.OpenModeListIncludingSplit, 0)] = 18;
        }

        var opened = Open(FakeRarFiles.Write(TestFixture.CreateDirectory()), api);

        Assert.Equal(FatalKind.ArchiveUnreadable, opened.Fatal!.Kind);
        Assert.Equal(kind == "read" ? "ERAR_BAD_DATA" : "ERAR_EREAD", opened.Fatal.Detail);
        Assert.Equal("Close 2", api.Calls[^1]);
        Assert.Equal(0, api.OpenHandles);
    }

    // U37: 列挙中の上限 (docs/spec/rar.md#limits)。超過したエントリで直ちにやめ、以後のヘッダーを読まない。原因エントリは超過したもの。
    [Fact]
    public void U37_EnumerationLimits_StopImmediately()
    {
        var headers = Enumerable.Range(0, 5).Select(i => FakeHeader.File($"f{i}.txt", [1])).ToArray();

        var count = new FakeUnrarApi(headers);
        var tooMany = Open(FakeRarFiles.Write(TestFixture.CreateDirectory()), count, Limits.Default with { MaxEntries = 2 });
        Assert.Equal(FatalKind.TooManyEntries, tooMany.Fatal!.Kind);
        Assert.Equal(new ZipEntryRef(2, "f2.txt"), tooMany.Fatal.Entry);
        Assert.Equal(["Open 2", "Read 2 0", "Skip 2 0", "Read 2 1", "Skip 2 1", "Read 2 2", "Close 2"], count.Calls);

        // ディレクトリの名前長は末尾の \ を含む。
        var name = new FakeUnrarApi(FakeHeader.File("abc", [1]), FakeHeader.Directory("abc"));
        var tooLong = Open(FakeRarFiles.Write(TestFixture.CreateDirectory()), name, Limits.Default with { MaxNameLength = 3 });
        Assert.Equal(FatalKind.NameTooLong, tooLong.Fatal!.Kind);
        Assert.Equal(@"abc\", tooLong.Fatal.Entry!.Name);

        var metadata = new FakeUnrarApi(headers);
        var tooLarge = Open(FakeRarFiles.Write(TestFixture.CreateDirectory()), metadata, Limits.Default with { MaxMetadataBytes = 3 * (12 + 128) });
        Assert.Equal(FatalKind.MetadataTooLarge, tooLarge.Fatal!.Kind);
        Assert.Equal(3, tooLarge.Fatal.Entry!.Index);
    }

    // U37: バッファを使い切った名前は切り詰めの可能性があるので、上限に関係なく名前長の上限違反。
    [Fact]
    public void U37_NameFillingBuffer_IsNameTooLong()
    {
        var api = new FakeUnrarApi(FakeHeader.File(new string('a', UnrarNative.NameBufferChars + 10), [1]));

        var opened = Open(FakeRarFiles.Write(TestFixture.CreateDirectory()), api, Limits.Default with { MaxNameLength = int.MaxValue - 1 });

        Assert.Equal(FatalKind.NameTooLong, opened.Fatal!.Kind);
        Assert.Equal(UnrarNative.NameBufferChars - 1, opened.Fatal.Entry!.Name.Length);
    }

    // U38: 一覧用のハンドルの close の失敗は internal_error (例外)。元の失敗の処理中なら元の失敗を優先する (docs/ARCHITECTURE.md#lifetimes)。
    [Fact]
    public void U38_ListCloseFailure()
    {
        var path = FakeRarFiles.Write(TestFixture.CreateDirectory());
        var api = new FakeUnrarApi(FakeHeader.File("a.txt", [1]));
        api.CloseResults[UnrarNative.OpenModeListIncludingSplit] = 17;
        var ex = Assert.Throws<InvalidOperationException>(() => Open(path, api));
        Assert.Contains("ERAR_ECLOSE", ex.Message, StringComparison.Ordinal);
        AssertReleased(path);

        var failing = new FakeUnrarApi(FakeHeader.File("a.txt", [1])) { ArchiveFlags = UnrarNative.ArchiveVolume };
        failing.CloseResults[UnrarNative.OpenModeListIncludingSplit] = 17;
        Assert.Equal(FatalKind.ArchiveMultiVolume, Open(FakeRarFiles.Write(TestFixture.CreateDirectory()), failing).Fatal!.Kind);
    }

    // U39: 手順10 は内容読み取り用 (RAR_OM_EXTRACT) に開くだけで、ヘッダーを読まない。失敗は ARCHIVE_OPEN_FAILED。
    [Fact]
    public void U39_OpenSession()
    {
        var api = new FakeUnrarApi(FakeHeader.File("a.txt", [1]));
        using var source = OpenSource(api);
        api.Calls.Clear();

        var opened = source.OpenSession();
        using (opened.Session)
        {
            Assert.Equal(["Open 1"], api.Calls);
        }

        Assert.Equal(["Open 1", "Close 1"], api.Calls);

        api.OpenFailures[UnrarNative.OpenModeExtract] = 15;
        var failed = source.OpenSession();
        Assert.Null(failed.Session);
        Assert.Equal(FatalKind.ArchiveOpenFailed, failed.Fatal!.Kind);
        Assert.Equal("ERAR_EOPEN", failed.Fatal.Detail);
    }

    // U40: 前進は直前の未テストのエントリを1回だけ RAR_SKIP し、通過するエントリのヘッダーを照合して RAR_SKIP し、前進先のヘッダーを照合する。
    // 最後の前進先より後ろは読まない。
    [Fact]
    public void U40_Advance_SkipsOnceAndVerifiesPassedHeaders()
    {
        var api = new FakeUnrarApi(FakeHeader.File("a", [1]), FakeHeader.Directory("d"), FakeHeader.File("b", [2]), FakeHeader.File("c", [3]), FakeHeader.File("z", [4]));
        using var source = OpenSource(api);
        using var session = source.OpenSession().Session!;
        api.Calls.Clear();

        Assert.True(session.Advance(0).Succeeded);
        var verified = new List<byte>();
        Assert.Equal(RarTestResult.Success, session.GetContent(0).Test(chunk => { verified.AddRange(chunk.ToArray()); return true; }));
        Assert.True(session.Advance(2).Succeeded);
        Assert.True(session.Advance(3).Succeeded);

        Assert.Equal([1], verified);
        Assert.Equal(["Read 1 0", "Test 1 0", "Chunk 0 1", "Read 1 1", "Skip 1 1", "Read 1 2", "Skip 1 2", "Read 1 3"], api.Calls);
        Assert.Throws<InvalidOperationException>(() => session.Advance(3));
        Assert.Throws<InvalidOperationException>(() => session.GetContent(2));
    }

    public static TheoryData<string> ChangedFields() =>
        ["name", "directory", "size", "host", "attributes", "flags", "crc", "hash-type", "hash", "dictionary", "redirection"];

    // U41: ヘッダーの1項目だけの不一致 → ARCHIVE_CHANGED。検出位置は通過中のエントリ。照合を終えた位置を返す。
    [Theory]
    [MemberData(nameof(ChangedFields))]
    public void U41_HeaderFieldMismatch_IsArchiveChanged(string field)
    {
        var original = new FakeHeader("p") { UnpackedSize = 1, Crc = 5, Hash = new byte[32] };
        var changed = field switch
        {
            "name" => original with { Name = "q" },
            "directory" => original with { Flags = UnrarNative.HeaderDirectory },
            "size" => original with { UnpackedSize = 2 },
            "host" => original with { HostOs = UnrarNative.HostUnix },
            "attributes" => original with { Attributes = 0x21 },
            "flags" => original with { Flags = UnrarNative.HeaderSplitAfter },
            "crc" => original with { Crc = 6 },
            "hash-type" => original with { HashType = UnrarNative.HashBlake2 },
            "hash" => original with { Hash = [.. new byte[31], 1] },
            "dictionary" => original with { DictionarySizeKiB = 256 },
            "redirection" => original with { RedirectionType = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var api = new FakeUnrarApi(FakeHeader.File("a", [1]), original, FakeHeader.File("b", [2]))
        {
            ExtractHeaders = [FakeHeader.File("a", [1]), changed, FakeHeader.File("b", [2])],
        };
        using var source = OpenSource(api);
        using var session = source.OpenSession().Session!;

        Assert.True(session.Advance(0).Succeeded);
        var result = session.Advance(2);

        Assert.Equal(FatalKind.ArchiveChanged, result.FailureKind);
        Assert.Equal(new ZipEntryRef(1, "p"), result.DetectedAt);
        Assert.Equal(0, result.LastVerifiedIndex);
        Assert.Throws<InvalidOperationException>(() => session.Advance(3));
    }

    // U41: ハッシュの種類が CRC-32 でないヘッダーの FileCRC は値として扱わない。DLL はハッシュの無いエントリ (RAR5 のディレクトリなど) にも
    // 初期化されていない値・直前のヘッダーの値を FileCRC に詰める (unrarsrc 7.2.3) ため、列挙と内容読み取りで異なっても照合は成功する。
    [Fact]
    public void U41_FileCrcWithoutCrcHashType_IsNotCompared()
    {
        var listed = FakeHeader.Directory("d") with { HashType = UnrarNative.HashNone, Crc = 0x11111111 };
        var api = new FakeUnrarApi(listed, FakeHeader.File("a", [1]))
        {
            ExtractHeaders = [listed with { Crc = 0x22222222 }, FakeHeader.File("a", [1])],
        };
        using var source = OpenSource(api);
        using var session = source.OpenSession().Session!;

        Assert.True(session.Advance(1).Succeeded);
    }

    // U41: ハッシュの種類が BLAKE2 のヘッダーは Hash を照合し、FileCRC は照合しない。内容検証に CRC-32 の期待値を渡さない (ExpectedCrc32 は null)。
    [Fact]
    public void U41_Blake2Header_ComparesHashButNotCrc()
    {
        var hash = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var listed = FakeHeader.File("p", [1, 2, 3]) with { HashType = UnrarNative.HashBlake2, Hash = hash, Crc = 0x11111111 };

        var crcOnly = new FakeUnrarApi(FakeHeader.File("a", [1]), listed, FakeHeader.File("b", [2]))
        {
            ExtractHeaders = [FakeHeader.File("a", [1]), listed with { Crc = 0x22222222 }, FakeHeader.File("b", [2])],
        };
        using (var source = OpenSource(crcOnly))
        using (var session = source.OpenSession().Session!)
        {
            Assert.True(session.Advance(1).Succeeded);
            Assert.Null(session.GetContent(1).ExpectedCrc32);
            Assert.True(session.Advance(2).Succeeded);
        }

        var hashChanged = new FakeUnrarApi(FakeHeader.File("a", [1]), listed, FakeHeader.File("b", [2]))
        {
            ExtractHeaders = [FakeHeader.File("a", [1]), listed with { Hash = [.. hash[..31], 0] }, FakeHeader.File("b", [2])],
        };
        using (var source = OpenSource(hashChanged))
        using (var session = source.OpenSession().Session!)
        {
            Assert.True(session.Advance(0).Succeeded);
            var result = session.Advance(2);

            Assert.Equal(FatalKind.ArchiveChanged, result.FailureKind);
            Assert.Equal(new ZipEntryRef(1, "p"), result.DetectedAt);
            Assert.Equal(0, result.LastVerifiedIndex);
        }
    }

    // U42:早期の終端 → ARCHIVE_CHANGED、ヘッダーの読み取り・RAR_SKIP の失敗 → ARCHIVE_UNREADABLE。
    [Theory]
    [InlineData("end", FatalKind.ArchiveChanged, 2, 1)]
    [InlineData("read", FatalKind.ArchiveUnreadable, 2, 1)]
    [InlineData("skip-previous", FatalKind.ArchiveUnreadable, 0, 0)]
    [InlineData("skip-passed", FatalKind.ArchiveUnreadable, 1, 1)]
    public void U42_AdvanceFailures(string kind, FatalKind expected, int detected, int verified)
    {
        var listed = new[] { FakeHeader.File("a", [1]), FakeHeader.File("b", [2]), FakeHeader.File("c", [3]) };
        var api = new FakeUnrarApi(listed);
        switch (kind)
        {
            case "end":
                api.ExtractHeaders = [.. listed[..2]];
                break;
            case "read":
                api.ReadFailures[(UnrarNative.OpenModeExtract, 2)] = 12;
                break;
            case "skip-previous":
                api.SkipFailures[(UnrarNative.OpenModeExtract, 0)] = 12;
                break;
            case "skip-passed":
                api.SkipFailures[(UnrarNative.OpenModeExtract, 1)] = 12;
                break;
        }

        using var source = OpenSource(api);
        using var session = source.OpenSession().Session!;
        Assert.True(session.Advance(0).Succeeded);

        var result = session.Advance(2);

        Assert.Equal(expected, result.FailureKind);
        Assert.Equal(detected, result.DetectedAt!.Index);
        Assert.Equal(verified, result.LastVerifiedIndex);
        Assert.Equal(kind == "end" ? "ヘッダーが足りません" : "ERAR_BAD_DATA", result.Code);
    }

    // U43: 内容の押し込み。64 KiB を超えるチャンク、中止で以後のコールバックを受けない (中止後の再呼び出しは何もせず中止)、
    // RAR_TEST の失敗のコード名、範囲外の長さ、パスワードの要求。
    [Fact]
    public void U43_Test_Chunks_Abort_AndFailures()
    {
        var data = new byte[(4 * 1024 * 1024) + 3];
        new Random(3).NextBytes(data);
        var api = new FakeUnrarApi(FakeHeader.File("big", data), FakeHeader.File("x", [1, 2, 3], chunk: 1)) { CallAgainAfterAbort = true };
        using var source = OpenSource(api);

        using (var session = source.OpenSession().Session!)
        {
            Assert.True(session.Advance(0).Succeeded);
            var content = session.GetContent(0);
            Assert.Equal(data.LongLength, content.Length);
            Assert.Equal(System.IO.Hashing.Crc32.HashToUInt32(data), content.ExpectedCrc32);
            var sizes = new List<int>();
            Assert.Equal(RarTestResult.Success, content.Test(chunk => { sizes.Add(chunk.Length); return true; }));
            Assert.Equal([4 * 1024 * 1024, 3], sizes);
            Assert.Throws<InvalidOperationException>(() => content.Test(_ => true));

            Assert.True(session.Advance(1).Succeeded);
            var seen = 0;
            var aborted = session.GetContent(1).Test(_ => ++seen < 2);
            Assert.Equal(RarTestResult.Failed("ERAR_UNKNOWN"), aborted);
            Assert.Equal(2, seen);
            Assert.Contains("Again 1 -1", api.Calls);
        }

        foreach (var (prelude, detail) in new[]
        {
            ((UnrarNative.CallbackProcessData, (nint)1, (nint)(-1)), "ERAR_UNKNOWN、データの長さが不正"),
            ((UnrarNative.CallbackProcessData, (nint)1, unchecked((nint)((long)int.MaxValue + 1))), "ERAR_UNKNOWN、データの長さが不正"),
            ((UnrarNative.CallbackNeedPasswordW, (nint)0, (nint)0), "ERAR_UNKNOWN、パスワードの要求を拒否"),
            ((UnrarNative.CallbackChangeVolumeW, (nint)0, (nint)0), "ERAR_UNKNOWN、巻の変更の要求を拒否"),
            ((UnrarNative.CallbackLargeDictionary, (nint)8_388_608, (nint)4_194_304), "ERAR_UNKNOWN、大きな辞書の確認を拒否"),
        })
        {
            api.TestPreludes[0] = prelude;
            using var session = source.OpenSession().Session!;
            Assert.True(session.Advance(0).Succeeded);
            var called = false;
            Assert.Equal(RarTestResult.Failed(detail), session.GetContent(0).Test(_ => called = true));
            Assert.False(called);
            Assert.Throws<InvalidOperationException>(() => session.Advance(1));
        }

        api.TestPreludes.Clear();
        api.TestFailures[0] = 12;
        using (var session = source.OpenSession().Session!)
        {
            Assert.True(session.Advance(0).Succeeded);
            Assert.Equal(RarTestResult.Failed("ERAR_BAD_DATA"), session.GetContent(0).Test(_ => true));
        }
    }

    // U44: コールバック内の例外はネイティブの境界を越えず (-1 で中止)、DLL から戻った後に元の例外を再送出する。
    [Fact]
    public void U44_ExceptionInCallback_IsRethrownAfterNativeCall()
    {
        var api = new FakeUnrarApi(FakeHeader.File("a", [1, 2, 3], chunk: 1)) { CallAgainAfterAbort = true };
        using var source = OpenSource(api);
        using var session = source.OpenSession().Session!;
        Assert.True(session.Advance(0).Succeeded);
        var thrown = new InvalidOperationException("target");

        var caught = Assert.Throws<InvalidOperationException>(() => session.GetContent(0).Test(_ => throw thrown));

        Assert.Same(thrown, caught);
        Assert.Equal(["Chunk 0 1", "Again 0 -1"], api.Calls.SkipWhile(c => !c.StartsWith("Chunk", StringComparison.Ordinal)).ToList());
    }

    // U45: セッションの Dispose は close → 解放。2回呼んでも1回だけ閉じる。close の失敗は解放の後に例外。
    [Fact]
    public void U45_SessionDispose()
    {
        var api = new FakeUnrarApi(FakeHeader.File("a", [1]));
        using var source = OpenSource(api);
        var session = source.OpenSession().Session!;
        session.Dispose();
        session.Dispose();
        Assert.Single(api.Calls, c => c == "Close 1");
        Assert.Throws<ObjectDisposedException>(() => session.Advance(0));

        api.CloseResults[UnrarNative.OpenModeExtract] = 17;
        var failing = source.OpenSession().Session!;
        var ex = Assert.Throws<InvalidOperationException>(failing.Dispose);
        Assert.Contains("ERAR_ECLOSE", ex.Message, StringComparison.Ordinal);
        failing.Dispose();
        Assert.Equal(0, api.OpenHandles);
    }

    // U46: 製品は RAR_TEST と RAR_SKIP 以外の操作と展開先を使わない (src/ の静的な検査。実行時の書き込みの無いことは実物の DLL の試験で確かめる)。
    [Fact]
    public void U46_SourceUsesOnlySkipAndTest()
    {
        var src = Path.Combine(FindRepositoryRoot(), "src");
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
        var calls = files.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\.ProcessFile\(\s*[^,()]+,\s*([^)]+)\)").Select(m => m.Groups[1].Value.Trim())).ToList();

        Assert.NotEmpty(calls);
        Assert.All(calls, op => Assert.Contains(op, new[] { "UnrarNative.RarSkip", "UnrarNative.RarTest", "operation" }));
        Assert.DoesNotContain(files, f => Regex.IsMatch(File.ReadAllText(f), @"\bRarExtract\b|\bRAR_EXTRACT\s*="));

        // 関数ポインターの呼び出しは展開先を常に null にする1か所だけ。
        var native = File.ReadAllText(Path.Combine(src, "Unextract.Windows", "Rar", "UnrarNative.cs"));
        Assert.Single(Regex.Matches(native, @"processFile\("));
        Assert.Contains("return processFile(archive, operation, null, null);", native, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "unextract.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("リポジトリのルートが見つかりません。");
    }

    // 保持していたアーカイブのハンドルが閉じられた (他者が DELETE アクセスで開ける)。
    private static void AssertReleased(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
