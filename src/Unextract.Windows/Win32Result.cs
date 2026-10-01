using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Unextract.Windows;

// Win32 呼び出しの結果。失敗は例外にせず、Win32 エラーコードと失敗した操作名を返す。
// エラーコードの意味づけ (MISSING / FATAL / 停止など) は呼び出し側が行う。
// 失敗時に部分的な値は持たない。
public readonly struct Win32Result<T>
{
    private readonly T? _value;

    private Win32Result(T? value, int error, string? operation)
    {
        _value = value;
        Error = error;
        Operation = operation;
    }

    // 成功時は 0。
    public int Error { get; }

    // 失敗した操作 (API と情報クラス) の名前。成功時は null。
    public string? Operation { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    public bool Succeeded => Error == Win32Error.Success;

    public T? Value => Succeeded ? _value : default;

    internal static Win32Result<T> Ok(T value) => new(value, Win32Error.Success, null);

    // error が 0 の失敗は作らない (呼び出し側の取り違えで成功に見えないようにする)。
    internal static Win32Result<T> Fail(int error, string operation) =>
        new(default, error == Win32Error.Success ? Win32Error.InvalidData : error, operation);

    internal static Win32Result<T> LastError(string operation) => Fail(Marshal.GetLastPInvokeError(), operation);

    internal Win32Result<TOther> Cast<TOther>() => Win32Result<TOther>.Fail(Error, Operation!);

    public override string ToString() => Succeeded ? $"Ok({_value})" : $"{Operation} failed: {Error}";
}
