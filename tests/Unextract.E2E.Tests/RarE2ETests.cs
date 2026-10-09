using System.Text;
using System.Text.Json;
using Unextract.Core.Tests.Fixtures;

namespace Unextract.E2E.Tests;

// テスト U70〜U73: RAR の E2E (X/J 系の RAR 版。docs/TESTING.md#rar)。製品は UnRAR64.dll を実行中の exe と同じフォルダーからだけ読み、DLL は
// 製品に同梱しない (docs/spec/rar.md#pinning)。そこで、テスト対象の exe のフォルダーの直下のファイルを fixture の下へ複製し、隣に
// 採用版の DLL (UnrarTestDll。リポジトリ外) を置いた配置で RAR を実行する (利用者が DLL を置いた状態の再現)。元の exe のフォルダーには
// DLL を置かず、DLL の無い配置として使う。RAR はテスト専用の生成器 (RarWriter、Stored) で作る。fixture は各テストの終了後、DLL を置いた配置はクラスの全テストの後に削除する。
public sealed class RarE2ETests(RarPlacementWithLibrary placement) : IClassFixture<RarPlacementWithLibrary>
{
    private const int Success = 0;
    private const int Error = 1;

    private static readonly byte[] A = E2EFixture.Bytes("alpha content");
    private static readonly byte[] B = E2EFixture.Bytes("bravo content");

    private static string WriteRar(E2EFixture fixture, byte[] rar, string name = "archive.rar")
    {
        var path = Path.Combine(fixture.Directory, name);
        using var stream = new FileStream(path, FileMode.CreateNew);
        stream.Write(rar);
        return path;
    }

    private static Rar5File File5(string name, byte[] data) => new() { Name = name, Data = data };

    private static ProcessResult Run(string exe, E2EFixture fixture, string archive, string command, string? stdin, params string[] options) =>
        UnextractProcess.RunExe(exe, fixture.Directory, stdin, [command, archive, "--target", fixture.Target, .. options]);

    // U70: 配布物・通常 build の出力に UnRAR64.dll は無い (同梱しない)。DLL の無い配置では RAR だけが Prepare の FATAL
    // (RAR_LIBRARY_UNAVAILABLE、削除0件、target・entries に触れない) になり、同じ exe で ZIP は成功する。
    [Theory]
    [InlineData("analyze")]
    [InlineData("delete")]
    public void U70_WithoutLibrary_RarIsFatal_ZipWorks(string command)
    {
        var exe = UnextractProcess.ExePath;
        var library = Path.Combine(Path.GetDirectoryName(exe)!, "UnRAR64.dll");
        Assert.False(File.Exists(library), $"exe のフォルダーに UnRAR64.dll がある (同梱しない): {library}");
        var fixture = E2EFixture.Create().WriteTarget("a.txt", A);
        var rar = WriteRar(fixture, Rar5Writer.Build([File5("a.txt", A)]));
        var entries = Path.Combine(fixture.Directory, "entries.txt");
        File.WriteAllBytes(entries, E2EFixture.Bytes("a.txt\n"));
        var before = E2EFixture.Snapshot(fixture.Directory);
        string[] options = command == "delete" ? ["--yes", "--entries", entries] : [];
        if (command == "delete")
        {
            fixture.CheckGuard();
        }

        var result = Run(exe, fixture, rar, command, null, options);

        Assert.True(result.ExitCode == Error, result.ToString());
        Assert.Equal(
            $"FATAL: UnRAR.dll を使用できないため、RAR を処理できません (見つかりません)。{library} に UnRAR.dll 7.23 (x64) の UnRAR64.dll を置いてください。ZIP の処理には影響しません。",
            result.ErrorLines[0]);
        Assert.Equal(before, E2EFixture.Snapshot(fixture.Directory));

        var machine = Run(exe, fixture, rar, command, null, [.. options, "--jsonl"]);
        var record = JsonDocument.Parse(machine.OutputLines[^1]).RootElement;
        var error = record.GetProperty("error");
        Assert.Equal("prepare", error.GetProperty("stage").GetString());
        Assert.Equal("RAR_LIBRARY_UNAVAILABLE", error.GetProperty("code").GetString());
        Assert.Equal(before, E2EFixture.Snapshot(fixture.Directory));

        fixture.WriteZip(ZipFixture.Create([new FixtureEntry("a.txt", A)]));
        var zip = command == "delete" ? fixture.RunDeleting(null, "--yes") : fixture.Run(command, null);
        Assert.True(zip.ExitCode == Success, zip.ToString());
        Assert.Equal(command == "analyze", File.Exists(Path.Combine(fixture.Target, "a.txt")));
    }

