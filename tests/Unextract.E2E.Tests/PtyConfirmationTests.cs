using Unextract.Core.Tests.Fixtures;

namespace Unextract.E2E.Tests;

[CollectionDefinition("PTY confirmation", DisableParallelization = true)]
public sealed class PtyConfirmationCollection;

// 実端末 (ConPTY) の delete の確認入力。X28 (旧 M01・M08): 確認の表示後に n (Strict、Fast) と Fast の警告の順序。
// X29 (旧 M02・M03): Enter だけ (中止) と y (逐次処理の開始) を Strict で。確認待ちの Ctrl+C (旧 M04) は、どちらの経路でも削除が始まらない
// (プロセスの終了か、回答 null の中止) ので、Core の P10 (回答 null) が担う。偽 prompt の経路、--yes / 非対話は Core・E2E のテストが担当する。
[Collection("PTY confirmation")]
public sealed class PtyConfirmationTests
{
    // docs/spec/cli.md#warning の指定を独立した期待値として保持する (製品定数は参照しない)。
    private const string Warning =
        "警告: --fast のため、パスとサイズだけで判定しています。内容が一致することと、ZIP から正常に展開できることは確認していません。";

    private const string Prompt =
        "最大 5 件のファイルエントリを1件ずつ検証し、条件を満たしたものをその場で完全に削除します。\n"
        + "途中で停止した場合、それまでに削除したファイルは元に戻りません。\n"
        + "続行しますか? ";

    // same1・same2・docs/deep が一致、changed は同じサイズで内容違い、missing は target に無い、unrelated は ZIP に無い。
    private static E2EFixture CreateFixture()
    {
        var fixture = E2EFixture.Create();
        fixture.WriteZip(ZipFixture.Create(
            new FixtureEntry("same1.txt", E2EFixture.Bytes("hello1")),
            new FixtureEntry("same2.txt", E2EFixture.Bytes("hello2")),
            new FixtureEntry("docs/"),
            new FixtureEntry("docs/deep.txt", E2EFixture.Bytes("deep")),
            new FixtureEntry("changed.txt", E2EFixture.Bytes("hello")),
            new FixtureEntry("missing.txt", E2EFixture.Bytes("x"))));
        return fixture.WriteTarget("same1.txt", "hello1")
            .WriteTarget("same2.txt", "hello2")
            .WriteTarget("docs/deep.txt", "deep")
            .WriteTarget("changed.txt", "hellO")
            .WriteTarget("unrelated.txt", "not in zip");
    }

    // 確認の表示までを待ち、入力前に、非対話中止でなく実際に prompt が出たことと、確認が最初のエントリの処理の前 (案 A) であることを検証する。
    private static async Task<string> WaitForPromptAsync(PtyProcess terminal, CancellationToken token)
    {
        await terminal.WaitForAsync("[y/N]", token);
        var output = terminal.Output;
        Assert.Contains("対象: 全 6 エントリ\n", output, StringComparison.Ordinal);
        Assert.Empty(Report.Parse(output).AllEntries);
        Assert.DoesNotContain("unrelated.txt", output, StringComparison.Ordinal);
        return output;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task X28_InteractiveDeleteWarningAndCancelWithN(bool fast)
    {
        var fixture = CreateFixture();
        var before = E2EFixture.Snapshot(fixture.Directory);
        await PtyProcess.RunAsync($"fixture: {fixture.Directory}\nmode: {(fast ? "Fast" : "Strict")}",
            token => PtyProcess.StartDeleteAsync(fixture, fast, token), async (terminal, token) =>
            {
                var output = await WaitForPromptAsync(terminal, token);
                var prompt = output.IndexOf("[y/N]", StringComparison.Ordinal);
                if (fast)
                {
                    // ヘッダーの先頭行と確認の直前の行の計2回。最後の警告から確認文・[y/N] までに別の出力が無い。
                    Assert.Equal(2, output.Split(Warning, StringSplitOptions.None).Length - 1);
                    Assert.StartsWith(Warning + "\nArchive: ", output.TrimStart(), StringComparison.Ordinal);
                    var lastWarning = output.LastIndexOf(Warning, StringComparison.Ordinal);
                    Assert.Equal(Warning + "\n" + Prompt, output[lastWarning..prompt]);
                }
                else
                {
                    Assert.DoesNotContain("警告:", output, StringComparison.Ordinal);
                    Assert.DoesNotContain("--fast", output, StringComparison.Ordinal);
                    Assert.EndsWith("\n" + Prompt, output[..prompt], StringComparison.Ordinal);
                }

                await terminal.SendLineAsync("n", token);
                await terminal.WaitForAsync("中止しました。削除0件。", token);
                Assert.Equal(2, await terminal.WaitForExitAsync(token));
                Assert.Equal(fast ? 2 : 0, terminal.Output.Split(Warning, StringSplitOptions.None).Length - 1);
                Assert.Equal(before, E2EFixture.Snapshot(fixture.Directory));
            });
    }

    // X29: Enter だけ → 中止・削除0件・終了 2・target 不変。y → 逐次処理を始め、全バイト一致した3件だけを削除し、結果行を ZIP の順に
    // 1件ずつ出す (docs/ の DIRECTORY は行を出さない)。changed.txt・unrelated.txt・docs\・ZIP は残る。終了 0。
    [Theory]
    [InlineData("")]
    [InlineData("y")]
    public async Task X29_InteractiveDeleteWithEnterOrY(string answer)
    {
        var fixture = CreateFixture();
        var before = E2EFixture.Snapshot(fixture.Directory);
        await PtyProcess.RunAsync($"fixture: {fixture.Directory}\nanswer: \"{answer}\"",
            token => PtyProcess.StartDeleteAsync(fixture, fast: false, token), async (terminal, token) =>
            {
                await WaitForPromptAsync(terminal, token);
                await terminal.SendLineAsync(answer, token);
                var exit = await terminal.WaitForExitAsync(token);
                var output = terminal.OutputWithoutProgress;
                if (answer.Length == 0)
                {
                    Assert.Equal(2, exit);
                    Assert.Contains("中止しました。削除0件。", output, StringComparison.Ordinal);
                    Assert.Empty(Report.Parse(output).AllEntries);
                    Assert.Equal(before, E2EFixture.Snapshot(fixture.Directory));
                    return;
                }

                Assert.Equal(0, exit);
                var report = Report.Parse(output);
                Assert.Equal(["DELETED", "DELETED", "DELETED", "MODIFIED", "MISSING"], report.StatusesInOrder);
                Assert.Equal(["same1.txt", "same2.txt", "docs/deep.txt", "changed.txt", "missing.txt"], report.AllEntries);
                Assert.Contains("要約: 削除済み 3、MODIFIED 1、MISSING 1、SKIPPED_SPECIAL_FILE 0、DIRECTORY 1、DELETE_FAILED 0、処理対象外 0、未処理 0",
                    output, StringComparison.Ordinal);

                // 削除で更新日時が変わる親ディレクトリ (target、target\docs) は存在だけを確かめる。
                var after = E2EFixture.Snapshot(fixture.Directory);
                foreach (var removed in new[] { @"target\same1.txt", @"target\same2.txt", @"target\docs\deep.txt" })
                {
                    Assert.True(before.Remove(removed), removed);
                }

                foreach (var directory in new[] { "target", @"target\docs" })
                {
                    Assert.True(before.Remove(directory) && after.Remove(directory), directory);
                }

                Assert.Equal(before, after);
            });
    }
}
