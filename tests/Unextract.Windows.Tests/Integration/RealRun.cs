using System.IO.Compression;
using System.Security.Cryptography;
using Unextract.Core.Analysis;
using Unextract.Core.Commands;
using Unextract.Core.Deletion;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Tests.Fixtures;
using Unextract.Core.Zip;
using Unextract.Windows.Rar;

namespace Unextract.Windows.Tests.Integration;

// 実 ZIP と実 NTFS の target で、Windows の probe を接続した analyze・delete を実行する (Prepare と確認を含む)。
// Analyze は削除しない (削除の能力を型として持たない)。Delete は実際に削除する: 削除の指示の直前 (H5) に毎回 DeletionGuard で、
// 削除用ハンドルの最終パスが fixture 内であることを確かめる。削除用オープンは記録する (事前判定で開かないことの確認用)。
internal static class RealRun
{
    // 確認で answer を返す。返す前に awaiting (確認待ち中の変更の注入) を呼ぶ。
    private sealed class ScriptedPrompt(string? answer, Action? awaiting = null) : IConfirmationPrompt
    {
        public bool IsInteractive => true;

        public string? Ask(string prompt)
        {
            awaiting?.Invoke();
            return answer;
        }
    }

    // 削除用オープンと識別確認を記録して、Windows の probe にそのまま委ねる。
    public sealed class RecordingDeletionProbe(IDeletionProbe inner) : IDeletionProbe
    {
        public List<string> Opened { get; } = [];

        public List<string> IdentityChecked { get; } = [];

        public ProbeResult<IDeletionHandle> OpenForDeletion(string path)
        {
            Opened.Add(path);
            return inner.OpenForDeletion(path);
        }

        public ProbeResult<IdentityCheckInfo> CheckIdentity(string path)
        {
            IdentityChecked.Add(path);
            return inner.CheckIdentity(path);
        }
    }

    public sealed record Result(ExitStatus Status, AnalysisResult? AnalysisOrNull, DeleteReport? ReportOrNull, FatalError? PrepareError, string Output, string Error)
    {
        public RecordingDeletionProbe? Probe { get; init; }

        public AnalysisResult Analysis => AnalysisOrNull ?? throw new InvalidOperationException(Output + Error);

        public DeleteReport Report => ReportOrNull ?? throw new InvalidOperationException(Output + Error);

        public Classification Of(string name) => Analysis.Results.Single(r => r.Entry.Name == name).Classification;

        public SkipReason? SkipOf(string name) => Analysis.Results.Single(r => r.Entry.Name == name).SkipReason;

        public DeleteEntryResult ResultOf(string name) => Report.Results.Single(r => r.Entry.Name == name);

        public IEnumerable<string> DeletedNames => Report.Results.Where(r => r.Status == DeleteStatus.Deleted).Select(r => r.Entry.Name);

