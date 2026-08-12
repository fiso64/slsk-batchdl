namespace Sockseek.Core.Models;

public static class DirectoryTransferPlanner
{
    public static DirectoryTransferPlan FromSnapshot(PeerDirectorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsComplete)
            throw new ArgumentException("A directory transfer plan requires a complete snapshot.", nameof(snapshot));
        if (snapshot.Files.Count == 0)
            throw new ArgumentException("A directory transfer plan cannot be created from an empty snapshot.", nameof(snapshot));

        string root = Normalize(snapshot.Identity.FolderPath).TrimEnd('\\');
        string displayRoot = Leaf(root);
        var entries = new List<DirectoryTransferEntry>(snapshot.Files.Count);

        foreach (var target in snapshot.Files)
        {
            string filename = Normalize(target.Filename);
            string prefix = root + "\\";
            if (!filename.StartsWith(prefix, StringComparison.Ordinal))
                throw new ArgumentException("A retrieved file lies outside the selected directory.", nameof(snapshot));

            string relative = filename[prefix.Length..];
            string[] components = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (components.Length == 0)
                throw new ArgumentException("A retrieved file has no leaf name.", nameof(snapshot));

            entries.Add(new DirectoryTransferEntry(target, components[..^1]));
        }

        return new DirectoryTransferPlan(displayRoot, entries);
    }

    private static string Normalize(string value) => value.Replace('/', '\\');

    private static string Leaf(string value)
    {
        int separator = value.LastIndexOf('\\');
        string leaf = separator < 0 ? value : value[(separator + 1)..];
        if (leaf.Length == 0)
            throw new ArgumentException("The selected directory has no display root.", nameof(value));
        return leaf;
    }
}
