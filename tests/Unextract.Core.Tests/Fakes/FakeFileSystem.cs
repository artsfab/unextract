using Unextract.Core.Target;

namespace Unextract.Core.Tests.Fakes;

// 注入できる失敗の種類。FakeNode.Errors に Win32 エラーコードを設定する。
public enum FakeOp
{
    ConfirmTarget,
    OpenTargetRoot,
    OpenEnumeration,
    OpenComparison,
    GetFileIdentity,
    DirectoryInfo,
    FileSystemName,
    Enumerate,
    VolumeFileId,
    Standard,
    Basic,
    AttributeTag,
    Streams,
    ParentFileId,
    FinalPath,
    Read,
    OpenDeletion,
    CheckIdentity,
    Disposition,
}

internal sealed class FakeNode
{
    public const uint ReparseTagMountPoint = 0xA0000003;
    public const uint ReparseTagSymlink = 0xA000000C;

    public required string Name { get; set; }

    public bool IsDirectory { get; init; }

    public uint Attributes { get; set; }

    public uint ReparseTag { get; set; }

    public FileId Id { get; set; }

    public ulong VolumeSerial { get; set; } = FakeFileSystem.DefaultVolumeSerial;

    public byte[] Content { get; set; } = [];

    public uint Links { get; set; } = 1;

    public List<StreamEntry> ExtraStreams { get; } = [];

    public long LastWriteTime { get; set; } = 133_000_000_000_000_000;

    public long ChangeTime { get; set; } = 133_000_000_000_000_001;

    public FakeNode? Parent { get; set; }

    public List<FakeNode> Children { get; } = [];

    // 列挙の結果を差し替える (重複・見落とし・File ID の食い違いの注入)。
    public List<DirectoryItem>? EnumerationOverride { get; set; }

    // 列挙で返す項目の数がこれに達したら Errors[Enumerate] のエラーで失敗する。
    public int EnumerationFailAfter { get; set; }

    // 最終パスの差し替え (途中の junction 化・大小文字だけの改名・UNC の模擬)。
    public string? FinalPathOverride { get; set; }

    // 最終成分を開くとき (OPEN_REPARSE_POINT なし) にたどるリンク先。
    public FakeNode? LinkTarget { get; set; }

    // 内容の読み取りが失敗する位置 (Errors[Read] のエラー)。
    public long ReadFailAt { get; set; } = -1;

    // 読み取りで例外を投げる (例外経路のテスト用)。
    public bool ThrowOnRead { get; set; }

    public Dictionary<FakeOp, int> Errors { get; } = [];

    // 削除の指示を受けて、最後のハンドルが閉じるのを待っている状態 (FILE_STANDARD_INFO.DeletePending)。
    public bool DeletePending { get; set; }

    // 削除の指示が成功を返すが何もしない (DELETE ビットを含まない flags 相当、テスト D18)。
    public bool DispositionHasNoEffect { get; set; }

    public bool IsReparse => (Attributes & 0x400) != 0 || ReparseTag != 0;
}

// target 側だけを表す偽ファイルシステム (テスト専用)。ボリューム C: の木を持ち、パスは大小文字を区別せずに解決する
// (Windows の既定と同じ)。呼び出し記録、エラー注入、開いているハンドルの追跡、解析途中の変更の注入 (フック) を持つ。
internal sealed class FakeFileSystem : IFileSystemProbe, IDeletionProbe
{
    public const ulong DefaultVolumeSerial = 0x1234_5678_9ABC_DEF0;
    public const string DrivePrefix = @"\\?\C:\";

    private readonly HashSet<FakeHandle> _open = [];
    private ulong _nextId = 100;

    public FakeFileSystem()
    {
        Drive = new FakeNode { Name = "C:", IsDirectory = true, Attributes = 0x10, Id = NextId() };
    }

    public FakeNode Drive { get; }

    public string FileSystemName { get; set; } = "NTFS";

    public List<string> Calls { get; } = [];

    public int OpenHandleCount => _open.Count;

    public int OpenComparisonCount => _open.Count(h => h is FakeComparisonHandle);

    public int MaxConcurrentComparisons { get; private set; }

    public int ComparisonOpenCount { get; private set; }

    public int ComparisonCloseCount { get; private set; }

