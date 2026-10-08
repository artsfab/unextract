using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Unextract.Core.Rar;

namespace Unextract.Windows.Rar;

// 1つの DLL のハンドルに登録するコールバックの状態 (UserData は GCHandle)。DLL のハンドルを閉じ終えるまで解放しない。
// コールバックの中で例外を投げるとネイティブの呼び出し元をまたいでプロセスが終了するため、全例外を捕捉して記録し -1 (中止) を返す。
// 呼び出し側は DLL の呼び出しから戻った後に TakeException で元の例外を再送出する (docs/ARCHITECTURE.md#dependencies)。
internal sealed class UnrarCallbackState : IDisposable
{
    private GCHandle _handle;

    public UnrarCallbackState()
    {
        _handle = GCHandle.Alloc(this);
    }

    public nint UserData => GCHandle.ToIntPtr(_handle);

    // RAR_TEST の間だけ設定する内容の受け取り先。
    public ContentSink? Sink { get; set; }

    // 受け取り先が中止を返した後は、DLL が再び呼んでも何もせず中止を返す。
    public bool Aborted { get; set; }

    public bool PasswordRequested { get; private set; }

    public bool VolumeRequested { get; private set; }

    public bool LargeDictionaryRequested { get; private set; }

    public bool InvalidLength { get; private set; }

    private ExceptionDispatchInfo? Exception { get; set; }

    public bool HasException => Exception is not null;

    // UnmanagedCallersOnly の入口から呼ぶ処理本体。テストの偽物も同じ処理を呼ぶ。
    public static int Dispatch(uint message, nint userData, nint p1, nint p2)
    {
        UnrarCallbackState? state = null;
        try
        {
            state = GCHandle.FromIntPtr(userData).Target as UnrarCallbackState
                ?? throw new InvalidOperationException("コールバックの状態がありません。");
            return state.Handle(message, p1, p2);
        }
        catch (Exception ex)
        {
            if (state is not null)
            {
                state.Exception ??= ExceptionDispatchInfo.Capture(ex);
                state.Aborted = true;
            }

            return -1;
        }
    }

    // コールバックで記録した例外があれば再送出する。
    public void ThrowIfFailed()
    {
        var exception = Exception;
        Exception = null;
        exception?.Throw();
    }

    // 1回の DLL の呼び出しの前に、要求の記録を消す。
    public void Reset()
    {
        Sink = null;
        Aborted = false;
        PasswordRequested = false;
        VolumeRequested = false;
        LargeDictionaryRequested = false;
        InvalidLength = false;
        Exception = null;
    }

    // 中止の理由の補足 (診断用)。
    public string? Describe() =>
        PasswordRequested ? "パスワードの要求を拒否"
        : VolumeRequested ? "巻の変更の要求を拒否"
        : LargeDictionaryRequested ? "大きな辞書の確認を拒否"
        : InvalidLength ? "データの長さが不正"
        : null;

    public void Dispose()
    {
        if (_handle.IsAllocated)
        {
            _handle.Free();
        }
    }

    private unsafe int Handle(uint message, nint p1, nint p2)
    {
        switch (message)
        {
            case UnrarNative.CallbackProcessData:
                if (Aborted || Sink is not { } sink)
                {
                    Aborted = true;
                    return -1;
                }

                if (p2 < 0 || p2 > int.MaxValue || (p1 == 0 && p2 != 0))
                {
                    InvalidLength = true;
                    Aborted = true;
                    return -1;
                }

                if (!sink(new ReadOnlySpan<byte>((void*)p1, (int)p2)))
                {
                    Aborted = true;
                    return -1;
                }

                return 1;
            case UnrarNative.CallbackNeedPassword:
            case UnrarNative.CallbackNeedPasswordW:
                PasswordRequested = true;
                return -1;
            case UnrarNative.CallbackChangeVolume:
            case UnrarNative.CallbackChangeVolumeW:
                VolumeRequested = true;
                return -1;
            case UnrarNative.CallbackLargeDictionary:
                // -1 で ERAR_LARGE_DICT になる (docs/RATIONALE.md#rar-dll-usage)。1 GiB を超える辞書は Prepare で拒否済み。
                LargeDictionaryRequested = true;
                return -1;
            default:
                return -1;
        }
    }
}
