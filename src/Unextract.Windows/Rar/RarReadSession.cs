using Unextract.Core.Rar;
using Unextract.Core.Results;
using Unextract.Core.Zip;

namespace Unextract.Windows.Rar;

// 内容読み取りのセッション (docs/spec/rar.md#session)。Prepare の手順10で RarArchiveSource が作り、Prepared が唯一所有する。単一スレッドで使う。
// 前進は、直前の未テストのエントリの RAR_SKIP → 通過するエントリのヘッダー照合と RAR_SKIP → 前進先のヘッダー照合 の順に行う。
// ヘッダーは Prepare の列挙の生の値と照合し、最後の前進先より後ろのヘッダーは読まない。
// 失敗・RAR_TEST の失敗の後は DLL の位置が保証されないので、それ以上前進させない (以後の Advance・GetContent は製品の誤配線として例外)。
internal sealed class RarReadSession : IRarReadSession
{
    private readonly IUnrarApi _api;
    private readonly nint _handle;
    private readonly UnrarCallbackState _callback;
    private readonly UnrarHeaderBuffer _buffer;
    private readonly IReadOnlyList<RarHeaderSnapshot> _snapshots;
    private readonly IReadOnlyList<ZipEntryInfo> _entries;

    // 最後にヘッダーを読んだエントリ (前進先)。まだ読んでいなければ -1。
    private int _current = -1;
    private bool _tested;
    private bool _broken;
    private bool _disposed;

    internal RarReadSession(IUnrarApi api, nint handle, UnrarCallbackState callback, UnrarHeaderBuffer buffer,
        IReadOnlyList<RarHeaderSnapshot> snapshots, IReadOnlyList<ZipEntryInfo> entries)
    {
        _api = api;
        _handle = handle;
        _callback = callback;
        _buffer = buffer;
        _snapshots = snapshots;
        _entries = entries;
    }

    public RarAdvanceResult Advance(int index)
    {
        Usable();
        if (index <= _current || index >= _snapshots.Count)
        {
            throw new InvalidOperationException($"前進先が不正です ({_current} → {index})。");
        }

        var verified = _current;

        // 1. 直前に読んだヘッダーのエントリで RAR_TEST を行っていなければ RAR_SKIP する。
        if (_current >= 0 && !_tested)
        {
            var skipped = Process(UnrarNative.RarSkip);
            if (skipped != UnrarNative.ErarSuccess)
            {
                return Fail(FatalKind.ArchiveUnreadable, _current, Describe(skipped), verified);
            }
        }

        // 2・3. 通過するエントリと前進先のヘッダーを読んで照合する。通過するエントリは RAR_SKIP する。
        for (var k = _current + 1; k <= index; k++)
        {
            _callback.Reset();
            var read = _buffer.Read(_api, _handle);
            _callback.ThrowIfFailed();
            if (read == UnrarNative.ErarEndArchive)
            {
                return Fail(FatalKind.ArchiveChanged, k, "ヘッダーが足りません", verified);
            }

            if (read != UnrarNative.ErarSuccess)
            {
                return Fail(FatalKind.ArchiveUnreadable, k, Describe(read), verified);
            }

            if (!_buffer.Snapshot().Matches(_snapshots[k]))
            {
                return Fail(FatalKind.ArchiveChanged, k, null, verified);
            }

            verified = k;
            if (k < index)
            {
                var skipped = Process(UnrarNative.RarSkip);
                if (skipped != UnrarNative.ErarSuccess)
                {
                    return Fail(FatalKind.ArchiveUnreadable, k, Describe(skipped), verified);
                }
            }
        }

        _current = index;
        _tested = false;
        return RarAdvanceResult.Ok(index);
    }

    public IRarEntryContent GetContent(int index)
    {
        Usable();
        if (index != _current || _tested)
        {
            throw new InvalidOperationException($"前進先でないエントリの内容を要求しました ({index})。");
        }

        var entry = _entries[index];
        var snapshot = _snapshots[index];
        return new Content(this, entry.Length, snapshot.HashType == UnrarNative.HashCrc32 ? snapshot.Crc : null);
    }

    // close → コールバックの状態 → ヘッダー構造体・バッファの順に解放する。2回呼ばれても安全。close の失敗は解放の後に例外として伝える。
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        int closed;
        try
        {
            closed = _api.Close(_handle);
        }
        finally
        {
            _callback.Dispose();
            _buffer.Dispose();
        }

        if (closed != UnrarNative.ErarSuccess)
        {
            throw new InvalidOperationException($"内容読み取り用の RAR のハンドルを閉じられません ({UnrarNative.ErrorName(closed)})");
        }
    }

    private void Usable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_broken)
        {
            throw new InvalidOperationException("失敗した読み取りセッションは使えません。");
        }
    }

    private int Process(int operation)
    {
        _callback.Reset();
        var result = _api.ProcessFile(_handle, operation);
        _callback.ThrowIfFailed();
        return result;
    }

    private string Describe(int code) => RarArchiveSource.Describe(code, _callback);

    private RarAdvanceResult Fail(FatalKind kind, int detectedAt, string? code, int verified)
    {
        _broken = true;
        return new RarAdvanceResult(kind, new ZipEntryRef(detectedAt, _entries[detectedAt].FullName), code, verified);
    }

    private sealed class Content(RarReadSession session, long length, uint? expectedCrc32) : IRarEntryContent
    {
        public long Length => length;

        public uint? ExpectedCrc32 => expectedCrc32;

        // RAR_TEST で内容を sink へ渡す。コールバック内の例外 (target 側・フックを含む) は DLL から戻った後に再送出する。
        public RarTestResult Test(ContentSink sink)
        {
            ArgumentNullException.ThrowIfNull(sink);
            session.Usable();
            if (session._tested)
            {
                throw new InvalidOperationException("同じエントリを2回テストしようとしました。");
            }

            session._tested = true;
            var callback = session._callback;
            callback.Reset();
            callback.Sink = sink;
            int result;
            try
            {
                result = session._api.ProcessFile(session._handle, UnrarNative.RarTest);
            }
            finally
            {
                callback.Sink = null;
            }

            if (callback.HasException || result != UnrarNative.ErarSuccess)
            {
                session._broken = true;
            }

            callback.ThrowIfFailed();
            return result == UnrarNative.ErarSuccess ? RarTestResult.Success : RarTestResult.Failed(session.Describe(result));
        }
    }
}
