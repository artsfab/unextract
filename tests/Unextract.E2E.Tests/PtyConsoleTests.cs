using System.Text;
using System.Text.RegularExpressions;
using Unextract.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace Unextract.E2E.Tests;

// 実端末 (ConPTY) のコンソールの状態に依存する性質。X30 (旧 M06): コードページと名前の表示。X31 (旧 M13・L18): Windows PowerShell 5.1 が
// 保存した analyze の出力を --entries に使うときの文字コード。字形 (フォントによる □) は端末の性質で、対象にしない。
[Collection("PTY confirmation")]
public sealed class PtyConsoleTests(ITestOutputHelper output)
{
    private static readonly string[] Matched = ["資料/報告書.txt", "café░.txt"];

    // 日本語名と、CP932 に無い文字 (é、░) を含む名前。target には Matched の2件だけがある。
    private static E2EFixture CreateFixture()
    {
        var fixture = E2EFixture.Create();
        fixture.WriteZip(ZipFixture.Create(
            new FixtureEntry("資料/報告書.txt", E2EFixture.Bytes("report")),
            new FixtureEntry("資料/写真一覧.csv", E2EFixture.Bytes("a,b")),
            new FixtureEntry("ファイル名.txt", E2EFixture.Bytes("name")),
            new FixtureEntry("café░.txt", E2EFixture.Bytes("cafe"))));
        return fixture.WriteTarget("資料/報告書.txt", "report").WriteTarget("café░.txt", "cafe");
    }

    private static string Quote(string path) => $"\"{path}\"";

    // X30: chcp 932 / 65001 の後に analyze → Entry と Target の名前が変換されず (? や U+FFFD、\u{...} にならず) に表示され、終了 0。
    // 製品は実行中だけ出力を UTF-8 にするので、実行後のコードページは実行前の値に戻る。
    [Theory]
    [InlineData(932)]
    [InlineData(65001)]
    public async Task X30_NamesAreShownAsIsUnderCodePage(int codePage)
    {
        var fixture = CreateFixture();
        var before = E2EFixture.Snapshot(fixture.Directory);
        var command = $"chcp {codePage} >nul & {Quote(UnextractProcess.ExePath)} analyze {Quote(fixture.ArchivePath)} --target {Quote(fixture.Target)}"
            + " && echo X30-EXIT-0 || echo X30-EXIT-NOT-0 & echo X30-AFTER & chcp";
        await PtyProcess.RunAsync($"fixture: {fixture.Directory}\ncode page: {codePage}",
            token => PtyProcess.StartCmdAsync(fixture.Directory, command, token), async (terminal, token) =>
            {
                Assert.Equal(0, await terminal.WaitForExitAsync(token));
                var text = terminal.OutputWithoutProgress;
                Assert.Matches(new Regex("(?m)^X30-EXIT-0 *$"), text);
                var report = Report.Parse(text);
                report.AssertStatus("MATCHED", Matched);
                report.AssertStatus("MISSING", "資料/写真一覧.csv", "ファイル名.txt");
                Assert.EndsWith(@"\資料\報告書.txt", report.TargetOf("資料/報告書.txt"), StringComparison.Ordinal);
                Assert.EndsWith(@"\café░.txt", report.TargetOf("café░.txt"), StringComparison.Ordinal);
                Assert.DoesNotContain("�", text, StringComparison.Ordinal);
                Assert.DoesNotContain(@"\u{", text, StringComparison.Ordinal);
                var after = text[(text.IndexOf("X30-AFTER", StringComparison.Ordinal) + "X30-AFTER".Length)..];
                Assert.Matches(new Regex($@"\b{codePage}\b"), after);
                Assert.Equal(before, E2EFixture.Snapshot(fixture.Directory));
            });
    }