    public int OpenDeletionHandleCount => _open.Count(h => h is FakeDeletionHandle);

    public int DeletionOpenCount { get; private set; }

    public int DeletionCloseCount { get; private set; }

    // 削除が成立した (削除用ハンドルのクローズで名前が消えた) ノード。
    public List<FakeNode> Deleted { get; } = [];

    // 確認用ハンドルを閉じた後、保持用ハンドルを開く前 (target の差し替えの注入)。
    public Action? AfterConfirmTarget { get; set; }

    // 比較用ハンドルを開く直前 (解析中の変化の注入)。
    public Action<string>? BeforeOpenComparison { get; set; }

    public FileId NextId() => new(_nextId++, 0);

    public FakeNode AddDirectory(string path, uint attributes = 0x10)
    {
        var (parent, name) = ParentOf(path);
        return Attach(parent, new FakeNode { Name = name, IsDirectory = true, Attributes = attributes, Id = NextId() });
    }

    public FakeNode AddFile(string path, byte[] content, uint attributes = 0x20)
    {
        var (parent, name) = ParentOf(path);
        return Attach(parent, new FakeNode { Name = name, Attributes = attributes, Content = content, Id = NextId() });
    }

    public FakeNode AddJunction(string path, FakeNode? target = null) =>
        AddReparse(path, isDirectory: true, FakeNode.ReparseTagMountPoint, target);

    public FakeNode AddReparse(string path, bool isDirectory, uint tag, FakeNode? target = null)
    {
        var (parent, name) = ParentOf(path);
        var attributes = 0x400u | (isDirectory ? 0x10u : 0x20u);
        return Attach(parent, new FakeNode
        {
            Name = name,
            IsDirectory = isDirectory,
            Attributes = attributes,
            ReparseTag = tag,
            Id = NextId(),
            LinkTarget = target,
        });
    }

    // path ("C:\..." または "\\?\C:\...") のノード。見つからなければ null。
    public FakeNode? Find(string path) => Resolve(path, followFinal: false).Node;

    public FakeNode Get(string path) => Find(path) ?? throw new InvalidOperationException($"no node: {path}");

    public string FinalPathOf(FakeNode node)
    {
        if (node.FinalPathOverride is { } overridden)
        {
            return overridden;
        }

        var names = new Stack<string>();
        for (var current = node; current != Drive; current = current.Parent!)
        {
            names.Push(current.Name);
        }

        return DrivePrefix + string.Join('\\', names);
    }

    public void Remove(FakeNode node)
    {
        node.Parent!.Children.Remove(node);
        node.Parent = null;
    }

    public ProbeResult<TargetConfirmation> ConfirmTargetFinalComponent(string path)
    {
        Calls.Add($"ConfirmTarget {path}");
        var (node, error) = Open(path, FakeOp.ConfirmTarget, followFinal: false);
        if (node is null)
        {
            return ProbeResult<TargetConfirmation>.Fail(error, "ConfirmTarget");
        }

        var confirmation = new TargetConfirmation(node.Attributes, node.ReparseTag, new VolumeFileId(node.VolumeSerial, node.Id));
        AfterConfirmTarget?.Invoke();
        return ProbeResult<TargetConfirmation>.Ok(confirmation);
    }

    public ProbeResult<IDirectoryHandle> OpenTargetRoot(string path)
    {
        Calls.Add($"OpenTargetRoot {path}");
        var (node, error) = Open(path, FakeOp.OpenTargetRoot, followFinal: true);
        return node is null
            ? ProbeResult<IDirectoryHandle>.Fail(error, "OpenTargetRoot")
            : ProbeResult<IDirectoryHandle>.Ok(Track(new FakeDirectoryHandle(this, node, "root")));
    }

    public ProbeResult<IDirectoryHandle> OpenDirectoryForEnumeration(string path)
    {
        Calls.Add($"OpenEnumeration {path}");
        var (node, error) = Open(path, FakeOp.OpenEnumeration, followFinal: false);
        return node is null
            ? ProbeResult<IDirectoryHandle>.Fail(error, "OpenEnumeration")
            : ProbeResult<IDirectoryHandle>.Ok(Track(new FakeDirectoryHandle(this, node, "enumeration")));
    }

