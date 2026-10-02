using System.IO.Compression;
using System.Security.Cryptography;
using Unextract.Core.Analysis;
using Unextract.Core.Deletion;
using Unextract.Core.Results;
using Unextract.Core.Target;
using Unextract.Core.Tests.Fixtures;
using Unextract.Core.Zip;

namespace Unextract.Windows.Tests.Integration;

// 実 ZIP と実 NTFS の target で、Windows の probe を接続した runner を実行する。
// Run は削除しない (dry-run、または通常実行で確認に n と答える)。RunDeleting は実際に削除する: 削除フェーズに
// DeletionGuard を組み込み、削除の指示の直前に毎回、削除用ハンドルの最終パスが fixture 内であることを確かめる。
internal static class RealRun
{
    // 確認で answer を返す。返す前に awaiting (確認待ち中の変更の注入) を呼ぶ。
    private sealed class ScriptedPrompt(string? answer, Action? awaiting = null) : IConfirmationPrompt
    {
        public int AskCount { get; private set; }

        public bool IsInteractive => true;

        public string? Ask(string prompt)
        {
            AskCount++;
            awaiting?.Invoke();
            return answer;
        }
    }

    // 削除しない実行で削除フェーズに入ったら失敗にする。
    private sealed class NoDeletion : IDeletionPhase
    {
        public DeletionReport Delete(DeletionRequest request) => throw new InvalidOperationException("削除フェーズには入らない");
    }

    public sealed record Result(RunOutcome Outcome, string Output, string Error = "")
    {
        public DeletionReport Deletion => Outcome.Deletion ?? throw new InvalidOperationException(Output + Error);

        public IEnumerable<string> DeletedNames => Deletion.Deleted.Select(e => e.Name);

        public AnalysisResult Analysis => Outcome.Analysis ?? throw new InvalidOperationException(Output);

        public Classification Of(string name) => Analysis.Results.Single(r => r.Entry.Name == name).Classification;

        public SkipReason? SkipOf(string name) => Analysis.Results.Single(r => r.Entry.Name == name).SkipReason;
    }

    // mode は実行全体のモード (SPEC §15)。共通の安全性テストを Fast でも実行するために切り替える。
    // hooks は runner のテスト用の差し込み口 (解析完了直後など。実行中の変更の注入に使う)。
    public static Result Run(string zipPath, string target, bool dryRun = true, Limits? limits = null, RunMode mode = RunMode.Strict, RunHooks? hooks = null)
    {
        var opened = ZipArchiveSource.Open(zipPath);
        using var source = opened.Source ?? throw new InvalidOperationException(opened.Fatal!.Describe());
        var policy = ProtectedLocations.Resolve();
        Assert.True(policy.Policy is not null, policy.Error?.Describe());
        var output = new StringWriter();
        var error = new StringWriter();
        var outcome = UnextractRunner.Run(new RunRequest(
            source, zipPath, target, dryRun, AssumeYes: false, new WindowsFileSystemProbe(), policy.Policy,
            limits ?? Limits.Default, new ScriptedPrompt("n"), new NoDeletion(), output, error, Hooks: hooks, Mode: mode));
        return new Result(outcome, output.ToString(), error.ToString());
    }

    // 実際に削除する実行。確認には y と答え、その直前に awaiting を呼ぶ (確認待ち中の変更)。
    // hooks.BeforeDisposition の後に guard.Check を必ず呼ぶ (違反なら例外 → 削除フェーズはその対象で停止し、削除しない)。
    public static Result RunDeleting(
        string zipPath, string target, DeletionGuard guard, Action? awaiting = null, DeletionHooks? hooks = null, RunMode mode = RunMode.Strict)
    {
        var opened = ZipArchiveSource.Open(zipPath);
        using var source = opened.Source ?? throw new InvalidOperationException(opened.Fatal!.Describe());
        var policy = ProtectedLocations.Resolve();
        Assert.True(policy.Policy is not null, policy.Error?.Describe());
        hooks ??= new DeletionHooks();
        var userBeforeDisposition = hooks.BeforeDisposition;
        var guarded = new DeletionHooks
        {
            BeforeOpen = hooks.BeforeOpen,
            AfterRevalidation = hooks.AfterRevalidation,
            DuringRecompare = hooks.DuringRecompare,
            BeforeFinalCheck = hooks.BeforeFinalCheck,
            BeforeDisposition = (file, handle) =>
            {
                userBeforeDisposition?.Invoke(file, handle);
                guard.Check(handle);
            },
        };
        var probe = new WindowsFileSystemProbe();
        var prompt = new ScriptedPrompt("y", awaiting);
        var output = new StringWriter();
        var error = new StringWriter();
        var outcome = UnextractRunner.Run(new RunRequest(
            source, zipPath, target, DryRun: false, AssumeYes: false, probe, policy.Policy, Limits.Default, prompt,
            new DeletionPhase(probe, guarded), output, error, Mode: mode));
        Assert.Empty(guard.Violations);
        return new Result(outcome, output.ToString(), error.ToString());
    }

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

    // 削除候補の分類 (Strict は MATCHED、Fast は SAME_SIZE。PLAN_TESTS のモード違いの再利用の原則)。
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