        // target からの相対パス (\ 区切り) の対象を削除用に開こうとしたか。
        public bool Opened(string relative) => Probe!.Opened.Any(p => p.EndsWith(@"\target\" + relative, StringComparison.Ordinal));
    }

    // mode は実行全体のモード (docs/SPEC.md#modes)。afterResults は結果表示の直後に呼ぶ (実行中の変更の注入に使う)。
    public static Result Analyze(string zipPath, string target, Limits? limits = null, RunMode mode = RunMode.Strict, Action? afterResults = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var outcome = AnalyzeCommand.Run(new AnalyzeCommandRequest(zipPath, target, mode, Context(limits, output, error), afterResults));
        return new Result(outcome.Status, outcome.Analysis, null, outcome.PrepareError, output.ToString(), error.ToString());
    }

    // 実際に削除する実行。確認には answer (既定は y) と答え、その直前に awaiting を呼ぶ。entriesPath は --entries。
    // hooks.BeforeDisposition の後に guard.Check を必ず呼ぶ (違反なら例外 → そのエントリで STOP し、削除しない)。
    public static Result Delete(
        string zipPath,
        string target,
        DeletionGuard guard,
        RunMode mode = RunMode.Strict,
        DeleteHooks? hooks = null,
        string? entriesPath = null,
        Action? awaiting = null,
        string? answer = "y")
    {
        hooks ??= new DeleteHooks();
        var userBeforeDisposition = hooks.BeforeDisposition;
        var guarded = new DeleteHooks
        {
            BeforeOpen = hooks.BeforeOpen,
            AfterOpen = hooks.AfterOpen,
            DuringCompare = hooks.DuringCompare,
            BeforeFinalCheck = hooks.BeforeFinalCheck,
            BeforeDisposition = (entry, handle) =>
            {
                userBeforeDisposition?.Invoke(entry, handle);
                guard.Check(handle);
            },
        };
        var probe = new RecordingDeletionProbe(new WindowsFileSystemProbe());
        var output = new StringWriter();
        var error = new StringWriter();
        var outcome = DeleteCommand.Run(new DeleteCommandRequest(
            zipPath, target, mode, entriesPath, AssumeYes: false, probe, new ScriptedPrompt(answer, awaiting), Context(null, output, error), Hooks: guarded));
        Assert.Empty(guard.Violations);
        return new Result(outcome.Status, null, outcome.Report, outcome.PrepareError, output.ToString(), error.ToString()) { Probe = probe };
    }

    private static CommandContext Context(Limits? limits, TextWriter output, TextWriter error)
    {
        var locations = ProtectedLocations.Resolve();
        Assert.True(locations.Policy is not null, locations.Error?.Describe());
        return new CommandContext(
            new WindowsFileSystemProbe(),
            () => new TargetLocationPolicyResult(locations.Policy, locations.Error),
            limits ?? Limits.Default,
            output,
            error,
            OpenRarArchive: OpenRar);
    }

    // RAR を開く関数 (CLI の RarArchives.Open と同じ RarArchiveSource.Open)。製品は実行中の exe のフォルダーの DLL を読むが、
    // テストホストの exe のフォルダーには置かないので、採用版の DLL (UnrarTestDll) をテスト用のローダーで読む。
    // ZIP の実行では呼ばれない (DLL が無くても ZIP のテストは影響を受けない)。
    private static readonly Lazy<UnrarLibrary> RarLibrary = new(() => new UnrarLibrary(UnrarTestDll.Location));

    public static ZipOpenResult OpenRar(string path, Limits limits) => RarArchiveSource.Open(path, limits, () => RarLibrary.Value.Load());

    // ZIP を path に書く (FileMode.CreateNew: 既存のファイルを上書きしない)。
    public static string WriteZip(string path, byte[] zip)
    {
        using (var stream = new FileStream(path, FileMode.CreateNew))
        {
            stream.Write(zip);
        }

        return path;
    }

    public static byte[] Zip(params (string Name, byte[]? Content)[] entries) =>
        ZipFixture.Create(entries.Select(e => new FixtureEntry(e.Name, e.Content)));

    public static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    // 削除対象の分類 (Strict は MATCHED、Fast は SAME_SIZE。docs/TESTING.md#principles のモード適用)。
    public static Classification Candidate(RunMode mode) => mode == RunMode.Fast ? Classification.SameSize : Classification.Matched;

    // ディレクトリ配下の全項目の (相対パス → 種類・サイズ・SHA-256・更新日時)。
    public static SortedDictionary<string, string> Snapshot(string directory)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, path);
            var info = new FileInfo(path);
            result[relative] = (info.Attributes & FileAttributes.Directory) != 0
                ? $"dir {info.LastWriteTimeUtc.Ticks}"
                : $"{info.Length} {Hash(path)} {info.LastWriteTimeUtc.Ticks}";
        }

        return result;
    }

    public static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return System.Convert.ToHexString(SHA256.HashData(stream));
    }

    // 内容の検証の代表 fixture (テスト C01・C02・C03・C07)。Core のテスト ContentVerificationTests と同じ作り方。
    public static readonly byte[] Data = MakeData(7000);

    public static (byte[] Zip, FatalKind Expected) Broken(string id)
    {
        var deflate = ZipFixture.Create(new FixtureEntry("x.bin", Data));
        switch (id)
        {
            case "C01":
                var crc = new ZipPatcher(deflate).GetCrc32(0);
                return (new ZipPatcher(deflate).SetCrc32(0, crc ^ 0xFFFFFFFF).ToArray(), FatalKind.ContentCrcMismatch);
            case "C02":
                return (CorruptDeflateMidway(deflate), FatalKind.ContentReadFailed);
            case "C03":
                var half = (uint)(new ZipPatcher(deflate).GetCompressedSize(0) / 2);
                return (new ZipPatcher(deflate).SetCompressedSize(0, half).ToArray(), FatalKind.ContentTooShort);
            case "C07":
                return (new ZipPatcher(deflate).SetEncryptedFlag(0).ToArray(), FatalKind.ContentEncrypted);
            default:
                throw new ArgumentOutOfRangeException(nameof(id), id, null);
        }
    }

    private static byte[] CorruptDeflateMidway(byte[] deflate)
    {
        var compressed = (int)new ZipPatcher(deflate).GetCompressedSize(0);
        for (var offset = compressed / 2; offset < compressed; offset++)
        {
            var candidate = new ZipPatcher(deflate).CorruptData(0, offset).ToArray();
            try
            {
                using var archive = new ZipArchive(new MemoryStream(candidate), ZipArchiveMode.Read);
                using var stream = archive.Entries[0].Open();
                stream.CopyTo(Stream.Null);
            }
            catch (InvalidDataException)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("no corruption offset produced InvalidDataException");
    }

    private static byte[] MakeData(int length)
    {
        var random = new Random(20261002);
        string[] words = ["unextract", "zip", "target", "matched", "modified", "missing", "crc", "deflate", "ntfs", "handle"];
        var text = new System.Text.StringBuilder();
        while (text.Length < length)
        {
            text.Append(words[random.Next(words.Length)]).Append(random.Next(1000)).Append(' ');
        }

        return System.Text.Encoding.ASCII.GetBytes(text.ToString(0, length));
    }
}