    public ProbeResult<IComparisonHandle> OpenForComparison(string path)
    {
        BeforeOpenComparison?.Invoke(path);
        Calls.Add($"OpenComparison {path}");
        var (node, error) = Open(path, FakeOp.OpenComparison, followFinal: false);
        if (node is null)
        {
            return ProbeResult<IComparisonHandle>.Fail(error, "OpenComparison");
        }

        ComparisonOpenCount++;
        var handle = Track(new FakeComparisonHandle(this, node));
        MaxConcurrentComparisons = Math.Max(MaxConcurrentComparisons, OpenComparisonCount);
        return ProbeResult<IComparisonHandle>.Ok(handle);
    }

    public ProbeResult<VolumeFileId> GetFileIdentity(string path)
    {
        Calls.Add($"GetFileIdentity {path}");
        var (node, error) = Open(path, FakeOp.GetFileIdentity, followFinal: false);
        return node is null
            ? ProbeResult<VolumeFileId>.Fail(error, "GetFileIdentity")
            : ProbeResult<VolumeFileId>.Ok(new VolumeFileId(node.VolumeSerial, node.Id));
    }

    // 削除用オープン (SPEC §8.1)。実機と同じく、ディレクトリと削除保留中の対象は 5 (ERROR_ACCESS_DENIED)。
    public ProbeResult<IDeletionHandle> OpenForDeletion(string path)
    {
        Calls.Add($"OpenDeletion {path}");
        var (node, error) = Open(path, FakeOp.OpenDeletion, followFinal: false);
        if (node is null)
        {
            return ProbeResult<IDeletionHandle>.Fail(error, "OpenDeletion");
        }

        if (node.IsDirectory || node.DeletePending)
        {
            return ProbeResult<IDeletionHandle>.Fail(5, "OpenDeletion");
        }

        DeletionOpenCount++;
        return ProbeResult<IDeletionHandle>.Ok(Track(new FakeDeletionHandle(this, node)));
    }

    // 識別確認 (SPEC §8.4)。削除保留中は実機と同じく識別確認のオープンも 5。
    public ProbeResult<IdentityCheckInfo> CheckIdentity(string path)
    {
        Calls.Add($"CheckIdentity {path}");
        var (node, error) = Open(path, FakeOp.CheckIdentity, followFinal: false);
        if (node is null)
        {
            return ProbeResult<IdentityCheckInfo>.Fail(error, "CheckIdentity");
        }

        if (node.DeletePending)
        {
            return ProbeResult<IdentityCheckInfo>.Fail(5, "CheckIdentity");
        }

        return ProbeResult<IdentityCheckInfo>.Ok(new IdentityCheckInfo(
            new VolumeFileId(node.VolumeSerial, node.Id), node.Parent!.Id, FinalPathOf(node), node.IsDirectory, node.DeletePending,
            node.Attributes, node.ReparseTag));
    }

    internal void Closed(FakeHandle handle)
    {
        if (_open.Remove(handle))
        {
            Calls.Add($"Close {handle.Kind} {handle.Path}");
            if (handle is FakeComparisonHandle)
            {
                ComparisonCloseCount++;
            }

            if (handle is FakeDeletionHandle)
            {
                DeletionCloseCount++;

                // POSIX semantics: 削除を指示したハンドルのクローズで名前が消える (他者の削除保留はこのハンドルでは消さない)。
                if (handle is FakeDeletionHandle { DispositionSet: true } && handle.Node.DeletePending && handle.Node.Parent is not null)
                {
                    Remove(handle.Node);
                    Deleted.Add(handle.Node);
                }
            }
        }
    }

    private static FakeNode Attach(FakeNode parent, FakeNode node)
    {
        node.Parent = parent;
        parent.Children.Add(node);
        return node;
    }

    private T Track<T>(T handle)
        where T : FakeHandle
    {
        _open.Add(handle);
        return handle;
    }

    private (FakeNode Parent, string Name) ParentOf(string path)
    {
        var parts = Split(path);
        var parent = Drive;
        foreach (var part in parts[..^1])
        {
            parent = parent.Children.FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"no parent for {path}");
        }

