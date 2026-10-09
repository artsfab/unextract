using System.Text;
using System.Text.Json;

namespace Unextract.Cli.Tests;

// J08: ASCII/LF のシリアライズ (docs/spec/machine-output.md#destinations、#records)。run/entry/result の明示的なフィールド、null の
// 省略、数値/bool、64bit Length、BOM/CR なし・LF 終端、日本語/CP437 相当の文字/補助平面/Cf/C1/区切り/引用符/バックスラッシュの
// raw name の往復。Core の型からの対応は J11 (MachineRecordTests)。
public class MachineOutputTests
{
    [Theory]
    [InlineData("日本語.txt")]
    [InlineData("éÇ£.txt")]
    [InlineData("face-\U0001F642.txt")]
    [InlineData("cf-\u202E\u2066\u2069\u200B-\U000E0001.txt")]
    [InlineData("c1-\u0085\u009F.txt")]
    [InlineData("line-\u2028\u2029.txt")]
    [InlineData("dir\\name\".txt")]
    [InlineData("control-\0\t\r\n.txt")]
    public void J08_RawNameRoundTripsAsAsciiJson(string name)
    {
        var bytes = MachineOutput.Serialize(new MachineEntryRecord(17, name, false, long.MaxValue, "MATCHED"));
        using var document = ParseLine(bytes, "entry");
        var root = document.RootElement;
        Assert.Equal(name, root.GetProperty("name").GetString());
        Assert.Equal(17, root.GetProperty("index").GetInt32());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("length").ValueKind);
        Assert.Equal(long.MaxValue, root.GetProperty("length").GetInt64());
        Assert.Equal(JsonValueKind.False, root.GetProperty("directory").ValueKind);
        Assert.Equal(["v", "type", "index", "name", "directory", "length", "status"], Names(root));
    }

    [Fact]
    public void J08_RunUsesExplicitFieldsAndJsonTypes()
    {
        const string archive = "日本語\\archive.zip";
        const string target = "C:\\日本語\\target";
        using var document = ParseLine(MachineOutput.Serialize(
            new MachineRunRecord("delete", "fast", archive, target, 5, 0, true)), "run");
        var root = document.RootElement;
        Assert.Equal(["v", "type", "operation", "mode", "archive", "target", "entries_total", "selected", "entries_option"], Names(root));
        Assert.Equal("delete", root.GetProperty("operation").GetString());
        Assert.Equal("fast", root.GetProperty("mode").GetString());
        Assert.Equal(archive, root.GetProperty("archive").GetString());
        Assert.Equal(target, root.GetProperty("target").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("entries_total").ValueKind);
        Assert.Equal(5, root.GetProperty("entries_total").GetInt32());
        Assert.Equal(0, root.GetProperty("selected").GetInt32());
        Assert.Equal(JsonValueKind.True, root.GetProperty("entries_option").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void J08_ReasonAndFalseFlagsAreNotOmitted(bool possiblyDeleted)
    {
        const string message = "表示用 \\u{202E} 日本語";
        using var document = ParseLine(MachineOutput.Serialize(new MachineEntryRecord(3, "file", false, 0, "STOPPED",
            Reason: new MachineReason("TARGET_INFO_FAILED", message, "final_check", 123), PossiblyDeleted: possiblyDeleted)), "entry");
        var root = document.RootElement;
        Assert.Equal(possiblyDeleted, root.GetProperty("possibly_deleted").GetBoolean());
        Assert.False(root.TryGetProperty("skip_reason", out _));
        var reason = root.GetProperty("reason");
        Assert.Equal(["step", "code", "win32_error", "message"], Names(reason));
        Assert.Equal("final_check", reason.GetProperty("step").GetString());
        Assert.Equal("TARGET_INFO_FAILED", reason.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Number, reason.GetProperty("win32_error").ValueKind);
        Assert.Equal(123, reason.GetProperty("win32_error").GetInt32());
        Assert.Equal(message, reason.GetProperty("message").GetString());
    }

    [Fact]
    public void J08_OptionalFieldsAreAbsentAndDirectoryLengthIsNumericZero()
    {
        using var directory = ParseLine(MachineOutput.Serialize(new MachineEntryRecord(1, "dir/", true, 0, "DIRECTORY")), "entry");
        Assert.True(directory.RootElement.GetProperty("directory").GetBoolean());
        Assert.Equal(0, directory.RootElement.GetProperty("length").GetInt64());
        using var stopped = ParseLine(MachineOutput.Serialize(new MachineEntryRecord(2, "file", false, 0, "STOPPED",
            Reason: new MachineReason("UNEXPECTED_EXCEPTION", "message"), PossiblyDeleted: false)), "entry");
        Assert.Equal(["code", "message"], Names(stopped.RootElement.GetProperty("reason")));
        using var skipped = ParseLine(MachineOutput.Serialize(new MachineEntryRecord(3, "link", false, 99,
            "SKIPPED_SPECIAL_FILE", SkipReason: "HARDLINK")), "entry");
        Assert.Equal("HARDLINK", skipped.RootElement.GetProperty("skip_reason").GetString());
        Assert.False(skipped.RootElement.TryGetProperty("reason", out _));
        Assert.False(skipped.RootElement.TryGetProperty("possibly_deleted", out _));
        using var result = ParseLine(MachineOutput.Serialize(new MachineResultRecord("completed", 0)), "result");
        Assert.Equal(["v", "type", "outcome", "exit_code"], Names(result.RootElement));
    }

    [Fact]
    public void J08_ResultErrorRetainsAllValuesAndOmittedValuesStayAbsent()
    {
        var error = new MachineError("entry", "TARGET_INFO_FAILED", "message", "inspect", 7,
            "日本語\u202E\U0001F642", 2, 5, false, true);
        using var document = ParseLine(MachineOutput.Serialize(new MachineResultRecord("stopped", 1, Error: error)), "result");
        var root = document.RootElement;
        Assert.Equal("stopped", root.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("exit_code").ValueKind);
        Assert.Equal(1, root.GetProperty("exit_code").GetInt32());
        Assert.False(root.TryGetProperty("counts", out _));
        var value = root.GetProperty("error");
        Assert.Equal(["stage", "step", "code", "entry_index", "entry_name", "line", "win32_error", "possibly_deleted", "deletion_started", "message"], Names(value));
        Assert.Equal(error.Stage, value.GetProperty("stage").GetString());
        Assert.Equal(error.Step, value.GetProperty("step").GetString());
        Assert.Equal(error.Code, value.GetProperty("code").GetString());
        Assert.Equal(error.EntryIndex, value.GetProperty("entry_index").GetInt32());
        Assert.Equal(error.EntryName, value.GetProperty("entry_name").GetString());
        Assert.Equal(error.Line, value.GetProperty("line").GetInt32());
        Assert.Equal(error.Win32Error, value.GetProperty("win32_error").GetInt32());
        Assert.False(value.GetProperty("possibly_deleted").GetBoolean());
        Assert.True(value.GetProperty("deletion_started").GetBoolean());
        Assert.Equal(error.Message, value.GetProperty("message").GetString());
        using var minimal = ParseLine(MachineOutput.Serialize(new MachineResultRecord("input_error", 1,
            Error: new MachineError("usage", "USAGE"))), "result");
        Assert.Equal(["stage", "code"], Names(minimal.RootElement.GetProperty("error")));
    }

    [Fact]
    public void J08_CountsUseExplicitNumbersIncludingZeroAndOmitNulls()
    {
        // 値から件数を推定しない。P7 が operation/mode/段階に該当するキーだけを設定する。
        var counts = new MachineCounts(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        using var document = ParseLine(MachineOutput.Serialize(new MachineResultRecord("completed", 0, counts)), "result");
        var values = document.RootElement.GetProperty("counts").EnumerateObject().ToArray();
        Assert.Equal(["matched", "same_size", "modified", "missing", "skipped_special_file", "directory", "undetermined", "deleted", "delete_failed", "not_selected", "unprocessed"], values.Select(p => p.Name));
        for (var i = 0; i < values.Length; i++)
        {
            Assert.Equal(JsonValueKind.Number, values[i].Value.ValueKind);
            Assert.Equal(i, values[i].Value.GetInt32());
        }
        using var partial = ParseLine(MachineOutput.Serialize(new MachineResultRecord("fatal", 1,
            new MachineCounts(Undetermined: 0))), "result");
        Assert.Equal(["undetermined"], Names(partial.RootElement.GetProperty("counts")));
    }

    private static JsonDocument ParseLine(byte[] bytes, string type)
    {
        Assert.All(bytes, value => Assert.InRange(value, (byte)0, (byte)0x7F));
        Assert.Equal((byte)'{', bytes[0]);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(1, bytes.Count(value => value == (byte)'\n'));
        Assert.DoesNotContain(":null", Encoding.ASCII.GetString(bytes));
        var document = JsonDocument.Parse(bytes);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal(1, document.RootElement.GetProperty("v").GetInt32());
        Assert.Equal(type, document.RootElement.GetProperty("type").GetString());
        return document;
    }

    private static string[] Names(JsonElement value) => value.EnumerateObject().Select(p => p.Name).ToArray();
}
