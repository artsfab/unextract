using System.Text;
using Unextract.Core.Tests.Fixtures;

namespace Unextract.E2E.Tests;

// D35・D36: delete --prune-dirs を exe (通常 build と publish 版) で確かめる (docs/spec/filesystem.md#prune-dirs)。
// - D35: --entries なしの人間向け出力。ヘッダーのフォルダー行、ファイルの結果行の後の深い順のフォルダーの結果行 (DIR_DELETED・DIR_KEPT
//   (空でない)・DIR_MISSING、もとから空の明示ディレクトリの削除)、要約の次のフォルダーの要約、終了 0、実際に消えたディレクトリ。
// - D36: --entries ありの JSONL (Strict/Fast)。run.prune_dirs・dirs_selected、entry の後の dir (選んだファイルの祖先だけ)、
//   counts の dir_*、ログとの byte 一致、選んだファイルの祖先でない空のディレクトリが残ること。
// 期待値は仕様の書式から組み立て、製品の表示関数を参照しない。
public sealed class PruneE2ETests
{
    private static string Line(string status, string target) => $"{status,-20}  {target}";

    // a/b/x.txt は一致、a/c.txt は変更、e/ は明示ディレクトリ (target ではもとから空)、m/y.txt は不在。
    private static E2EFixture Fixture()
    {
        var fixture = E2EFixture.Create().WriteZip(ZipFixture.Create(
            new FixtureEntry("a/b/x.txt", E2EFixture.Bytes("same")),
            new FixtureEntry("a/c.txt", E2EFixture.Bytes("same")),
            new FixtureEntry("e/"),
            new FixtureEntry("m/y.txt", E2EFixture.Bytes("same"))));
        fixture.WriteTarget("a/b/x.txt", "same").WriteTarget("a/c.txt", "diff!").CreateTargetDirectory("e").WriteTarget("z.txt", "not in zip");
        return fixture;
    }

    // D35: 人間向け出力の行と、実際に削除されたフォルダー。
    [Fact]
    public void D35_HumanOutputListsFoldersDeepestFirstAndRemovesOnlyEmptyOnes()
    {
        var fixture = Fixture();
        var zip = E2EFixture.Describe(fixture.ArchivePath);

        var result = fixture.RunDeleting(stdin: null, "--prune-dirs", "--yes");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.StandardError);
        string t = fixture.Target;
        Assert.Equal(
            [
                $"Archive: {fixture.ArchivePath}",
                $"Target:  {t}",
                "Mode:    Strict",
                "凡例: Target は target 内の対応する場所です。MISSING の場合は実在しない期待位置を示します。Target は確認用で、--entries には Entry を書きます。",
                "対象: 全 4 エントリ",
                "フォルダー: 空のフォルダーも削除します (--prune-dirs、対象 4 件)",
                "Status                Entry -> Target",
                $@"DELETED               a/b/x.txt -> {t}\a\b\x.txt",
                $@"MODIFIED              a/c.txt -> {t}\a\c.txt",
                $@"MISSING               m/y.txt -> {t}\m\y.txt",
                Line("DIR_DELETED", $@"{t}\a\b"),
                Line("DIR_KEPT", $@"{t}\a") + " (空でない)",
                Line("DIR_DELETED", $@"{t}\e"),
                Line("DIR_MISSING", $@"{t}\m"),
                "要約: 削除済み 1、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 0、DIRECTORY 1、DELETE_FAILED 0、処理対象外 0、未処理 0",
                "フォルダー: 削除済み 2、DIR_KEPT 1、DIR_MISSING 1、未処理 0",
            ],
            result.OutputLines);

        Assert.Equal([@"a", @"a\c.txt", "z.txt"], E2EFixture.Snapshot(t).Keys);
        Assert.Equal(zip, E2EFixture.Describe(fixture.ArchivePath));
    }

    // D36: JSONL とログ。--entries では選んだファイルの祖先だけが対象。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task D36_JsonlReportsDirRecordsAfterEntriesAndTheLogMatches(bool fast)
    {
        var fixture = Fixture();
        string list = Path.Combine(fixture.Directory, "entries.txt");
        File.WriteAllBytes(list, Encoding.UTF8.GetBytes("a/b/x.txt\n"));
        string log = Path.Combine(fixture.Directory, "run.jsonl");
        string[] options = [.. fast ? new[] { "--fast" } : [], "--entries", list, "--prune-dirs", "--yes", "--log", log];

        using var process = new MachineProcess(fixture, "delete", options);
        var result = await process.Complete();

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        var records = result.Records;
        Assert.Equal(["run", "entry", "dir", "dir", "result"], records.Select(r => r.GetProperty("type").GetString()));
        Assert.All(records, r => Assert.Equal(2, r.GetProperty("v").GetInt32()));
        var run = records[0];
        Assert.True(run.GetProperty("prune_dirs").GetBoolean());
        Assert.Equal(2, run.GetProperty("dirs_selected").GetInt32());
        Assert.Equal(1, run.GetProperty("selected").GetInt32());
        Assert.Equal("DELETED", records[1].GetProperty("status").GetString());
        Assert.Equal(@"a\b", records[2].GetProperty("path").GetString());
        Assert.Equal("DIR_DELETED", records[2].GetProperty("status").GetString());
        Assert.False(records[2].TryGetProperty("keep_reason", out _));
        Assert.Equal("a", records[3].GetProperty("path").GetString());
        Assert.Equal("DIR_KEPT", records[3].GetProperty("status").GetString());
        Assert.Equal("NOT_EMPTY", records[3].GetProperty("keep_reason").GetString());
        var end = records[4];
        Assert.Equal("completed", end.GetProperty("outcome").GetString());
        var counts = end.GetProperty("counts");
        Assert.Equal(1, counts.GetProperty("deleted").GetInt32());
        Assert.Equal(3, counts.GetProperty("not_selected").GetInt32());
        Assert.Equal(1, counts.GetProperty("dir_deleted").GetInt32());
        Assert.Equal(1, counts.GetProperty("dir_kept").GetInt32());
        Assert.Equal(0, counts.GetProperty("dir_missing").GetInt32());
        Assert.Equal(0, counts.GetProperty("dir_unprocessed").GetInt32());
        Assert.Equal(result.Output, File.ReadAllBytes(log));

        // e/ (もとから空) は選んだファイルの祖先ではないので残る。
        Assert.Equal([@"a", @"a\c.txt", "e", "z.txt"], E2EFixture.Snapshot(fixture.Target).Keys);
    }
}