        return (parent, parts[^1]);
    }

    private (FakeNode? Node, int Error) Open(string path, FakeOp op, bool followFinal)
    {
        var (node, error) = Resolve(path, followFinal);
        if (node is null)
        {
            return (null, error);
        }

        return node.Errors.TryGetValue(op, out var injected) ? (null, injected) : (node, 0);
    }

    // 途中の reparse はリンク先があればたどり、無ければ 3 (ERROR_PATH_NOT_FOUND)。
    // 存在しない最終成分は 2、存在しない途中の成分・ファイルを親とするパスは 3。
    private (FakeNode? Node, int Error) Resolve(string path, bool followFinal)
    {
        string[] parts;
        try
        {
            parts = Split(path);
        }
        catch (ArgumentException)
        {
            return (null, 3);
        }

        var current = Drive;
        for (var i = 0; i < parts.Length; i++)
        {
            if (current.IsReparse)
            {
                if (current.LinkTarget is null)
                {
                    return (null, 3);
                }

                current = current.LinkTarget;
            }

            if (!current.IsDirectory)
            {
                return (null, 3);
            }

            var next = current.Children.FirstOrDefault(c => string.Equals(c.Name, parts[i], StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                return (null, i == parts.Length - 1 ? 2 : 3);
            }

            current = next;
        }

        if (followFinal && current.IsReparse && current.LinkTarget is { } target)
        {
            current = target;
        }

        return (current, 0);
    }

    private static string[] Split(string path)
    {
        var body = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        if (!body.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"unsupported path: {path}");
        }

        var rest = body[3..];
        return rest.Length == 0 ? [] : rest.Split('\\');
    }
}

internal abstract class FakeHandle(FakeFileSystem fs, FakeNode node, string kind) : IDisposable
{
    public FakeFileSystem FileSystem { get; } = fs;

    public FakeNode Node { get; } = node;

    public string Kind { get; } = kind;

    public string Path { get; } = fs.FinalPathOf(node);

    public void Dispose() => FileSystem.Closed(this);

    protected ProbeResult<T> Get<T>(FakeOp op, Func<T> value)
    {
        FileSystem.Calls.Add($"{op} {Path}");
        return Node.Errors.TryGetValue(op, out var error)
            ? ProbeResult<T>.Fail(error, op.ToString())
            : ProbeResult<T>.Ok(value());
    }
}

internal sealed class FakeDirectoryHandle(FakeFileSystem fs, FakeNode node, string kind) : FakeHandle(fs, node, kind), IDirectoryHandle
{
    public ProbeResult<DirectoryHandleInfo> GetInfo() => Get(FakeOp.DirectoryInfo, () => new DirectoryHandleInfo(
        new VolumeFileId(Node.VolumeSerial, Node.Id), Node.IsDirectory, Node.Attributes, Node.ReparseTag, FileSystem.FinalPathOf(Node)));

    public ProbeResult<string> GetFileSystemName() => Get(FakeOp.FileSystemName, () => FileSystem.FileSystemName);

    public IDirectoryEnumeration Enumerate()
    {
        FileSystem.Calls.Add($"Enumerate {Path}");
        var items = Node.EnumerationOverride
            ?? Node.Children.Select(c => new DirectoryItem(c.Name, c.Attributes, c.ReparseTag, c.Id)).ToList();
        return new FakeEnumeration(items, Node);
    }

    private sealed class FakeEnumeration(List<DirectoryItem> items, FakeNode node) : IDirectoryEnumeration
    {
        private int _index;

        public DirectoryEnumerationStep Next()
        {
            if (node.Errors.TryGetValue(FakeOp.Enumerate, out var error) && _index >= node.EnumerationFailAfter)
            {
                return DirectoryEnumerationStep.Fail(error, "Enumerate");
            }

            return _index < items.Count
                ? DirectoryEnumerationStep.OfItem(items[_index++])
                : DirectoryEnumerationStep.EndOfDirectory;
        }
    }
}

internal sealed class FakeComparisonHandle(FakeFileSystem fs, FakeNode node) : FakeFileHandle(fs, node, "comparison");

// 削除用ハンドル。読み取りと削除の指示も呼び出し記録に残す (テスト D03、D17)。
internal sealed class FakeDeletionHandle(FakeFileSystem fs, FakeNode node) : FakeFileHandle(fs, node, "deletion"), IDeletionHandle
{
    public List<uint> DispositionFlags { get; } = [];

