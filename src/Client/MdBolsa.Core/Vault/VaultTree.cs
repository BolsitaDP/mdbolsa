namespace MdBolsa.Core.Vault;

// The vault as a folder tree, for the shell's sidebar.
//
// Built from the relative paths the vault index already holds - no filesystem
// walk, no second source of truth. A path is a list of folder segments plus a
// file name, so a note at "Development/Notes/Docker.md" is Folder > Development >
// Folder > Notes > Docker, and folders exist only because some note lives under
// them.
//
// The tree is pure data: the shell decides how a node looks and what happens when
// it's clicked, which keeps this testable and keeps the UI out of Core.
public sealed record VaultTreeNode(
    string Name,
    string? RelativePath,
    bool IsFolder,
    IReadOnlyList<VaultTreeNode> Children)
{
    public static VaultTreeNode Folder(string name, IReadOnlyList<VaultTreeNode> children) =>
        new(name, RelativePath: null, IsFolder: true, children);

    public static VaultTreeNode Note(string name, string relativePath) =>
        new(name, relativePath, IsFolder: false, []);

    // The path a note lives at, or the folder's own path - what the UI shows
    // under a node without having to walk the tree again.
    public string FullPath => RelativePath ?? Name;
}

public static class VaultTree
{
    // Folders first, then notes, each group alphabetically and case-insensitively
    // so "Archive" and "archive" don't sort apart. This is the order a person
    // expects from a file tree; the flat note list elsewhere stays in plain path
    // order.
    public static IReadOnlyList<VaultTreeNode> Build(IEnumerable<NoteMetadata> notes)
    {
        var roots = new List<VaultTreeNode>();

        // Keyed by full folder path, so "Development/Notes" and "Development" are
        // different entries and the same folder is only created once. The empty
        // key *is* the root list - one list per level, not a parallel structure.
        var folders = new Dictionary<string, List<VaultTreeNode>>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = roots,
        };

        foreach (var note in notes)
        {
            var segments = note.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) continue;

            var parent = roots;

            // Walk - and create - the folders along the way.
            var currentPath = string.Empty;
            for (var depth = 0; depth < segments.Length - 1; depth++)
            {
                var folderName = segments[depth];
                currentPath = currentPath.Length == 0 ? folderName : $"{currentPath}/{folderName}";

                if (!folders.TryGetValue(currentPath, out var children))
                {
                    children = [];
                    folders[currentPath] = children;
                    parent.Add(VaultTreeNode.Folder(folderName, children));
                }

                parent = children;
            }

            var fileName = segments[^1];
            var displayName = fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^3]
                : fileName;

            parent.Add(VaultTreeNode.Note(displayName, note.RelativePath));
        }

        return SortAll(roots);
    }


    // Records are immutable, so a sorted tree is a new tree rather than a list
    // sorted in place.
    private static IReadOnlyList<VaultTreeNode> SortAll(IReadOnlyList<VaultTreeNode> nodes) =>
        nodes
            .OrderByDescending(node => node.IsFolder)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .Select(node => node with { Children = SortAll(node.Children) })
            .ToList();

    // Every note in the tree, in the order it appears. The shell uses this to
    // expand "select all" and to count what the sidebar is showing.
    public static IEnumerable<VaultTreeNode> NotesIn(IReadOnlyList<VaultTreeNode> nodes) =>
        nodes.SelectMany(node =>
            node.IsFolder ? NotesIn(node.Children) : [node]);

    // Every folder path in the tree, which is what the shell persists so a
    // folder stays folded or open across restarts.
    public static IEnumerable<string> FolderPaths(IReadOnlyList<VaultTreeNode> nodes, string prefix = "")
    {
        foreach (var node in nodes.Where(node => node.IsFolder))
        {
            var path = prefix.Length == 0 ? node.Name : $"{prefix}/{node.Name}";
            yield return path;
            foreach (var child in FolderPaths(node.Children, path)) yield return child;
        }
    }
}
