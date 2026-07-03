using System.Formats.Tar;
using System.IO.Compression;

const UnixFileMode DirectoryMode =
    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
    UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

const UnixFileMode RegularFileMode =
    UnixFileMode.UserRead | UnixFileMode.UserWrite |
    UnixFileMode.GroupRead |
    UnixFileMode.OtherRead;

const UnixFileMode ExecutableFileMode =
    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
    UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

if (args.Length != 4 || args[0] != "tar-gz")
{
    await Console.Error.WriteLineAsync("Usage: Sockseek.Packager tar-gz <source-dir> <destination.tar.gz> <executable-name>");
    return 2;
}

var sourceDir = Path.GetFullPath(args[1]);
var destination = Path.GetFullPath(args[2]);
var executableName = args[3];

if (!Directory.Exists(sourceDir))
{
    await Console.Error.WriteLineAsync($"Source directory does not exist: {sourceDir}");
    return 2;
}

var destinationDir = Path.GetDirectoryName(destination);
if (!string.IsNullOrEmpty(destinationDir))
    Directory.CreateDirectory(destinationDir);

if (File.Exists(destination))
    File.Delete(destination);

await using var output = File.Create(destination);
await using var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: false);
await using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);

foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
{
    var entryName = ToEntryName(sourceDir, directory) + "/";
    var entry = new PaxTarEntry(TarEntryType.Directory, entryName)
    {
        Mode = DirectoryMode,
        ModificationTime = Directory.GetLastWriteTimeUtc(directory),
    };
    await writer.WriteEntryAsync(entry);
}

foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
{
    var entryName = ToEntryName(sourceDir, file);
    await using var data = File.OpenRead(file);
    var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
    {
        Mode = string.Equals(entryName, executableName, StringComparison.Ordinal)
            ? ExecutableFileMode
            : RegularFileMode,
        ModificationTime = File.GetLastWriteTimeUtc(file),
        DataStream = data,
    };
    await writer.WriteEntryAsync(entry);
}

return 0;

static string ToEntryName(string sourceDir, string path)
    => Path.GetRelativePath(sourceDir, path).Replace(Path.DirectorySeparatorChar, '/');
