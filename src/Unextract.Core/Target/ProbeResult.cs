using System.Diagnostics.CodeAnalysis;

namespace Unextract.Core.Target;

// IFileSystemProbe の操作結果。失敗は例外にせず、OS のエラーコード (Win32 エラー) と失敗した操作名を返す。
// 失敗時に部分的な値は持たない。エラーコードの意味づけ (MISSING / FATAL / 入力エラー) は Core が行う。
public readonly struct ProbeResult<T>
{
    // エラーコード 0 の失敗を作らないための代替値 (ERROR_INVALID_DATA)。
    private const int InvalidData = 13;

    private readonly T? _value;

    private ProbeResult(T? value, int error, string? operation)
    {
        _value = value;
        Error = error;
        Operation = operation;
    }

    // 成功時は 0。
    public int Error { get; }

    // 失敗した操作の名前。成功時は null。
    public string? Operation { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    public bool Succeeded => Error == 0;

    public T? Value => Succeeded ? _value : default;

    public static ProbeResult<T> Ok(T value) => new(value, 0, null);

    public static ProbeResult<T> Fail(int error, string operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return new(default, error == 0 ? InvalidData : error, operation);
    }

    public string Describe() => Succeeded ? "成功" : $"{Operation} が失敗 (Win32 エラー {Error})";

    public override string ToString() => Succeeded ? $"Ok({_value})" : Describe();
}
