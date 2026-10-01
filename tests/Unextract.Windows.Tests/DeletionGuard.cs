namespace Unextract.Windows.Tests;

public sealed class GuardViolationException(string message) : Exception(message);

// 実削除を伴うテストの安全装置 (テスト側。製品の安全装置の代わりにしない)。
// テストが自分で作った一意な fixture ディレクトリ (bin/.../fixtures/<一意名>/) を最初に確定し、削除を指示する直前に、
// 削除するハンドルから得た最終パスがその内側であること (\ 境界付きの序数比較) と、fixture から対象の親までの各ディレクトリと
// 対象自身が reparse point でないことを確かめる。違反なら GuardViolationException で中止する。
// 残る隙間: 親ディレクトリの確認はパスで開き直して行うため、確認から削除の指示までの短い間の差し替えは防げない。
// fixture を操作するのはテスト自身だけである。
public sealed class DeletionGuard
{
    private const uint FileAttributeReparsePoint = 0x400;

    private readonly List<string> _violations = [];

    public DeletionGuard(string fixtureDirectory)
    {
        var fixturesRoot = Path.Combine(AppContext.BaseDirectory, "fixtures") + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(fixtureDirectory);
        if (!full.StartsWith(fixturesRoot, StringComparison.OrdinalIgnoreCase)
            || full.Length <= fixturesRoot.Length
            || full[fixturesRoot.Length..].Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new GuardViolationException($"fixture は fixtures/<一意名>/ の直下でなければならない: {full}");
        }

        RootFinalPath = FinalPathOfDirectory(full);
        CheckNotReparse(RootFinalPath);
    }

    // fixture のハンドルから得た最終パス (\\?\ 形式)。
    public string RootFinalPath { get; }

    public IReadOnlyList<string> Violations => _violations;

    public int CheckCount { get; private set; }

    // finalPath はハンドルから得た最終パスでなければならない (利用者の入力文字列を使わない)。
    public void Check(string finalPath)
    {
        CheckCount++;
        var prefix = RootFinalPath + "\\";
        if (!finalPath.StartsWith(prefix, StringComparison.Ordinal) || finalPath.Length <= prefix.Length)
        {
            Fail($"fixture の外: {finalPath} (fixture {RootFinalPath})");
        }

        var components = finalPath[prefix.Length..].Split('\\');
        if (components.Any(c => c.Length == 0 || c is "." or ".."))
        {
            Fail($"想定外の形式: {finalPath}");
        }

        var current = RootFinalPath;
        foreach (var component in components)
        {
            current += "\\" + component;
            CheckNotReparse(current);
        }
    }

    public void Check(Unextract.Core.Target.IComparisonHandle handle)
    {
        var finalPath = handle.GetFinalPath();
        if (!finalPath.Succeeded)
        {
            Fail($"最終パスを取得できない: {finalPath.Describe()}");
        }

        Check(finalPath.Value!);
    }

    private void CheckNotReparse(string path)
    {
        var opened = HandleOpener.OpenForConfirmation(path);
        if (!opened.Succeeded)
        {
            Fail($"確認のために開けない: {path} ({opened})");
        }

        using var handle = opened.Value!;
        var tag = FileInformation.GetAttributeTagInformation(handle);
        if (!tag.Succeeded || (tag.Value.Attributes & FileAttributeReparsePoint) != 0 || tag.Value.ReparseTag != 0)
        {
            Fail($"reparse point または属性を取得できない: {path}");
        }
    }

    private static string FinalPathOfDirectory(string path)
    {
        var opened = HandleOpener.OpenForConfirmation(path);
        if (!opened.Succeeded)
        {
            throw new GuardViolationException($"fixture を開けない: {path} ({opened})");
        }

        using var handle = opened.Value;
        var finalPath = FileInformation.GetFinalPath(handle);
        return finalPath.Succeeded ? finalPath.Value : throw new GuardViolationException($"fixture の最終パスを取得できない: {path}");
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Fail(string message)
    {
        _violations.Add(message);
        throw new GuardViolationException(message);
    }
}
