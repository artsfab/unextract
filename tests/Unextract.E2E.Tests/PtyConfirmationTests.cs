using Unextract.Core.Tests.Fixtures;

namespace Unextract.E2E.Tests;

[CollectionDefinition("PTY confirmation", DisableParallelization = true)]
public sealed class PtyConfirmationCollection;

// X28 (旧 M08 / O07 の PTY テストの置き換え): delete の対話実行で、確認の表示後に n を送る (Strict、Fast)。偽 prompt の経路、
// --yes / 非対話は Core・E2E のテストが担当する。
[Collection("PTY confirmation")]
public sealed class PtyConfirmationTests
{
    // PLAN.md §5.2・§5.4 の指定を独立した期待値として保持する (製品定数は参照しない)。
    private const string Warning =
        "警告: --fast のため、パスとサイズだけで判定しています。内容が一致することと、ZIP から正常に展開できることは確認していません。";

    private const string Prompt =
        "最大 5 件のファイルエントリを1件ずつ検証し、条件を満たしたものをその場で完全に削除します。\n"
        + "途中で停止した場合、それまでに削除したファイルは元に戻りません。\n"
        + "続行しますか? ";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task X28_InteractiveDeleteWarningAndCancelWithN(bool fast)
    {
        var fixtureName = $"X28_yn-n_{(fast ? "Fast" : "Strict")}";
        var fixture = E2EFixture.Create(fixtureName);
        using var timeout = new CancellationTokenSource(UnextractProcess.Timeout);
        PtyProcess? terminal = null;
        string? failure = null;
        try
        {
            fixture.WriteZip(ZipFixture.Create(
                new FixtureEntry("same1.txt", E2EFixture.Bytes("hello1")),
                new FixtureEntry("same2.txt", E2EFixture.Bytes("hello2")),
                new FixtureEntry("docs/"),
                new FixtureEntry("docs/deep.txt", E2EFixture.Bytes("deep")),
                new FixtureEntry("changed.txt", E2EFixture.Bytes("hello")),
                new FixtureEntry("missing.txt", E2EFixture.Bytes("x"))));
            fixture.WriteTarget("same1.txt", "hello1")
                .WriteTarget("same2.txt", "hello2")
                .WriteTarget("docs/deep.txt", "deep")
                .WriteTarget("changed.txt", "hellO")
                .WriteTarget("unrelated.txt", "not in zip");
            var before = E2EFixture.Snapshot(fixture.Directory);
            terminal = await PtyProcess.StartAsync(fixture, fast, timeout.Token);
            await terminal.WaitForAsync("[y/N]", timeout.Token);
            var output = terminal.Output;

            // 入力前に、非対話中止でなく実際に prompt が出たことを検証する。
            Assert.Contains("[y/N]", output, StringComparison.Ordinal);
            Assert.Contains("対象: 全 6 エントリ\n", output, StringComparison.Ordinal);
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

            // 確認は最初のエントリの処理の前 (案 A): 結果行はまだ出ていない。
            Assert.Empty(Report.Parse(output).AllEntries);
            Assert.DoesNotContain("unrelated.txt", output, StringComparison.Ordinal);
            await terminal.SendNoAsync(timeout.Token);
            await terminal.WaitForAsync("中止しました。削除0件。", timeout.Token);
            Assert.Equal(2, await terminal.WaitForExitAsync(timeout.Token));
            Assert.Contains("中止しました。削除0件。", terminal.Output, StringComparison.Ordinal);
            Assert.Equal(fast ? 2 : 0, terminal.Output.Split(Warning, StringSplitOptions.None).Length - 1);
            Assert.Equal(before, E2EFixture.Snapshot(fixture.Directory));
        }
        catch (Exception e)
        {
            failure = $"{e}\nfixture: {fixture.Directory}\nmode: {(fast ? "Fast" : "Strict")}\n" +
                (terminal?.Diagnostics ?? "PTY 未起動。");
        }
        finally
        {
            try
            {
                if (terminal is not null)
                {
                    await terminal.DisposeAsync();
                }

                // Create が返した今回の fixture だけを削除する。共有 fixtures ルートや過去の fixture は削除しない。
                // PTY の後始末が失敗した場合はここに進まない (target のハンドルが残っている可能性がある)。
                var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));
                var path = Path.GetFullPath(fixture.Directory);
                var name = Path.GetFileName(path);
                var prefix = fixtureName + "-";
                if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase) ||
                    !name.StartsWith(prefix, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(name[prefix.Length..], "N", out _))
                {
                    throw new InvalidOperationException($"fixture の削除範囲を確認できない: {path}");
                }

                Directory.Delete(path, recursive: true);
            }
            catch (Exception e)
            {
                // 本体の失敗を保持し、cleanup だけが失敗した場合もテストを失敗させる。
                failure += $"\nPTY / fixture 後始末失敗: {e}\nfixture: {fixture.Directory}\n{terminal?.Diagnostics}";
            }
        }

        if (failure is not null)
        {
            Assert.Fail(failure);
        }
    }
}
