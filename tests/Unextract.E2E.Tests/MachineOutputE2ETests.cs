using System.Text;
using System.Text.Json;
using Unextract.Core.Tests.Fixtures;
using Unextract.Windows;
using Xunit.Abstractions;

namespace Unextract.E2E.Tests;

// J14〜J19: 機械出力を exe (通常 build と publish 版) で確かめる (docs/spec/machine-output.md)。
// - J14〜J16: 両操作/両モードの分類・件数・終了コード、ASCII/LF、生の name と UTF-8/CP437/Cf/補助平面、空 ZIP/全 DIRECTORY、ログとの
//   byte 一致。JSON→UTF-8 entries→delete で同サイズ変更を再検証し、選択外と未処理を区別する。Prepare の失敗 (--log NUL を含む) では
//   run なし・削除0件・作成後のログの result を Strict で確認する (Fast で同じ result になることは J12)。CRC 異常の FATAL/STOP と Fast の
//   非読取、実共有拒否の DELETE_FAILED と後続の削除。
// - J17: target 内のログの DELETE_FAILED・非削除、後続の削除と終了後の解放。J18: stdout の drain を止め、別読取ハンドルから run と
//   entry の LF を観測して、ログ先行・実行中の可視性・書込/DELETE access の拒否を確認し、drain 再開後に全 byte を照合する。
// - J19: run の LF 受信と別ハンドルからのログ entry の観測を同期点に stdout の読み手を close する。途中で検出した場合は OUTPUT_FAILED/
//   終了1・後続未処理、非検出時は承認範囲の完走を検証し、ログの最後の entry/result・counts と実削除の範囲を照合する。終端の配送だけの
//   失敗では記録済みの result と実終了コードを区別する。検出自体を全環境の assert にせず、観測条件は docs/OPEN_ISSUES.md#observations。
public sealed class MachineOutputE2ETests(ITestOutputHelper output)
{
    private static string[] Options(bool fast, string operation, string? log = null) =>
        [.. fast ? new[] { "--fast" } : [], .. operation == "delete" ? new[] { "--yes" } : [],
            .. log is not null ? new[] { "--log", log } : []];

    private static JsonElement[] Entries(JsonElement[] records) =>
        records.Where(record => record.GetProperty("type").GetString() == "entry").ToArray();