    public bool DispositionSet { get; private set; }

    public ProbeResult<bool> SetDispositionEx(uint flags)
    {
        FileSystem.Calls.Add($"{FakeOp.Disposition} 0x{flags:X} {Path}");
        DispositionFlags.Add(flags);
        if (Node.Errors.TryGetValue(FakeOp.Disposition, out var error))
        {
            return ProbeResult<bool>.Fail(error, "Disposition");
        }

        // read-only は IGNORE_READONLY_ATTRIBUTE なしでは指示が 5 で失敗する (PoC 1〜3)。
        if ((Node.Attributes & 0x1) != 0 && (flags & 0x10) == 0)
        {
            return ProbeResult<bool>.Fail(5, "Disposition");
        }

        if (!Node.DispositionHasNoEffect && (flags & 0x1) != 0)
        {
            Node.DeletePending = true;
            DispositionSet = true;
        }

        return ProbeResult<bool>.Ok(true);
    }

    public override ProbeResult<int> Read(Span<byte> buffer)
    {
        FileSystem.Calls.Add($"Read {Path}");
        return base.Read(buffer);
    }
}

// 比較用・削除用ハンドルの共通部分。
internal abstract class FakeFileHandle(FakeFileSystem fs, FakeNode node, string kind) : FakeHandle(fs, node, kind), IComparisonHandle
{
    private const int ErrorHandleEof = 38;
    private long _position;

    public ProbeResult<VolumeFileId> GetVolumeFileId() =>
        Get(FakeOp.VolumeFileId, () => new VolumeFileId(Node.VolumeSerial, Node.Id));

    public ProbeResult<StandardInformation> GetStandardInformation() =>
        Get(FakeOp.Standard, () => new StandardInformation(Node.Content.LongLength, Node.Links, Node.DeletePending, Node.IsDirectory));

    public ProbeResult<BasicInformation> GetBasicInformation() =>
        Get(FakeOp.Basic, () => new BasicInformation(Node.LastWriteTime, Node.ChangeTime, Node.Attributes));

    public ProbeResult<AttributeTagInformation> GetAttributeTagInformation() =>
        Get(FakeOp.AttributeTag, () => new AttributeTagInformation(Node.Attributes, Node.ReparseTag));

    public ProbeResult<IReadOnlyList<StreamEntry>> GetStreams()
    {
        // データストリームを持たないディレクトリでは実機と同じく ERROR_HANDLE_EOF で失敗する。
        if (Node.IsDirectory && Node.ExtraStreams.Count == 0 && !Node.Errors.ContainsKey(FakeOp.Streams))
        {
            FileSystem.Calls.Add($"{FakeOp.Streams} {Path}");
            return ProbeResult<IReadOnlyList<StreamEntry>>.Fail(ErrorHandleEof, "Streams");
        }

        return Get<IReadOnlyList<StreamEntry>>(FakeOp.Streams, () =>
        {
            var streams = new List<StreamEntry>();
            if (!Node.IsDirectory)
            {
                streams.Add(new StreamEntry(StreamEntry.DefaultDataStream, Node.Content.LongLength));
            }

            streams.AddRange(Node.ExtraStreams);
            return streams;
        });
    }

    public ProbeResult<FileId> GetParentFileId() => Get(FakeOp.ParentFileId, () => Node.Parent!.Id);

    public ProbeResult<string> GetFinalPath() => Get(FakeOp.FinalPath, () => FileSystem.FinalPathOf(Node));

    public virtual ProbeResult<int> Read(Span<byte> buffer)
    {
        if (Node.ThrowOnRead)
        {
            throw new InvalidOperationException("injected exception");
        }

        if (Node.ReadFailAt >= 0 && _position >= Node.ReadFailAt)
        {
            return ProbeResult<int>.Fail(Node.Errors.GetValueOrDefault(FakeOp.Read, 23), "Read");
        }

        var available = (int)Math.Min(buffer.Length, Node.Content.LongLength - _position);
        if (Node.ReadFailAt >= 0)
        {
            available = (int)Math.Min(available, Node.ReadFailAt - _position);
        }

        Node.Content.AsSpan((int)_position, available).CopyTo(buffer);
        _position += available;
        return ProbeResult<int>.Ok(available);
    }
}
