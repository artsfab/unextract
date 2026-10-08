using System.ComponentModel;
using System.Security.Cryptography;
using Unextract.Core.Results;

namespace Unextract.Windows.Rar;

internal sealed record UnrarLoadResult(IUnrarApi? Api, FatalError? Fatal);

// UnRAR.dll の照合とロード (docs/spec/rar.md#pinning)。読み込み元のファイルを FileShare.Read で開いたまま SHA-256 を計算し、
// 一致すれば同じパスを LoadLibraryExW (絶対パス、依存の探索を DLL のフォルダーと System32 に限る) でロードしてから閉じる。
// 保持中は他者の書き込み・削除・改名が拒否されるので、照合したファイルとロードしたファイルが同じになる (docs/RATIONALE.md#rar-dll-usage)。FreeLibrary はしない。
// 結果はインスタンスが保持し、ロードは1回だけ行う。製品はプロセスで1つのインスタンス (ForProcess) を使う。
// テストは読み込み元・期待値を差し込んだ新しいインスタンスを使う (CLI からは設定しない)。
internal sealed class UnrarLibrary
{
    // 採用版 UnRAR.dll 7.23 の x64 版 (rarlab unrardll-723.exe の x64\UnRAR64.dll。docs/RATIONALE.md#rar-dll-usage)。
    public const string FileName = "UnRAR64.dll";

    public const string AdoptedSha256 = "894b7d2db8d6363eb12f30c7b89f48eab9e71963b8b438675bdd64c12dd59bcc";

    private static readonly Lazy<UnrarLibrary> Process = new(() => new UnrarLibrary(
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? throw new InvalidOperationException("実行中の exe のパスを取得できません。"), FileName)));

    private readonly string _expectedSha256;
    private readonly int _expectedVersion;
    private readonly object _gate = new();
    private UnrarLoadResult? _result;

    public UnrarLibrary(string libraryPath)
        : this(libraryPath, AdoptedSha256, UnrarNative.AdoptedVersion)
    {
    }

    internal UnrarLibrary(string libraryPath, string expectedSha256, int expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(libraryPath);
        if (!Path.IsPathFullyQualified(libraryPath))
        {
            throw new ArgumentException("UnRAR.dll の読み込み元は絶対パスで指定する。", nameof(libraryPath));
        }

        LibraryPath = libraryPath;
        _expectedSha256 = expectedSha256;
        _expectedVersion = expectedVersion;
    }

    // 実行中の exe と同じフォルダーの UnRAR64.dll。最初に RAR を処理するときに作る (ZIP の実行では DLL を探さない)。
    public static UnrarLibrary ForProcess => Process.Value;

    public string LibraryPath { get; }

    // 照合とロード (1回だけ)。失敗は RAR_LIBRARY_UNAVAILABLE の4つの原因のどれか。
    public UnrarLoadResult Load()
    {
        lock (_gate)
        {
            return _result ??= LoadCore();
        }
    }

    private static string Win32Description(int error) => new Win32Exception(error).Message;

    private static int Win32CodeOf(Exception ex) => (ex.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? ex.HResult & 0xFFFF : 0;

    private unsafe UnrarLoadResult LoadCore()
    {
        FileStream held;
        try
        {
            held = new FileStream(LibraryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Fail(RarLibraryFailure.NotFound);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            var code = Win32CodeOf(ex);
            return Fail(RarLibraryFailure.LoadFailed, code == 0 ? ex.Message : Win32Description(code), code == 0 ? null : code);
        }

        using (held)
        {
            byte[] hash;
            try
            {
                hash = SHA256.HashData(held);
            }
            catch (IOException ex)
            {
                var code = Win32CodeOf(ex);
                return Fail(RarLibraryFailure.LoadFailed, code == 0 ? ex.Message : Win32Description(code), code == 0 ? null : code);
            }

            if (!string.Equals(Convert.ToHexStringLower(hash), _expectedSha256, StringComparison.Ordinal))
            {
                return Fail(RarLibraryFailure.HashMismatch);
            }

            // 照合したファイルを開いたままロードする。
            var module = Kernel32.LoadLibraryEx(LibraryPath, 0, LoadLibraryFlags.SearchDllLoadDirAndSystem32);
            if (module == 0)
            {
                var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                return Fail(RarLibraryFailure.LoadFailed, Win32Description(error), error);
            }

            var open = Kernel32.GetProcAddress(module, "RAROpenArchiveEx");
            var readHeader = Kernel32.GetProcAddress(module, "RARReadHeaderEx");
            var processFile = Kernel32.GetProcAddress(module, "RARProcessFileW");
            var close = Kernel32.GetProcAddress(module, "RARCloseArchive");
            var version = Kernel32.GetProcAddress(module, "RARGetDllVersion");
            if (open == 0 || readHeader == 0 || processFile == 0 || close == 0 || version == 0)
            {
                const int ProcNotFound = 127;
                return Fail(RarLibraryFailure.LoadFailed, Win32Description(ProcNotFound), ProcNotFound);
            }

            var functions = new UnrarFunctions(
                (delegate* unmanaged[Stdcall]<UnrarNative.RAROpenArchiveDataEx*, nint>)open,
                (delegate* unmanaged[Stdcall]<nint, UnrarNative.RARHeaderDataEx*, int>)readHeader,
                (delegate* unmanaged[Stdcall]<nint, int, char*, char*, int>)processFile,
                (delegate* unmanaged[Stdcall]<nint, int>)close,
                (delegate* unmanaged[Stdcall]<int>)version);

            var actual = functions.Version();
            return actual == _expectedVersion
                ? new UnrarLoadResult(functions, null)
                : Fail(RarLibraryFailure.VersionMismatch, version: actual);
        }
    }

    private UnrarLoadResult Fail(RarLibraryFailure failure, string? description = null, int? win32Error = null, int? version = null) =>
        new(null, FatalError.RarLibraryUnavailable(failure, LibraryPath, description, win32Error, version));
}
