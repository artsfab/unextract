using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Unextract.Windows.Tests;

// テスト専用の P/Invoke。製品コードではない。削除保留中の状態を作る (ヘルパーの FileDispositionInfo) ことと、
// タイムスタンプの書き戻し (テスト D10 の FileBasicInfo) だけに使う。
internal static unsafe partial class TestKernel32
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, void* buffer, uint bufferSize);
}
