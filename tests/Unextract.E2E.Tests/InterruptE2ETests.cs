using System.Diagnostics;
using System.Text;
using Unextract.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace Unextract.E2E.Tests;

// X32 (旧 M10): delete の逐次処理の途中で、自分が起動した exe を強制終了する (Ctrl+C の実入力・OS の制御イベントではない)。
// 通常出力 (delete --yes) と JSONL (delete --jsonl --yes --log) × Strict と Fast。判定 (docs/spec/cli.md#interruption、
// docs/spec/machine-output.md#boundary) は、EOF まで回収した完全な結果 (通常出力は改行で終わる行、JSONL は LF で終わる entry) について:
// - I1: 表示された DELETED は全て実際に削除されている。JSONL は「stdout ⊆ ログ ⊆ 実削除」。
// - I2: 表示されずに削除され得るのは、最後に表示した結果の次の1件だけ。差が0件か1件かは記録するだけで、どちらかを要求しない。
// - I3: その1件は削除の候補に限る。サイズ違い、ZIP に無いファイル、(Strict の) 同じサイズで内容違い、末尾の未処理ファイルは残る。
// 最終確認を省く退行はここでは検出できない。最終確認の不一致で削除しないことは Core・実 NTFS の最終確認のテスト (S25 など) が担う。
// 同期: stdout のパイプ容量 (既定の匿名パイプ、数 KiB) を超える結果を出す fixture (一致する小さなファイル Filler 件。通常出力は1行
// 約300バイト、JSONL は1レコード約100バイトで、どちらも最初の DELETED の後に数十 KiB 以上) で、最初の完全な DELETED を受け取った後は
// 強制終了まで読まない。読み手が止まるので、子は末尾のファイルまで進めない。自然終了・同期の失敗はテストの失敗とし、再実行しない。
public sealed class InterruptE2ETests(ITestOutputHelper output)
{
    private const int Filler = 512;

    private static readonly string[] Filled =
        [.. Enumerable.Range(0, Filler).Select(index => $"match-{index:D4}-{new string('x', 30)}.txt")];

    // ZIP の順: size.txt (サイズ違い)、same-size.txt (同じサイズで内容違い)、一致する Filled、tail.txt (一致。処理されずに残る)。
    // target には ZIP に無い unrelated.txt もある。
    private static readonly string[] ZipOrder = ["size.txt", "same-size.txt", .. Filled, "tail.txt"];

