using System.Runtime.InteropServices;

namespace Unextract.Windows;

// FILE_ID_128 (16 バイト)。Win32 構造体のフィールドとしてそのまま使うため blittable にする。
// バイト列を2つの ulong (リトルエンディアン) として保持する。比較は値の完全一致だけに使う。
[StructLayout(LayoutKind.Sequential)]
public readonly record struct FileId128(ulong Low, ulong High)
{
    public override string ToString() => $"{High:X16}{Low:X16}";
}