    private static void End(MachineProcessResult result, string outcome, int exit)
    {
        Assert.True(result.ExitCode == exit, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        var records = result.Records;
        Assert.Single(records, record => record.GetProperty("type").GetString() == "result");
        Assert.Equal("result", records[^1].GetProperty("type").GetString());
        Assert.Equal(outcome, records[^1].GetProperty("outcome").GetString());
        Assert.Equal(exit, records[^1].GetProperty("exit_code").GetInt32());
    }

    // J14: 実 exe の両操作/両モード、全分類、ASCII/LF/raw name、CP437/UTF-8、ログ byte 一致。
    [Theory]
    [InlineData("analyze", false, false)]
    [InlineData("analyze", true, false)]
    [InlineData("delete", false, false)]
    [InlineData("delete", true, false)]
    [InlineData("analyze", false, true)]
    [InlineData("analyze", true, true)]
    [InlineData("delete", false, true)]
    [InlineData("delete", true, true)]
    public async Task J14_RawRecordsAndNames(string operation, bool fast, bool cp437)
    {
        string[] names = cp437 ? ["café.txt", "über.txt", "quote'.txt"] : ["日本語.txt", "format\u202E.txt", "star\U0001F31F.txt"];
        var zipEntries = names.Select(name => new FixtureEntry(name, E2EFixture.Bytes("same"))).Concat(
        [new FixtureEntry("docs/"), new FixtureEntry("changed.txt", E2EFixture.Bytes("same")),
            new FixtureEntry("longer.txt", E2EFixture.Bytes("same")), new FixtureEntry("missing.txt", E2EFixture.Bytes("same")),
            new FixtureEntry("folder.txt", E2EFixture.Bytes("same")), new FixtureEntry("empty.txt", [])]);
        var fixture = E2EFixture.Create().WriteZip(ZipFixture.Create(zipEntries, cp437 ? ZipFixture.Cp437 : Encoding.UTF8));
        foreach (var name in names)
        {
            fixture.WriteTarget(name, "same");
        }

        fixture.CreateTargetDirectory("docs").WriteTarget("changed.txt", "diff").WriteTarget("longer.txt", "longer")
            .CreateTargetDirectory("folder.txt").WriteTarget("empty.txt", []).WriteTarget("unrelated.txt", "keep");
        var before = E2EFixture.Snapshot(fixture.Target);
        var archive = E2EFixture.Describe(fixture.ArchivePath);
        var log = operation == "delete" ? Path.Combine(fixture.Directory, "run.jsonl") : null;
        using var process = new MachineProcess(fixture, operation, Options(fast, operation, log));
        var result = await process.Complete();
        End(result, "completed", 0);
        var records = result.Records;
        var run = records[0];
        Assert.Equal("run", run.GetProperty("type").GetString());
        Assert.Equal(operation, run.GetProperty("operation").GetString());
        Assert.Equal(fast ? "fast" : "strict", run.GetProperty("mode").GetString());
        Assert.Equal(fixture.ArchivePath, run.GetProperty("archive").GetString());
        Assert.Equal(fixture.Target, run.GetProperty("target").GetString());
        Assert.Equal(9, run.GetProperty("entries_total").GetInt32());
        Assert.Equal(9, run.GetProperty("selected").GetInt32());
        Assert.False(run.GetProperty("entries_option").GetBoolean());
        var entries = Entries(records);
        Assert.Equal(operation == "analyze" ? Enumerable.Range(1, 9) : new[] { 1, 2, 3, 5, 6, 7, 8, 9 },
            entries.Select(entry => entry.GetProperty("index").GetInt32()));
        Assert.Equal(names, entries.Take(3).Select(entry => entry.GetProperty("name").GetString()));
        Assert.All(entries.Take(3), entry => Assert.Equal(operation == "delete" ? "DELETED" : fast ? "SAME_SIZE" : "MATCHED",
            entry.GetProperty("status").GetString()));
        Assert.All(entries.Take(3), entry => Assert.Equal(4, entry.GetProperty("length").GetInt64()));
        var byName = entries.ToDictionary(entry => entry.GetProperty("name").GetString()!, StringComparer.Ordinal);
        Assert.Equal(fast ? operation == "delete" ? "DELETED" : "SAME_SIZE" : "MODIFIED", byName["changed.txt"].GetProperty("status").GetString());
        Assert.Equal("MODIFIED", byName["longer.txt"].GetProperty("status").GetString());
        Assert.Equal("MISSING", byName["missing.txt"].GetProperty("status").GetString());
        Assert.Equal("DIRECTORY", byName["folder.txt"].GetProperty("skip_reason").GetString());
        Assert.False(byName["folder.txt"].GetProperty("directory").GetBoolean());
        Assert.Equal(0, byName["empty.txt"].GetProperty("length").GetInt64());
        var counts = records[^1].GetProperty("counts");
        Assert.Equal(1, counts.GetProperty("directory").GetInt32());
        Assert.Equal(fast ? 1 : 2, counts.GetProperty("modified").GetInt32());
        Assert.Equal(1, counts.GetProperty("missing").GetInt32());
        Assert.Equal(1, counts.GetProperty("skipped_special_file").GetInt32());
        Assert.DoesNotContain("unrelated", Encoding.ASCII.GetString(result.Output), StringComparison.Ordinal);
        if (operation == "analyze")
        {
            Assert.Equal(before, E2EFixture.Snapshot(fixture.Target));
            Assert.Equal(fast ? 5 : 4, counts.GetProperty(fast ? "same_size" : "matched").GetInt32());
            Assert.False(counts.TryGetProperty(fast ? "matched" : "same_size", out _));
            Assert.True(byName["docs/"].GetProperty("directory").GetBoolean());
        }
        else
        {
            Assert.Equal(result.Output, File.ReadAllBytes(log!));
            Assert.Equal(fast ? 5 : 4, counts.GetProperty("deleted").GetInt32());
            foreach (var entry in entries)
            {
                var name = entry.GetProperty("name").GetString()!;
                var path = Path.Combine(fixture.Target, name);
                if (entry.GetProperty("status").GetString() == "DELETED")
                {
                    Assert.False(File.Exists(path), name);
                }
                else if (entry.GetProperty("status").GetString() == "MODIFIED")
                {
                    Assert.Equal(before[name], E2EFixture.Describe(path));
                }
                else if (entry.GetProperty("status").GetString() == "MISSING")
                {
                    Assert.False(File.Exists(path), name);
                    Assert.False(Directory.Exists(path), name);
                }
            }

            Assert.Equal(before["unrelated.txt"], E2EFixture.Describe(Path.Combine(fixture.Target, "unrelated.txt")));
            Assert.True(Directory.Exists(Path.Combine(fixture.Target, "docs")));
            Assert.True(Directory.Exists(Path.Combine(fixture.Target, "folder.txt")));
        }

        Assert.Equal(archive, E2EFixture.Describe(fixture.ArchivePath));
    }

    // JSON の復号済み name を UTF-8 entries に使い、analyze 後の同サイズ変更も再検証する。
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task J14_JsonToEntriesRevalidatesCurrentState(bool fast, bool cp437)
    {
        string[] names = cp437 ? ["café.txt", "über.txt", "other.txt"] : ["日本語.txt", "format\u2066\U0001F31F.txt", "other.txt"];
        var fixture = E2EFixture.Create().WriteZip(ZipFixture.Create(
            names.Select(name => new FixtureEntry(name, E2EFixture.Bytes("same"))), cp437 ? ZipFixture.Cp437 : Encoding.UTF8));
        foreach (var name in names)
        {
            fixture.WriteTarget(name, "same");
        }

        var before = E2EFixture.Snapshot(fixture.Target);
        var archive = E2EFixture.Describe(fixture.ArchivePath);
        using var analyze = new MachineProcess(fixture, "analyze", Options(fast, "analyze"));
        var analyzed = await analyze.Complete();
        End(analyzed, "completed", 0);
        Assert.Equal(before, E2EFixture.Snapshot(fixture.Target));
        Assert.Equal(archive, E2EFixture.Describe(fixture.ArchivePath));
        var selected = Entries(analyzed.Records).Take(2).Select(entry => entry.GetProperty("name").GetString()!).Reverse().ToArray();
        var list = Path.Combine(fixture.Directory, "entries.txt");
        using (var file = new FileStream(list, FileMode.CreateNew))
        {
            file.Write(Encoding.UTF8.GetBytes(string.Join('\n', selected) + "\n"));
        }

        File.WriteAllText(Path.Combine(fixture.Target, names[0]), "diff", new UTF8Encoding(false));
        var changed = E2EFixture.Describe(Path.Combine(fixture.Target, names[0]));
        var log = Path.Combine(fixture.Directory, "run.jsonl");
        using var delete = new MachineProcess(fixture, "delete", [.. Options(fast, "delete", log), "--entries", list]);
        var deleted = await delete.Complete();
        End(deleted, "completed", 0);
        Assert.Equal(deleted.Output, File.ReadAllBytes(log));
        Assert.True(deleted.Records[0].GetProperty("entries_option").GetBoolean());
        Assert.Equal(2, deleted.Records[0].GetProperty("selected").GetInt32());
        Assert.Equal(new[] { 1, 2 }, Entries(deleted.Records).Select(entry => entry.GetProperty("index").GetInt32()));
        Assert.Equal(new[] { fast ? "DELETED" : "MODIFIED", "DELETED" }, Entries(deleted.Records).Select(entry => entry.GetProperty("status").GetString()));
        Assert.Equal(1, deleted.Records[^1].GetProperty("counts").GetProperty("not_selected").GetInt32());
        Assert.Equal(0, deleted.Records[^1].GetProperty("counts").GetProperty("unprocessed").GetInt32());
        Assert.False(File.Exists(Path.Combine(fixture.Target, names[1])));
        Assert.Equal(before[names[2]], E2EFixture.Describe(Path.Combine(fixture.Target, names[2])));
        if (fast)
        {
            Assert.False(File.Exists(Path.Combine(fixture.Target, names[0])));
        }
        else
        {
            Assert.Equal(changed, E2EFixture.Describe(Path.Combine(fixture.Target, names[0])));
        }

        Assert.Equal(archive, E2EFixture.Describe(fixture.ArchivePath));
    }

    // 空 ZIP と全 DIRECTORY でも run/result があり、target は不変。
    [Theory]
    [InlineData("analyze", false, false)]
    [InlineData("analyze", true, false)]
    [InlineData("delete", false, false)]
    [InlineData("delete", true, false)]
    [InlineData("analyze", false, true)]
    [InlineData("analyze", true, true)]
    [InlineData("delete", false, true)]
    [InlineData("delete", true, true)]
    public async Task J14_NoFileEntriesStillCompletes(string operation, bool fast, bool directory)
    {
        var fixture = E2EFixture.Create().WriteZip(ZipFixture.Create(directory ? [new FixtureEntry("docs/")] : []))
            .WriteTarget("unrelated.txt", "keep");
        var before = E2EFixture.Snapshot(fixture.Target);
        var archive = E2EFixture.Describe(fixture.ArchivePath);
        var log = operation == "delete" ? Path.Combine(fixture.Directory, "run.jsonl") : null;
        using var process = new MachineProcess(fixture, operation, Options(fast, operation, log));
        var result = await process.Complete();
        End(result, "completed", 0);
        var records = result.Records;
        Assert.Equal(directory ? 1 : 0, records[0].GetProperty("selected").GetInt32());
        Assert.Equal(directory ? 1 : 0, records[^1].GetProperty("counts").GetProperty("directory").GetInt32());
        if (directory && operation == "analyze")
        {
            Assert.Equal("DIRECTORY", Assert.Single(Entries(records)).GetProperty("status").GetString());
        }
        else
        {
            Assert.Empty(Entries(records));
        }

        Assert.Equal(before, E2EFixture.Snapshot(fixture.Target));
        Assert.Equal(archive, E2EFixture.Describe(fixture.ArchivePath));
        if (log is not null)
        {
            Assert.Equal(result.Output, File.ReadAllBytes(log));
        }
    }

    // J15: Prepare/result のみ、ログ作成前と作成後、ログと入力が同じパスの場合。Strict だけ (Prepare の失敗は run より前に終わり、
    // Fast でも同じ result になる。モードの違いは CLI の J12 が確かめる)。
    [Theory]
    [InlineData("usage")]
    [InlineData("existing-log")]
    [InlineData("create-log")]
    [InlineData("device-log")]
    [InlineData("invalid-zip")]
    [InlineData("archive-is-log")]
    [InlineData("entries-is-log")]
    [InlineData("entries-no-match")]
    public async Task J15_PrepareFailuresDeleteNothing(string scenario)
    {
        var fixture = E2EFixture.Create().WriteTarget("same.txt", "same");
        if (scenario != "archive-is-log")
        {
            fixture.WriteZip(ZipFixture.Create(new FixtureEntry(scenario == "invalid-zip" ? "../bad.txt" : "same.txt", E2EFixture.Bytes("same"))));
        }

        var before = E2EFixture.Snapshot(fixture.Target);
        var archive = File.Exists(fixture.ArchivePath) ? E2EFixture.Describe(fixture.ArchivePath) : null;
        var log = Path.Combine(fixture.Directory, "run.jsonl");
        var outcome = "input_error";
        var code = "USAGE";
        string[] extra = [];
        switch (scenario)
        {
            case "usage": break;
            case "existing-log":
                log = fixture.ArchivePath;
                code = "LOG_ALREADY_EXISTS";
                break;
            case "create-log":
                log = Path.Combine(fixture.Directory, "absent", "run.jsonl");
                code = "LOG_CREATE_FAILED";
                break;
            case "device-log":
                // 開けても記録が残らない出力先は、ZIP があっても処理を始めない。
                log = "NUL";
                code = "LOG_CREATE_FAILED";
                break;
            case "invalid-zip": outcome = "fatal"; code = "DOT_DOT_COMPONENT"; break;
            case "archive-is-log": log = fixture.ArchivePath; outcome = "fatal"; code = "ARCHIVE_OPEN_FAILED"; break;
            case "entries-is-log":
                log = Path.Combine(fixture.Directory, "entries.txt");
                extra = ["--entries", log];
                code = "ENTRIES_UNREADABLE";
                break;
            case "entries-no-match":
                var list = Path.Combine(fixture.Directory, "entries.txt");
                File.WriteAllText(list, "unknown.txt\n", new UTF8Encoding(false));
                extra = ["--entries", list];
                code = "ENTRIES_NO_MATCH";
                break;
        }

        using var process = new MachineProcess(fixture, "delete",
            [.. scenario != "usage" ? new[] { "--yes" } : [], "--log", log, .. extra]);
        var result = await process.Complete();
        End(result, outcome, 1);
        var final = Assert.Single(result.Records);
        Assert.Equal(code, final.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(scenario == "usage" ? "usage" : "prepare", final.GetProperty("error").GetProperty("stage").GetString());
        Assert.False(final.TryGetProperty("counts", out _));
        Assert.Equal(before, E2EFixture.Snapshot(fixture.Target));
        if (archive is not null)
        {
            Assert.Equal(archive, E2EFixture.Describe(fixture.ArchivePath));
        }

        if (scenario is "invalid-zip" or "archive-is-log" or "entries-is-log" or "entries-no-match")
        {
            Assert.Equal(result.Output, File.ReadAllBytes(log));
        }
        else if (scenario is not ("existing-log" or "device-log"))
        {
            Assert.False(File.Exists(log));
        }
    }

    [Theory]
    [InlineData("analyze")]
    [InlineData("delete")]
    public async Task J15_ArchiveValidationFailure(string operation)
    {
        var fixture = E2EFixture.Create().WriteZip(ZipFixture.Create(new FixtureEntry("../bad.txt", E2EFixture.Bytes("same"))))
            .WriteTarget("same.txt", "same");
        var before = E2EFixture.Snapshot(fixture.Target);
        var archive = E2EFixture.Describe(fixture.ArchivePath);
        using var process = new MachineProcess(fixture, operation, Options(fast: false, operation));
        var result = await process.Complete();
        End(result, "fatal", 1);
        var final = Assert.Single(result.Records);
        Assert.Equal("DOT_DOT_COMPONENT", final.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(before, E2EFixture.Snapshot(fixture.Target));
        Assert.Equal(archive, E2EFixture.Describe(fixture.ArchivePath));
    }

    // J16: entry の FATAL/STOP、Fast の内容非読取、実共有拒否の DELETE_FAILED と後続。
    [Theory]
    [InlineData("analyze", false)]
    [InlineData("analyze", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    public async Task J16_ContentFailureBoundary(string operation, bool fast)
    {
        var zip = new ZipPatcher(ZipFixture.Create(
            new FixtureEntry("first.txt", E2EFixture.Bytes("same")), new FixtureEntry("bad.txt", E2EFixture.Bytes("same")),
            new FixtureEntry("later.txt", E2EFixture.Bytes("same"))));
        zip.SetCrc32(1, zip.GetCrc32(1) ^ 1);
        var fixture = E2EFixture.Create().WriteZip(zip.ToArray()).WriteTarget("first.txt", "same")
            .WriteTarget("bad.txt", "same").WriteTarget("later.txt", "same");
        var before = E2EFixture.Snapshot(fixture.Target);
        var archive = E2EFixture.Describe(fixture.ArchivePath);
        var log = operation == "delete" ? Path.Combine(fixture.Directory, "run.jsonl") : null;
        using var process = new MachineProcess(fixture, operation, Options(fast, operation, log));
        var result = await process.Complete();
        End(result, fast ? "completed" : operation == "delete" ? "stopped" : "fatal", fast ? 0 : 1);
        var entries = Entries(result.Records);
        if (!fast)
        {
            var error = result.Records[^1].GetProperty("error");
            Assert.Equal("entry", error.GetProperty("stage").GetString());
            Assert.Equal("compare", error.GetProperty("step").GetString());
            Assert.Equal("CONTENT_CRC_MISMATCH", error.GetProperty("code").GetString());
            Assert.Equal(2, error.GetProperty("entry_index").GetInt32());
            Assert.Equal("bad.txt", error.GetProperty("entry_name").GetString());
            if (operation == "delete")
            {
                Assert.Equal(new[] { "DELETED", "STOPPED" }, entries.Select(entry => entry.GetProperty("status").GetString()));
                Assert.False(entries[^1].GetProperty("possibly_deleted").GetBoolean());
                Assert.Equal(1, result.Records[^1].GetProperty("counts").GetProperty("unprocessed").GetInt32());
                Assert.False(File.Exists(Path.Combine(fixture.Target, "first.txt")));
                Assert.Equal(before["bad.txt"], E2EFixture.Describe(Path.Combine(fixture.Target, "bad.txt")));
                Assert.Equal(before["later.txt"], E2EFixture.Describe(Path.Combine(fixture.Target, "later.txt")));
            }
            else
            {
                Assert.Single(entries);
                Assert.Equal(1, result.Records[^1].GetProperty("counts").GetProperty("undetermined").GetInt32());
            }
        }
        else
        {
            Assert.Equal(3, entries.Length);
            if (operation == "delete")
            {
                Assert.Empty(Directory.EnumerateFiles(fixture.Target));
            }
        }

        if (operation == "analyze")
        {
            Assert.Equal(before, E2EFixture.Snapshot(fixture.Target));
        }
        else
        {
            Assert.Equal(result.Output, File.ReadAllBytes(log!));
        }

        Assert.Equal(archive, E2EFixture.Describe(fixture.ArchivePath));
    }

    [Theory]
    [InlineData("analyze", false)]
    [InlineData("analyze", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    public async Task J16_SharingRefusal(string operation, bool fast)
    {
        var fixture = E2EFixture.Create().WriteZip(ZipFixture.Create(new FixtureEntry("held.txt", E2EFixture.Bytes("same")),
            new FixtureEntry("later.txt", E2EFixture.Bytes("same")))).WriteTarget("held.txt", "same").WriteTarget("later.txt", "same");
        var before = E2EFixture.Snapshot(fixture.Target);
        var archive = E2EFixture.Describe(fixture.ArchivePath);
        using var held = new FileStream(Path.Combine(fixture.Target, "held.txt"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var log = operation == "delete" ? Path.Combine(fixture.Directory, "run.jsonl") : null;
        using var process = new MachineProcess(fixture, operation, Options(fast, operation, log));
        var result = await process.Complete();
        End(result, operation == "delete" ? "completed" : "fatal", 1);
        if (operation == "delete")
        {
            var entries = Entries(result.Records);
            Assert.Equal(new[] { "DELETE_FAILED", "DELETED" }, entries.Select(entry => entry.GetProperty("status").GetString()));
            Assert.Equal("DELETE_OPEN_REFUSED", entries[0].GetProperty("reason").GetProperty("code").GetString());
            Assert.Equal(32, entries[0].GetProperty("reason").GetProperty("win32_error").GetInt32());
            Assert.Equal(1, result.Records[^1].GetProperty("counts").GetProperty("delete_failed").GetInt32());
            Assert.Equal(0, result.Records[^1].GetProperty("counts").GetProperty("unprocessed").GetInt32());
            Assert.False(File.Exists(Path.Combine(fixture.Target, "later.txt")));
            Assert.Equal(result.Output, File.ReadAllBytes(log!));
        }
        else
        {
            Assert.Empty(Entries(result.Records));
            Assert.Equal("COMPARISON_OPEN_FAILED", result.Records[^1].GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(32, result.Records[^1].GetProperty("error").GetProperty("win32_error").GetInt32());
        }

        held.Dispose();
        Assert.Equal(before["held.txt"], E2EFixture.Describe(Path.Combine(fixture.Target, "held.txt")));
        if (operation == "analyze")
        {
            Assert.Equal(before, E2EFixture.Snapshot(fixture.Target));
        }

        Assert.Equal(archive, E2EFixture.Describe(fixture.ArchivePath));
    }

    // J17: target 内の実ログは既存識別確認で DELETE_FAILED、後続は削除。ログ専用除外なし。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task J17_LogInsideTargetIsRetained(bool fast)
    {
        var fixture = E2EFixture.Create().WriteZip(ZipFixture.Create(new FixtureEntry("run.jsonl", E2EFixture.Bytes("x")),
            new FixtureEntry("later.txt", E2EFixture.Bytes("same")))).WriteTarget("later.txt", "same");
        var archive = E2EFixture.Describe(fixture.ArchivePath);
        var log = Path.Combine(fixture.Target, "run.jsonl");
        using var process = new MachineProcess(fixture, "delete", Options(fast, "delete", log));
        var result = await process.Complete();
        End(result, "completed", 1);
        var entries = Entries(result.Records);
        Assert.Equal(new[] { "DELETE_FAILED", "DELETED" }, entries.Select(entry => entry.GetProperty("status").GetString()));
        Assert.Equal("DELETE_OPEN_REFUSED", entries[0].GetProperty("reason").GetProperty("code").GetString());
        Assert.Equal(32, entries[0].GetProperty("reason").GetProperty("win32_error").GetInt32());
        Assert.Equal(result.Output, File.ReadAllBytes(log));
        Assert.False(File.Exists(Path.Combine(fixture.Target, "later.txt")));
        Assert.Equal(archive, E2EFixture.Describe(fixture.ArchivePath));
        // 終了後は書込/DELETE access が解放される。削除指示はしない。
        using (var writable = new FileStream(log, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Equal(result.Output.Length, writable.Length);
        }
        var deletable = HandleOpener.OpenForDeletion(log);
        Assert.True(deletable.Succeeded, deletable.ToString());
        deletable.Value!.Dispose();
    }

    // パイプ容量を超える長い MISSING レコード。stdout を drain しない間に完走できない。
    private static E2EFixture BackpressureFixture()
    {
        var names = Enumerable.Range(0, 512).Select(index => $"missing-{index:D4}-{new string('x', 180)}.txt");
        var fixture = E2EFixture.Create().WriteZip(ZipFixture.Create(names.Select(name => new FixtureEntry(name, E2EFixture.Bytes("same")))
            .Concat(Enumerable.Range(0, 3).Select(index => new FixtureEntry($"tail-{index}.txt", E2EFixture.Bytes("same"))))));
        foreach (var index in Enumerable.Range(0, 3))
        {
            fixture.WriteTarget($"tail-{index}.txt", "same");
        }

        fixture.WriteTarget("unrelated.txt", "keep");
        return fixture;
    }

    // 別読取ハンドルで run + 最初の entry の LF を観測する。時間経過を同期条件にしない。
    private static async Task<byte[]> ObserveEntry(string path)
    {
        using var timeout = new CancellationTokenSource(UnextractProcess.Timeout);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, timeout.Token);
            bytes.Write(buffer, 0, count);
            var current = bytes.ToArray();
            if (current.Count(value => value == '\n') >= 2)
            {
                return current[..(Array.LastIndexOf(current, (byte)'\n') + 1)];
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    // J18: exe 実行中の可視性/共有/ログ先行。stdout の drain 再開後は全 byte が一致。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task J18_LiveLogIsVisibleAndHeld(bool fast)
    {
        var fixture = BackpressureFixture();
        var unrelated = E2EFixture.Describe(Path.Combine(fixture.Target, "unrelated.txt"));
        var archive = E2EFixture.Describe(fixture.ArchivePath);
        var log = Path.Combine(fixture.Directory, "run.jsonl");
        using var process = new MachineProcess(fixture, "delete", Options(fast, "delete", log));
        var run = await process.ReadLine();
        var visible = await ObserveEntry(log);
        Assert.False(process.HasExited);
        Assert.Equal(run, visible[..run.Length]);
        Assert.Equal("entry", MachineProcessResult.Parse(visible)[1].GetProperty("type").GetString());
        var writeError = Assert.Throws<IOException>(() =>
        {
            using var writer = new FileStream(log, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        });
        Assert.Equal(32, writeError.HResult & 0xFFFF);
        var deletion = HandleOpener.OpenForDeletion(log);
        Assert.False(deletion.Succeeded);
        Assert.Equal(32, deletion.Error);
        var result = await process.Complete(run);
        End(result, "completed", 0);
        Assert.Equal(result.Output, File.ReadAllBytes(log));
        Assert.Equal(3, result.Records[^1].GetProperty("counts").GetProperty("deleted").GetInt32());
        Assert.Equal(new[] { "unrelated.txt" }, Directory.EnumerateFiles(fixture.Target).Select(Path.GetFileName));
        Assert.Equal(unrelated, E2EFixture.Describe(Path.Combine(fixture.Target, "unrelated.txt")));
        Assert.Equal(archive, E2EFixture.Describe(fixture.ArchivePath));
    }

    // J19: run LF と実ログ entry の可視性を同期点に読み手を close。検出の有無は保証せず両分岐を検証。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task J19_ClosedPipeObservation(bool fast)
    {
        var fixture = BackpressureFixture();
        var before = E2EFixture.Snapshot(fixture.Target);
        var archive = E2EFixture.Describe(fixture.ArchivePath);
        var log = Path.Combine(fixture.Directory, "run.jsonl");
        using var process = new MachineProcess(fixture, "delete", Options(fast, "delete", log));
        var run = await process.ReadLine();
        var visible = await ObserveEntry(log);
        Assert.False(process.HasExited);
        Assert.Equal(run, visible[..run.Length]);
        process.Output.Dispose();
        var (exit, error) = await process.Wait();
        var records = MachineProcessResult.Parse(File.ReadAllBytes(log));
        var final = records[^1];
        Assert.Equal("result", final.GetProperty("type").GetString());
        Assert.Single(records, record => record.GetProperty("type").GetString() == "result");
        Assert.Equal(string.Empty, error);
        var entries = Entries(records);
        Assert.Equal(Enumerable.Range(1, entries.Length), entries.Select(entry => entry.GetProperty("index").GetInt32()));
        var detected = exit == 1;
        if (final.GetProperty("outcome").GetString() == "internal_error")
        {
            Assert.Equal(1, exit);
            Assert.Equal(1, final.GetProperty("exit_code").GetInt32());
            Assert.Equal("OUTPUT_FAILED", final.GetProperty("error").GetProperty("code").GetString());
            Assert.True(final.GetProperty("error").GetProperty("deletion_started").GetBoolean());
            Assert.Equal(515 - entries.Length, final.GetProperty("counts").GetProperty("unprocessed").GetInt32());
        }
        else
        {
            // 終端 stdout 配送だけが失敗した場合は、ログの completed/0 と実終了1を許す (D1)。
            Assert.Contains(exit, new[] { 0, 1 });
            Assert.Equal("completed", final.GetProperty("outcome").GetString());
            Assert.Equal(0, final.GetProperty("exit_code").GetInt32());
            Assert.Equal(515, entries.Length);
        }

        var deleted = entries.Where(entry => entry.GetProperty("status").GetString() == "DELETED")
            .Select(entry => entry.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(deleted.Count, final.GetProperty("counts").GetProperty("deleted").GetInt32());
        foreach (var (name, description) in before)
        {
            if (deleted.Contains(name))
            {
                Assert.False(File.Exists(Path.Combine(fixture.Target, name)), name);
            }
            else
            {
                Assert.Equal(description, E2EFixture.Describe(Path.Combine(fixture.Target, name)));
            }
        }

        Assert.Equal(archive, E2EFixture.Describe(fixture.ArchivePath));
        output.WriteLine($"pipe observation: OS={Environment.OSVersion}; runtime={Environment.Version}; exe={UnextractProcess.ExePath}; fast={fast}; detected={detected}; exit={exit}; last_entry={entries[^1].GetProperty("index")}; result={final.GetProperty("outcome")}; deleted={deleted.Count}; unprocessed={final.GetProperty("counts").GetProperty("unprocessed")}; fixture={fixture.Directory}");
    }
}