    // U71: DLL を置いた配置で analyze (人間向け) → target と RAR が不変。名前の区切りは \。
    [Fact]
    public void U71_Analyze_WithLibrary()
    {
        var fixture = E2EFixture.Create().WriteTarget("same.txt", A).WriteTarget(@"d\deep.txt", B).WriteTarget("changed.txt", "bravo CONTENT");
        var rar = WriteRar(fixture, Rar5Writer.Build(
            [File5("same.txt", A), new Rar5File { Name = "d", IsDirectory = true, Attributes = 0x10 }, File5("d/deep.txt", B), File5("changed.txt", B), File5("missing.txt", A)]));
        var before = E2EFixture.Snapshot(fixture.Target);
        var archive = E2EFixture.Describe(rar);

        var result = Run(placement.Exe, fixture, rar, "analyze", null);

        Assert.True(result.ExitCode == Success, result.ToString());
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Contains($@"MATCHED               same.txt -> {fixture.Target}\same.txt", result.OutputLines);
        Assert.Contains($@"MATCHED               d\deep.txt -> {fixture.Target}\d\deep.txt", result.OutputLines);
        Assert.Contains($@"MODIFIED              changed.txt -> {fixture.Target}\changed.txt", result.OutputLines);
        Assert.Contains($@"MISSING               missing.txt -> {fixture.Target}\missing.txt", result.OutputLines);
        Assert.Contains($@"DIRECTORY             d\ -> {fixture.Target}\d", result.OutputLines);
        Assert.Equal(before, E2EFixture.Snapshot(fixture.Target));
        Assert.Equal(archive, E2EFixture.Describe(rar));
    }

    // U72: JSONL の name (区切り \) をそのまま --entries に書いて delete する。--jsonl の出力と --log がバイト一致し、選択した一致だけを削除する。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void U72_JsonNames_ToEntries_ToDelete(bool fast)
    {
        var fixture = E2EFixture.Create()
            .WriteTarget("a.txt", A).WriteTarget(@"d\b.txt", B).WriteTarget(@"d\keep.txt", A);
        var rar = WriteRar(fixture, Rar5Writer.Build(
            [File5("a.txt", A), new Rar5File { Name = "d", IsDirectory = true, Attributes = 0x10 }, File5("d/b.txt", B), File5("d/keep.txt", A)]));
        string[] mode = fast ? ["--fast"] : [];

        var analyzed = Run(placement.Exe, fixture, rar, "analyze", null, [.. mode, "--jsonl"]);

        Assert.True(analyzed.ExitCode == Success, analyzed.ToString());
        var names = analyzed.OutputLines.Select(line => JsonDocument.Parse(line).RootElement)
            .Where(r => r.GetProperty("type").GetString() == "entry")
            .Select(r => r.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(["a.txt", @"d\", @"d\b.txt", @"d\keep.txt"], names);
        var entries = Path.Combine(fixture.Directory, "entries.txt");
        File.WriteAllText(entries, $"{names[0]}\n{names[2]}\n", new UTF8Encoding(false));
        var log = Path.Combine(fixture.Directory, "run.jsonl");
        fixture.CheckGuard();

        var deleted = Run(placement.Exe, fixture, rar, "delete", null, [.. mode, "--yes", "--entries", entries, "--jsonl", "--log", log]);

        Assert.True(deleted.ExitCode == Success, deleted.ToString());
        Assert.Equal(Encoding.UTF8.GetBytes(deleted.StandardOutput), File.ReadAllBytes(log));
        Assert.False(File.Exists(Path.Combine(fixture.Target, "a.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Target, @"d\b.txt")));
        Assert.True(File.Exists(Path.Combine(fixture.Target, @"d\keep.txt")));
    }

    // U73: DLL は作業ディレクトリに何も書かない (空の作業ディレクトリが空のまま)。target とアーカイブのフォルダーも、削除以外は不変。
    [Fact]
    public void U73_NothingWrittenToWorkingDirectory()
    {
        var fixture = E2EFixture.Create().WriteTarget("a.txt", A).WriteTarget("b.txt", B);
        var rar = WriteRar(fixture, Rar5Writer.Build([File5("a.txt", A), File5("b.txt", B), File5("c.txt", A)]));
        var cwd = Directory.CreateDirectory(Path.Combine(fixture.Directory, "cwd")).FullName;
        fixture.CheckGuard();

        var analyzed = UnextractProcess.RunExe(placement.Exe, cwd, null, "analyze", rar, "--target", fixture.Target);
        var deleted = UnextractProcess.RunExe(placement.Exe, cwd, null, "delete", rar, "--target", fixture.Target, "--yes");

        Assert.True(analyzed.ExitCode == Success, analyzed.ToString());
        Assert.True(deleted.ExitCode == Success, deleted.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(cwd));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Target));
        Assert.Equal(["archive.rar", "cwd", "target"], Directory.EnumerateFileSystemEntries(fixture.Directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }
}

// テスト対象の exe のフォルダーの直下のファイルを複製し、UnRAR64.dll を隣に置いた配置 (このクラスのテストで共有する1つ)。
// テストメソッドより寿命が長いので、所有者 (FixtureOwner) を自分で持ち、クラスの全テストの後 (Dispose) に削除する。
public sealed class RarPlacementWithLibrary : IDisposable
{
    private readonly FixtureOwner _owner = new("rar-app");
    private readonly Lazy<string> _exe;

    public RarPlacementWithLibrary() => _exe = new(Create);

    public string Exe => _exe.Value;

    private string Create()
    {
        var source = Path.GetDirectoryName(UnextractProcess.ExePath)!;
        var app = _owner.Create();
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(app, Path.GetFileName(file)), overwrite: false);
        }

        UnrarTestDll.CopyTo(app);
        return Path.Combine(app, Path.GetFileName(UnextractProcess.ExePath));
    }

    public void Dispose() => _owner.Cleanup();
}
