using Microsoft.Win32.SafeHandles;
using CoreTarget = Unextract.Core.Target;

namespace Unextract.Windows;

// Core の IFileSystemProbe の Win32 実装 (SPEC §3 の手順1、§6.2、§8.1)。C-1 の HandleOpener / FileInformation /
// DirectoryEnumerator を包むだけで、判定 (reparse・File ID の一致・NTFS・拒否対象など) は Core の
// TargetRootValidator と ClassificationPipeline が行う。オープン失敗は Win32 エラーコードのまま返す。
// 取得の失敗・想定外の応答形式は部分的な値を返さずに失敗を返す。
// 削除フェーズ (IDeletionProbe) では、削除用ハンドルと識別確認のハンドルを開くだけで、判定 (再検証・エラーの分類) は Core の
// DeletionPhase が行う。削除は削除用ハンドルへの指示 (WindowsDeletionHandle.SetDispositionEx) だけで、パスベースの削除・改名の API は使わない。
// Windows 層と Core に同名の型 (VolumeFileId など) があるため、Core の型は CoreTarget. で明示する。
public sealed class WindowsFileSystemProbe : CoreTarget.IFileSystemProbe, CoreTarget.IDeletionProbe
{
    public CoreTarget.ProbeResult<CoreTarget.IDeletionHandle> OpenForDeletion(string path)
    {
        var opened = HandleOpener.OpenForDeletion(path);
        return opened.Succeeded
            ? CoreTarget.ProbeResult<CoreTarget.IDeletionHandle>.Ok(new WindowsDeletionHandle(opened.Value))
            : opened.Fail<CoreTarget.IDeletionHandle, SafeFileHandle>();
    }

    // 識別確認 (SPEC §8.4)。比較する項目 (File ID、親 File ID、最終パス、Directory、reparse、DeletePending) を取得して閉じる。
    public CoreTarget.ProbeResult<CoreTarget.IdentityCheckInfo> CheckIdentity(string path)
    {
        var opened = HandleOpener.OpenForIdentityCheck(path);
        if (!opened.Succeeded)
        {
            return opened.Fail<CoreTarget.IdentityCheckInfo, SafeFileHandle>();
        }

        using var handle = opened.Value;
        var id = FileInformation.GetVolumeFileId(handle);
        if (!id.Succeeded)
        {
            return id.Fail<CoreTarget.IdentityCheckInfo, VolumeFileId>();
        }

        var parent = FileInformation.GetParentFileId(handle);
        if (!parent.Succeeded)
        {
            return parent.Fail<CoreTarget.IdentityCheckInfo, FileId128>();
        }

        var finalPath = FileInformation.GetFinalPath(handle);
        if (!finalPath.Succeeded)
        {
            return finalPath.Fail<CoreTarget.IdentityCheckInfo, string>();
        }

        var standard = FileInformation.GetStandardInformation(handle);
        if (!standard.Succeeded)
        {
            return standard.Fail<CoreTarget.IdentityCheckInfo, StandardInformation>();
        }

        var tag = FileInformation.GetAttributeTagInformation(handle);
        if (!tag.Succeeded)
        {
            return tag.Fail<CoreTarget.IdentityCheckInfo, AttributeTagInformation>();
        }

        return CoreTarget.ProbeResult<CoreTarget.IdentityCheckInfo>.Ok(new CoreTarget.IdentityCheckInfo(
            id.Value.ToCore(),
            parent.Value.ToCore(),
            finalPath.Value,
            standard.Value.IsDirectory,
            standard.Value.DeletePending,
            tag.Value.Attributes,
            tag.Value.ReparseTag));
    }

    public CoreTarget.ProbeResult<CoreTarget.TargetConfirmation> ConfirmTargetFinalComponent(string path)
    {
        var opened = HandleOpener.OpenForConfirmation(path);
        if (!opened.Succeeded)
        {
            return opened.Fail<CoreTarget.TargetConfirmation, SafeFileHandle>();
        }

        using var handle = opened.Value;
        var tag = FileInformation.GetAttributeTagInformation(handle);
        if (!tag.Succeeded)
        {
            return tag.Fail<CoreTarget.TargetConfirmation, AttributeTagInformation>();
        }

        var id = FileInformation.GetVolumeFileId(handle);
        if (!id.Succeeded)
        {
            return id.Fail<CoreTarget.TargetConfirmation, VolumeFileId>();
        }

        return CoreTarget.ProbeResult<CoreTarget.TargetConfirmation>.Ok(
            new CoreTarget.TargetConfirmation(tag.Value.Attributes, tag.Value.ReparseTag, id.Value.ToCore()));
    }

    public CoreTarget.ProbeResult<CoreTarget.IDirectoryHandle> OpenTargetRoot(string path) =>
        WrapDirectory(HandleOpener.OpenTargetRoot(path));

    public CoreTarget.ProbeResult<CoreTarget.IDirectoryHandle> OpenDirectoryForEnumeration(string path) =>
        WrapDirectory(HandleOpener.OpenDirectoryForEnumeration(path));

    public CoreTarget.ProbeResult<CoreTarget.IComparisonHandle> OpenForComparison(string path)
    {
        var opened = HandleOpener.OpenForComparison(path);
        return opened.Succeeded
            ? CoreTarget.ProbeResult<CoreTarget.IComparisonHandle>.Ok(new WindowsComparisonHandle(opened.Value))
            : opened.Fail<CoreTarget.IComparisonHandle, SafeFileHandle>();
    }

    public CoreTarget.ProbeResult<CoreTarget.VolumeFileId> GetFileIdentity(string path)
    {
        var opened = HandleOpener.OpenForAttributes(path);
        if (!opened.Succeeded)
        {
            return opened.Fail<CoreTarget.VolumeFileId, SafeFileHandle>();
        }

        using var handle = opened.Value;
        var id = FileInformation.GetVolumeFileId(handle);
        return id.Succeeded
            ? CoreTarget.ProbeResult<CoreTarget.VolumeFileId>.Ok(id.Value.ToCore())
            : id.Fail<CoreTarget.VolumeFileId, VolumeFileId>();
    }

    private static CoreTarget.ProbeResult<CoreTarget.IDirectoryHandle> WrapDirectory(Win32Result<SafeFileHandle> opened) =>
        opened.Succeeded
            ? CoreTarget.ProbeResult<CoreTarget.IDirectoryHandle>.Ok(new WindowsDirectoryHandle(opened.Value))
            : opened.Fail<CoreTarget.IDirectoryHandle, SafeFileHandle>();
}

// Windows 層の型と Win32Result を Core の型に写す。
internal static class CoreConversions
{
    public static CoreTarget.FileId ToCore(this FileId128 id) => new(id.Low, id.High);

    public static CoreTarget.VolumeFileId ToCore(this VolumeFileId id) => new(id.VolumeSerialNumber, id.FileId.ToCore());

    public static CoreTarget.ProbeResult<TCore> Fail<TCore, TWin>(this Win32Result<TWin> result) =>
        CoreTarget.ProbeResult<TCore>.Fail(result.Error, result.Operation ?? "Win32");
}