    private static E2EFixture CreateFixture()
    {
        var fixture = E2EFixture.Create();
        fixture.WriteZip(ZipFixture.Create(ZipOrder.Select(name => new FixtureEntry(name, E2EFixture.Bytes(name == "same-size.txt" ? "same" : "keep")))));
        fixture.WriteTarget("size.txt", "longer").WriteTarget("same-size.txt", "SAME").WriteTarget("unrelated.txt", "keep");
        foreach (var name in Filled.Append("tail.txt"))
        {
            fixture.WriteTarget(name, "keep");
        }

        return fixture;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task X32_KilledDelete(bool jsonl, bool fast)
    {
        var fixture = CreateFixture();
        var before = E2EFixture.Snapshot(fixture.Target);
        var archive = E2EFixture.Describe(fixture.ArchivePath);
        var log = Path.Combine(fixture.Directory, "run.jsonl");
        fixture.CheckGuard();
        var info = new ProcessStartInfo(UnextractProcess.ExePath)
        {
            WorkingDirectory = fixture.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        string[] arguments = ["delete", fixture.ArchivePath, "--target", fixture.Target, "--yes",
            .. fast ? new[] { "--fast" } : [], .. jsonl ? new[] { "--jsonl", "--log", log } : []];
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var received = new MemoryStream();
        int? exit = null;
        string error = "(stderr not collected)";
        using (var process = Process.Start(info) ?? throw new InvalidOperationException("unextract.exe を起動できない"))
        {
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.BaseStream;
            Exception? failure = null;
            try
            {
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(UnextractProcess.Timeout);

                // 最初の完全な DELETED の結果まで1バイトずつ読む (先読みしない)。
                using (var line = new MemoryStream())
                {
                    var value = new byte[1];
                    while (true)
                    {
                        Assert.True(await stdout.ReadAsync(value, timeout.Token) == 1, "最初の DELETED の前に stdout が閉じた");
                        received.WriteByte(value[0]);
                        line.WriteByte(value[0]);
                        if (value[0] == '\n')
                        {
                            if (Shown(line.ToArray(), jsonl) is [("DELETED", _)])
                            {
                                break;
                            }

                            line.SetLength(0);
                        }
                    }
                }

                Assert.False(process.HasExited, "強制終了の前に子が終了した");
                process.Kill();
                await process.WaitForExitAsync(timeout.Token);
                // 読み取りハンドルは閉じずに、強制終了までにパイプへ書かれた分を EOF まで回収する。
                await stdout.CopyToAsync(received, timeout.Token);
                exit = process.ExitCode;
                error = await stderr.WaitAsync(timeout.Token);
            }
            catch (Exception e) { failure = e; }
            finally
            {
                // Use a fresh deadline: the test's timeout may already have expired. Keep the original failure.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cleanup.Token);
                    exit = process.ExitCode;
                }
                catch (Exception e) { failure = failure is null ? e : new AggregateException(failure, e); }
                try { await stdout.CopyToAsync(received, cleanup.Token); }
                catch (Exception e) { failure = failure is null ? e : new AggregateException(failure, e); }
                try { error = await stderr.WaitAsync(cleanup.Token); }
                catch (Exception e) { failure = failure is null ? e : new AggregateException(failure, e); }

                // Save raw evidence before parsing/asserting, including early EOF, timeout and natural exit.
                output.WriteLine($"X32 fixture: {fixture.Directory}; exit={exit}; failure={failure}");
                output.WriteLine($"--- stdout ---\n{Encoding.UTF8.GetString(received.ToArray())}\n--- stderr ---\n{error}");
                if (jsonl) Diagnose("log", () => File.Exists(log) ? File.ReadAllText(log) : "(not created)");
                Diagnose("remaining files", () => string.Join("\n", E2EFixture.Snapshot(fixture.Target)));
            }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        var bytes = received.ToArray();
        var complete = bytes[..(Array.LastIndexOf(bytes, (byte)'\n') + 1)];
        var shown = Shown(complete, jsonl);
        var deleted = before.Keys.Where(name => !File.Exists(Path.Combine(fixture.Target, name))).ToHashSet(StringComparer.Ordinal);
        var shownDeleted = shown.Where(result => result.Status == "DELETED").Select(result => result.Name).ToHashSet(StringComparer.Ordinal);
        var unshown = deleted.Except(shownDeleted).ToList();
        var logBytes = jsonl ? File.ReadAllBytes(log) : [];
        var logDeleted = jsonl
            ? Shown(logBytes[..(Array.LastIndexOf(logBytes, (byte)'\n') + 1)], jsonl: true)
                .Where(result => result.Status == "DELETED").Select(result => result.Name).ToHashSet(StringComparer.Ordinal)
            : [];

        // 判定より前に、終了コード・生の出力・削除された名前と残った名前を結果へ残す (fixture はテストの後に削除される)。
        output.WriteLine($"X32 {(jsonl ? "JSONL" : "通常出力")} {(fast ? "Fast" : "Strict")}: exit={exit}; 表示 {shown.Count} 件 (DELETED {shownDeleted.Count}); " +
            $"実削除 {deleted.Count}; 表示されない削除 {unshown.Count} [{string.Join(", ", unshown)}]; ログの DELETED {logDeleted.Count}; " +
            $"stdout {bytes.Length} バイト (途中の行 {bytes.Length - complete.Length} バイト: {Convert.ToHexString(bytes[complete.Length..])})");
        output.WriteLine($"--- 残った名前 ---\n{string.Join("\n", before.Keys.Except(deleted))}");

        Assert.Equal(-1, exit);
        Assert.Equal(ZipOrder.Take(shown.Count), shown.Select(result => result.Name));
        Assert.Equal(["MODIFIED", fast ? "DELETED" : "MODIFIED", .. Enumerable.Repeat("DELETED", shown.Count - 2)],
            shown.Select(result => result.Status));
        Assert.Subset(deleted, shownDeleted);
        Assert.True(unshown.Count <= 1, $"表示されずに削除: {string.Join(", ", unshown)}");
        if (unshown is [var only])
        {
            Assert.Equal(ZipOrder[shown.Count], only);
            Assert.Contains(only, fast ? Filled.Append("same-size.txt") : Filled);
        }

        if (jsonl)
        {
            Assert.Subset(logDeleted, shownDeleted);
            Assert.Subset(deleted, logDeleted);
        }

        foreach (var kept in new[] { "size.txt", "unrelated.txt", "tail.txt" }.Concat(fast ? Array.Empty<string>() : ["same-size.txt"]))
        {
            Assert.DoesNotContain(kept, deleted);
        }

        Assert.DoesNotContain(shown, result => result.Name == "tail.txt");
        foreach (var (name, description) in before.Where(item => !deleted.Contains(item.Key)))
        {
            Assert.Equal(description, E2EFixture.Describe(Path.Combine(fixture.Target, name)));
        }

        Assert.Equal(archive, E2EFixture.Describe(fixture.ArchivePath));
    }

    private void Diagnose(string label, Func<string> read)
    {
        try { output.WriteLine($"--- {label} ---\n{read()}"); }
        catch (Exception e) { output.WriteLine($"{label}: diagnostics failed: {e}"); }
    }

    // 完全な結果 (LF で終わる bytes) の (状態名, 名前) を出力の順に。通常出力は結果行、JSONL は entry レコード。
    private static List<(string Status, string Name)> Shown(byte[] complete, bool jsonl)
    {
        if (jsonl)
        {
            return MachineProcessResult.Parse(complete)
                .Where(record => record.GetProperty("type").GetString() == "entry")
                .Select(record => (record.GetProperty("status").GetString()!, record.GetProperty("name").GetString()!)).ToList();
        }

        var report = Report.Parse(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(complete));
        return report.StatusesInOrder.Zip(report.AllEntries).ToList();
    }
}