    // X31: コードページ 932 のコンソールで、Windows PowerShell 5.1 が analyze の出力を `>` または `Out-File -Encoding utf8` で保存する。
    // 保存形式は実測して記録し (推定を期待値にしない)、MATCHED の行の Entry を同じ形式 (同じ BOM) の entries にして delete --entries --yes
    // を実行する。安全側の性質だけを判定する: UTF-16 なら L02 の入力エラー (UTF-8 の案内) で削除0件。それ以外なら、受理されて MATCHED の
    // 2件だけが削除されるか、入力エラー (文字化けで ZIP に無い名前など) で削除0件のどちらか。
    [Theory]
    [InlineData("redirect")]
    [InlineData("out-file-utf8")]
    public async Task X31_PowerShellSavedOutputAsEntries(string save)
    {
        var fixture = CreateFixture();
        var saved = Path.Combine(fixture.Directory, "saved.txt");
        var script = Path.Combine(fixture.Directory, "save.ps1");
        var sink = save == "redirect" ? "> $Saved" : "| Out-File -Encoding utf8 $Saved";
        File.WriteAllText(script,
            "param([string]$Exe, [string]$Zip, [string]$Target, [string]$Saved)\r\n"
            + $"& $Exe analyze $Zip --target $Target {sink}\r\n"
            + "\"X31-PS=$($PSVersionTable.PSVersion) OUT=$([Console]::OutputEncoding.CodePage) EXIT=$LASTEXITCODE\"\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var command = $"chcp 932 >nul & {Quote(powershell)} -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {Quote(script)}"
            + $" {Quote(UnextractProcess.ExePath)} {Quote(fixture.ArchivePath)} {Quote(fixture.Target)} {Quote(saved)}";
        string? shell = null;
        await PtyProcess.RunAsync($"fixture: {fixture.Directory}\nsave: {save}",
            token => PtyProcess.StartCmdAsync(fixture.Directory, command, token), async (terminal, token) =>
            {
                Assert.Equal(0, await terminal.WaitForExitAsync(token));
                shell = Regex.Match(terminal.Output, @"X31-PS=\S+ OUT=\d+ EXIT=\d+").Value;
                Assert.StartsWith("X31-PS=5.1.", shell, StringComparison.Ordinal);
                Assert.EndsWith(" EXIT=0", shell, StringComparison.Ordinal);
            });

        var bytes = File.ReadAllBytes(saved);
        var (form, encoding) = bytes switch
        {
            [0xFF, 0xFE, ..] => ("UTF-16LE (BOM)", (Encoding)new UnicodeEncoding(bigEndian: false, byteOrderMark: true)),
            [0xEF, 0xBB, 0xBF, ..] => ("UTF-8 (BOM)", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)),
            _ => ("BOM なし (UTF-8 として読む)", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)),
        };
        var text = encoding.GetString(bytes[encoding.Preamble.Length..]);
        var entries = Report.Parse(text).Entries("MATCHED");
        var entriesPath = Path.Combine(fixture.Directory, "entries.txt");
        File.WriteAllBytes(entriesPath, [.. encoding.Preamble, .. encoding.GetBytes(string.Concat(entries.Select(entry => entry + "\r\n")))]);
        var before = E2EFixture.Snapshot(fixture.Target);

        var delete = fixture.RunDeleting(stdin: null, "--entries", entriesPath, "--yes");

        var accepted = delete.ExitCode == 0;
        output.WriteLine($"X31 {save}: {shell}; 先頭 {Convert.ToHexString(bytes.AsSpan(0, Math.Min(8, bytes.Length)))} = {form}; " +
            $"MATCHED の Entry = [{string.Join(", ", entries)}]; 名前の一致 = {entries.SequenceEqual(Matched)}; " +
            $"delete = {(accepted ? "受理" : "入力エラー")} (終了 {delete.ExitCode})");
        output.WriteLine(delete.ToString());
        if (accepted)
        {
            Assert.NotEqual("UTF-16LE (BOM)", form);
            Report.Parse(delete).AssertStatus("DELETED", Matched);
            Assert.Empty(Directory.EnumerateFiles(fixture.Target, "*", SearchOption.AllDirectories));
            return;
        }

        Assert.True(delete.ExitCode == 1, delete.ToString());
        Assert.StartsWith("入力エラー: ", delete.ErrorLines[0], StringComparison.Ordinal);
        if (form == "UTF-16LE (BOM)")
        {
            Assert.Contains("UTF-16 で保存されています。UTF-8 で保存してください", delete.StandardError, StringComparison.Ordinal);
        }

        Assert.Empty(Report.Parse(delete).AllEntries);
        Assert.Equal(before, E2EFixture.Snapshot(fixture.Target));
    }
}
