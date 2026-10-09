using Unextract.Gui.Models;

namespace Unextract.Gui.Tests;

// Target templates: the known variables, the last extension, no expansion of environment variables and the like; empty names, bad
// syntax, absolute path forms, Windows names and dot components; the separator of duplicate keys and drive roots.
public sealed class TargetTemplateTests
{
    [Theory]
    [InlineData(TargetTemplate.SameDirectory, @"C:\日本語 空白")]
    [InlineData(TargetTemplate.ArchiveDirectory, @"C:\日本語 空白\a.b")]
    [InlineData(@"D:\Work\{{archive.name}}", @"D:\Work\a.b")]
    [InlineData(@"D:\fixed", @"D:\fixed")]
    [InlineData(@"D:\%TEMP%\~\$HOME", @"D:\%TEMP%\~\$HOME")]
    [InlineData("D:/Work/{{archive.name}}/", "D:/Work/a.b/")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"D:\{literal}", @"D:\{literal}")]
    public void ResolvesOnlyKnownVariablesWithoutExpandingOtherExpressions(string template, string expected) =>
        Assert.Equal(expected, TargetTemplate.Resolve(template, @"C:\日本語 空白\a.b.ZIP"));

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData(@"C:relative")]
    [InlineData(@"\rooted")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\work")]
    [InlineData(@"1:\work")]
    [InlineData(@"C:\a\..\b")]
    [InlineData(@"C:\a\.\b")]
    [InlineData("C:/a/../b")]
    [InlineData(@"C:\trailing.")]
    [InlineData(@"C:\trailing ")]
    [InlineData(@"C:\CON.txt")]
    [InlineData(@"C:\nul .txt")]
    [InlineData(@"C:\Lpt1\child")]
    [InlineData("C:/COM\u00B9.txt")]
    [InlineData(@"C:\a:b")]
    [InlineData(@"C:\a?b")]
    [InlineData(@"C:\a*b")]
    [InlineData("C:/a\"b")]
    [InlineData("C:/a\u0000b")]
    [InlineData("C:/a\u000Ab")]
    [InlineData(@"{{Archive.dir}}")]
    [InlineData(@"{{archive.unknown}}")]
    [InlineData(@"{{archive.dir}")]
    [InlineData(@"{{archive.dir}}\{{archive.name")]
    [InlineData(@"C:\work}}")]
    [InlineData(@"{{{{archive.dir}}")]
    public void InvalidTemplatesAndDotComponentsAreRejectedBeforeNormalization(string template) =>
        Assert.Throws<ArgumentException>(() => TargetTemplate.Resolve(template, @"C:\archives\a.zip"));

    [Fact]
    public void EmptyArchiveNameIsRejectedWhenUsed()
    {
        Assert.Throws<ArgumentException>(() => TargetTemplate.Resolve(TargetTemplate.ArchiveDirectory, @"C:\archives\.zip"));
        Assert.Equal(@"C:\archives", TargetTemplate.Resolve(TargetTemplate.SameDirectory, @"C:\archives\.zip"));
    }

    [Fact]
    public void InsertedVariablesAreNotParsedAgain() =>
        Assert.Equal(@"C:\{{archive.name}}\a", TargetTemplate.Resolve(TargetTemplate.ArchiveDirectory,
            @"C:\{{archive.name}}\a.zip"));

    [Theory]
    [InlineData(@"C:\", "c:/")]
    [InlineData(@"C:\\", "c:////")]
    [InlineData(@"D:\Work\Child", "d:/work/child///")]
    [InlineData(@"D:\Work\\Child\", "d:/work/child")]
    public void DuplicateKeyPreservesDriveRootAndComparesSeparatorsAndCase(string a, string b)
    {
        Assert.True(StringComparer.OrdinalIgnoreCase.Equals(TargetTemplate.DuplicateKey(a), TargetTemplate.DuplicateKey(b)));
        Assert.Equal(@"C:\", TargetTemplate.DuplicateKey(@"C:\\"));
    }

    [Fact]
    public void DisplayEscapesDangerousCharactersWithoutChangingRawValues()
    {
        string raw = "C:/顔-\U0001F642/\u202E\u0085\u2028\u2066.txt";
        string display = DisplayText.Escape(raw);
        Assert.Equal("C:/顔-\U0001F642/\\u{202E}\\u{0085}\\u{2028}\\u{2066}.txt", display);
        Assert.Contains("\\u{E0001}", DisplayText.Escape("a\U000E0001b"), StringComparison.Ordinal);
        Assert.Equal(raw, TargetTemplate.Resolve(raw, @"C:\archives\a.zip"));
    }
}
