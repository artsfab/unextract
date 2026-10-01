using Unextract.Core.Results;

namespace Unextract.Core.Zip;

// ZIP 内部の構造の検査 (SPEC §4.3)。明示ディレクトリと、親成分から生じる暗黙ディレクトリを木で持ち、
// 成分ごとに OrdinalIgnoreCase で引いて、序数で異なれば大文字小文字の衝突とする。
internal sealed class ZipStructure
{
    private enum NodeKind
    {
        File,
        ExplicitDirectory,
        ImplicitDirectory,
    }

    private sealed class Node(string name, NodeKind kind)
    {
        public string Name { get; } = name;

        public NodeKind Kind { get; set; } = kind;

        public Dictionary<string, Node>? Children { get; set; }
    }

    private readonly Node _root = new(string.Empty, NodeKind.ImplicitDirectory);

    public FatalKind? Add(IReadOnlyList<string> components, bool isDirectory)
    {
        var current = _root;
        for (var i = 0; i < components.Count; i++)
        {
            var component = components[i];
            var isLast = i == components.Count - 1;
            current.Children ??= new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);

            if (!current.Children.TryGetValue(component, out var node))
            {
                var kind = !isLast ? NodeKind.ImplicitDirectory
                    : isDirectory ? NodeKind.ExplicitDirectory
                    : NodeKind.File;
                node = new Node(component, kind);
                current.Children.Add(component, node);
                current = node;
                continue;
            }

            if (!string.Equals(node.Name, component, StringComparison.Ordinal))
            {
                return FatalKind.CaseInsensitiveCollision;
            }

            if (!isLast)
            {
                if (node.Kind == NodeKind.File)
                {
                    return FatalKind.FileUsedAsParent;
                }

                current = node;
                continue;
            }

            switch (node.Kind)
            {
                case NodeKind.File:
                    return isDirectory ? FatalKind.FileDirectoryConflict : FatalKind.DuplicateEntry;
                case NodeKind.ExplicitDirectory:
                    return isDirectory ? FatalKind.DuplicateEntry : FatalKind.FileDirectoryConflict;
                case NodeKind.ImplicitDirectory when !isDirectory:
                    // 先に現れた子エントリの親と同じパスのファイル。
                    return FatalKind.FileUsedAsParent;
                default:
                    node.Kind = NodeKind.ExplicitDirectory;
                    break;
            }
        }

        return null;
    }
}
