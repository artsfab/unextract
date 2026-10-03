namespace Unextract.Core.Target;

// 128 ビット File ID (FILE_ID_128)。比較は値の完全一致だけに使う。
public readonly record struct FileId(ulong Low, ulong High)
{
    public override string ToString() => $"{High:X16}{Low:X16}";
}

// ボリュームシリアル番号と File ID の組 (FILE_ID_INFO)。個体の同一性の判定に使う。
public readonly record struct VolumeFileId(ulong VolumeSerialNumber, FileId FileId);

// docs/spec/filesystem.md#target-root のtarget確認の確認用ハンドル (FILE_READ_ATTRIBUTES のみ、OPEN_REPARSE_POINT 付き) から得る情報。
public readonly record struct TargetConfirmation(uint Attributes, uint ReparseTag, VolumeFileId Id);

// 列挙用・target ルート保持用ハンドルの検証に使う情報 (docs/spec/filesystem.md#real-names の 1)。FinalPath は \\?\ 形式のまま。
public readonly record struct DirectoryHandleInfo(VolumeFileId Id, bool IsDirectory, uint Attributes, uint ReparseTag, string FinalPath);

// 列挙で返る1項目 (FileIdExtdDirectoryInfo)。名前はロング名。"." と ".." は返さない。
public readonly record struct DirectoryItem(string Name, uint Attributes, uint ReparseTag, FileId FileId);

public enum DirectoryEnumerationStepKind
{
    Item,
    End,
    Failed,
}

// 列挙の1歩。終端 (ERROR_NO_MORE_FILES) は End、それ以外のエラーは Failed。
public readonly record struct DirectoryEnumerationStep(DirectoryEnumerationStepKind Kind, DirectoryItem Item, int Error, string? Operation)
{
    public static DirectoryEnumerationStep OfItem(DirectoryItem item) => new(DirectoryEnumerationStepKind.Item, item, 0, null);

    public static DirectoryEnumerationStep EndOfDirectory { get; } = new(DirectoryEnumerationStepKind.End, default, 0, null);

    public static DirectoryEnumerationStep Fail(int error, string operation) =>
        new(DirectoryEnumerationStepKind.Failed, default, error, operation);
}

public readonly record struct StandardInformation(long EndOfFile, uint NumberOfLinks, bool DeletePending, bool IsDirectory);

public readonly record struct BasicInformation(long LastWriteTime, long ChangeTime, uint Attributes);

public readonly record struct AttributeTagInformation(uint Attributes, uint ReparseTag);

// 名前は "::$DATA" (既定のデータストリーム)、":name:$DATA" (名前付きストリーム) の形。
public readonly record struct StreamEntry(string Name, long Size)
{
    public const string DefaultDataStream = "::$DATA";
}

// 項目の列挙。1件ずつ返し、End または Failed の後は同じ結果を返し続ける。
public interface IDirectoryEnumeration
{
    DirectoryEnumerationStep Next();
}

// 開いたディレクトリ (target ルートの保持用ハンドル、または列挙用ハンドル) の不透明な表現。
public interface IDirectoryHandle : IDisposable
{
    ProbeResult<DirectoryHandleInfo> GetInfo();

    // ボリュームのファイルシステム名 (NTFS 判定用)。
    ProbeResult<string> GetFileSystemName();

    // 同じハンドルで項目を列挙する (FileIdExtdDirectoryRestartInfo から始める)。
    IDirectoryEnumeration Enumerate();
}

// 比較用ハンドル (docs/spec/filesystem.md#handles) の不透明な表現。情報取得と、先頭からの逐次読み取りができる。
public interface IComparisonHandle : IDisposable
{
    ProbeResult<VolumeFileId> GetVolumeFileId();

    ProbeResult<StandardInformation> GetStandardInformation();

    ProbeResult<BasicInformation> GetBasicInformation();

    ProbeResult<AttributeTagInformation> GetAttributeTagInformation();

    ProbeResult<IReadOnlyList<StreamEntry>> GetStreams();

    // 親ディレクトリの File ID (FSCTL_READ_FILE_USN_DATA)。
    ProbeResult<FileId> GetParentFileId();

    // GetFinalPathNameByHandleW(FILE_NAME_NORMALIZED | VOLUME_NAME_DOS) の \\?\ 形式のまま。
    ProbeResult<string> GetFinalPath();

    // 内容を先頭から順に読む。0 は終端。
    ProbeResult<int> Read(Span<byte> buffer);
}

// target に対する操作の抽象 (docs/spec/filesystem.md#target-root のtarget確認、docs/spec/filesystem.md#real-names、docs/spec/filesystem.md#handles)。Win32 の実装は Unextract.Windows が持つ。
// オープンの失敗は OS のエラーコードで返す。取得の失敗は部分的な値を返さない。
public interface IFileSystemProbe
{
    // docs/spec/filesystem.md#target-root のtarget確認の確認用ハンドル (FILE_READ_ATTRIBUTES のみ、FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT) で
    // path を開き、FileAttributeTagInfo と FileIdInfo を取得して閉じる。
    ProbeResult<TargetConfirmation> ConfirmTargetFinalComponent(string path);

    // docs/spec/filesystem.md#handles の target ルートの保持用ハンドル。呼び出し側が実行終了まで保持する。
    ProbeResult<IDirectoryHandle> OpenTargetRoot(string path);

    // docs/spec/filesystem.md#handles の列挙用ハンドル (target ルート以外のディレクトリ)。
    ProbeResult<IDirectoryHandle> OpenDirectoryForEnumeration(string path);

    // docs/spec/filesystem.md#handles の比較用ハンドル。
    ProbeResult<IComparisonHandle> OpenForComparison(string path);

    // ZIP 自身の個体判定 (docs/spec/filesystem.md#special-files) 用。FILE_READ_ATTRIBUTES のみで開いてボリュームシリアルと File ID を返す。
    // ZIP は FileShare.Read で保持されていて改名・削除されないため、パスで開き直しても同じ個体になる。
    ProbeResult<VolumeFileId> GetFileIdentity(string path);
}
