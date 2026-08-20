namespace Sockseek.Core.Models;

/// <summary>Projects an already selected album directory into neutral exact-file work.</summary>
public static class AlbumTransferPlanner
{
    public static DirectoryTransferPlan FromSelectedDirectory(AlbumFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return FromSelectedDirectory(folder, folder.Files);
    }

    public static DirectoryTransferPlan FromSelectedDirectory(
        AlbumFolder folder,
        IReadOnlyList<AlbumFile> selectedFiles)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(selectedFiles);
        if (selectedFiles.Count == 0)
            throw new ArgumentException("A selected album directory cannot be empty.", nameof(folder));
        if (selectedFiles.Any(file => file is null || !folder.Files.Contains(file)))
            throw new ArgumentException("Every selected album file must belong to the directory.", nameof(selectedFiles));

        string root = folder.FolderPath.Replace('/', '\\').TrimEnd('\\');
        string displayRoot = Leaf(root);
        var entries = new List<DirectoryTransferEntry>(selectedFiles.Count);

        foreach (var file in selectedFiles)
        {
            string filename = file.Candidate.Target.Filename.Replace('/', '\\');
            string prefix = root + "\\";
            if (!filename.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            string relative = filename[prefix.Length..];
            string[] components = relative.Split('\\', StringSplitOptions.None);
            if (components.Length == 0)
                continue;
            try
            {
                entries.Add(new DirectoryTransferEntry(file.Candidate.Target, components[..^1]));
            }
            catch (ArgumentException)
            {
                // Preserve the rest of the selected album when one peer-provided
                // logical path cannot be represented safely.
            }
        }

        if (entries.Count == 0)
            throw new ArgumentException("The selected album contains no valid downloadable files.", nameof(folder));
        return new DirectoryTransferPlan(displayRoot, entries);
    }

    private static string Leaf(string path)
    {
        int separator = path.LastIndexOf('\\');
        string leaf = separator < 0 ? path : path[(separator + 1)..];
        return leaf.Length == 0 ? "album" : leaf;
    }
}
