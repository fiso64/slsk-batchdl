using Models;
using System.Text;

public class InteractiveModeManager
{
    private readonly DownloaderApplication app;
    private readonly TrackListEntry tle;
    private readonly Searcher searchService;
    private readonly List<(List<Track> List, int Index)> original;
    private List<(List<Track> List, int Index)> filterList;
    private readonly bool retrieveFolder;
    private readonly HashSet<string>? retrievedFolders;
    private string? filterStr;
    private int savedPos;

    public InteractiveModeManager(DownloaderApplication app, TrackListEntry tle, List<List<Track>> list, bool retrieveFolder, HashSet<string>? retrievedFolders, Searcher searchService, string? filterStr = null)
    {
        this.app = app;
        this.tle = tle;
        this.retrieveFolder = retrieveFolder;
        this.retrievedFolders = retrievedFolders;
        this.filterStr = filterStr;
        this.searchService = searchService;

        this.original = list.Select((x, i) => (List: x, Index: i)).ToList();
        this.filterList = this.original;

        if (filterStr != null)
        {
            filterList = original.Where(ls => ls.List.Any(track => track.FirstDownload.Filename.ContainsIgnoreCase(filterStr))).ToList();
            if (filterList.Count == 0)
            {
                Console.WriteLine($"No matches for query: {filterStr}");
                filterStr = null;
                filterList = original;
            }
        }
    }

    private string InteractiveModeInput()
    {
        var buffer = new StringBuilder();
        var firstKey = true;
        var cursorPos = 0;

        while (true)
        {
            var key = Console.ReadKey(true);

            // Handle special keys that should work at any time
            if (key.Key == ConsoleKey.DownArrow || key.Key == ConsoleKey.UpArrow || key.Key == ConsoleKey.Escape)
            {
                // Clear the current line if there's any content
                if (buffer.Length > 0)
                {
                    Console.SetCursorPosition(0, Console.CursorTop);
                    Console.Write(new string(' ', buffer.Length + 1));
                    Console.SetCursorPosition(0, Console.CursorTop);
                }

                if (key.Key == ConsoleKey.DownArrow)
                    return "n";
                else if (key.Key == ConsoleKey.UpArrow)
                    return "p";
                else
                    return "q";
            }

            // On first keypress, check for action keys
            if (firstKey && "pnyqrsh".Contains(key.KeyChar))
                return key.KeyChar.ToString();

            // Handle editing keys
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }
            else if (key.Key == ConsoleKey.LeftArrow && cursorPos > 0)
            {
                cursorPos--;
                Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
            }
            else if (key.Key == ConsoleKey.RightArrow && cursorPos < buffer.Length)
            {
                cursorPos++;
                Console.SetCursorPosition(Console.CursorLeft + 1, Console.CursorTop);
            }
            else if (key.Key == ConsoleKey.Backspace && cursorPos > 0)
            {
                buffer.Remove(cursorPos - 1, 1);
                cursorPos--;

                var restOfLine = buffer.ToString(cursorPos, buffer.Length - cursorPos);
                Console.Write("\b" + restOfLine + " ");
                Console.SetCursorPosition(Console.CursorLeft - (restOfLine.Length + 1), Console.CursorTop);
            }
            else if (key.Key == ConsoleKey.Delete && cursorPos < buffer.Length)
            {
                buffer.Remove(cursorPos, 1);

                var restOfLine = buffer.ToString(cursorPos, buffer.Length - cursorPos);
                Console.Write(restOfLine + " ");
                Console.SetCursorPosition(Console.CursorLeft - (restOfLine.Length + 1), Console.CursorTop);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Insert(cursorPos, key.KeyChar);
                cursorPos++;

                var restOfLine = buffer.ToString(cursorPos - 1, buffer.Length - (cursorPos - 1));
                Console.Write(restOfLine);
                Console.SetCursorPosition(Console.CursorLeft - (restOfLine.Length - 1), Console.CursorTop);
            }

            firstKey = false;
        }
    }

    private static void ClearCurrentLine()
    {
        int currentLineCursor = Console.CursorTop;
        Console.SetCursorPosition(0, Console.CursorTop);
        Console.Write(new string(' ', Console.BufferWidth));
        Console.SetCursorPosition(0, currentLineCursor);
    }

    private static void ClearOutput(int toPos)
    {
        if (Console.IsOutputRedirected) return;

        int pos = Console.CursorTop;

        while (pos > toPos && pos > 0)
        {
            //Console.Write("\x1b[1A"); // Move up one line
            //Console.Write("\x1b[2K"); // Clear entire line
            Console.SetCursorPosition(0, pos - 1);
            ClearCurrentLine();
            pos--;
        }

        Console.SetCursorPosition(0, toPos);
    }

    public async Task<(int index, List<Track> tracks, bool retrieveFolder, string? filterStr)> Run()
    {
        int aidx = 0;
        string retrieveAll1 = retrieveFolder ? "| [r]            " : "";
        string retrieveAll2 = retrieveFolder ? "| Load All Files " : "";
        Console.WriteLine();
        Printing.WriteLine($" [Up/p] | [Down/n] | [Enter] | {retrieveAll1}| [s]  | [Esc/q] | [h]", ConsoleColor.Green);
        Printing.WriteLine($" Prev   | Next     | Accept  | {retrieveAll2}| Skip | Quit    | Help", ConsoleColor.Green);

        Console.WriteLine();
        savedPos = Console.CursorTop;

        while (true)
        {
            var index = filterList[aidx].Index;
            var tracks = filterList[aidx].List;
            var response = tracks[0].FirstResponse;
            var username = tracks[0].FirstUsername;

            if (filterStr != null)
            {
                Printing.Write($"Filter: ", ConsoleColor.White);
                Printing.Write($"{filterStr}\n", ConsoleColor.Cyan);
                Console.WriteLine();
            }

            Printing.WriteLine($"[{aidx + 1} / {filterList.Count}]", ConsoleColor.DarkGray);

            Printing.PrintAlbum(tracks, indices: true);
            Console.WriteLine();

        Loop:
            string userInputStr = InteractiveModeInput().Trim().ToLower();

            string command = userInputStr;
            string options = "";
            string folder = "";

            var commandsWithArgs = new string[] { "d:", "f:", "cd" };

            foreach (var cmd in commandsWithArgs)
            {
                if (command.StartsWith(cmd))
                {
                    options = command[cmd.Length..].Trim();
                    command = command[..cmd.Length].TrimEnd(':');
                    break;
                }
            }

            switch (command)
            {
                case "p":
                    ClearOutput(savedPos);
                    aidx = (aidx + filterList.Count - 1) % filterList.Count;
                    break;
                case "n":
                    ClearOutput(savedPos);
                    aidx = (aidx + 1) % filterList.Count;
                    break;
                case "s":
                    return (-1, new List<Track>(), false, null);
                case "q":
                    return (-2, new List<Track>(), false, null);
                case "y":
                    Console.WriteLine("Exiting interactive mode");
                    tle.config.interactiveMode = false;
                    tle.config.UpdateProfiles(tle, app.trackLists);
                    tle.PrintLines();
                    return (index, tracks, true, filterStr);
                case "r":
                    if (!retrieveFolder)
                        goto Loop;
                    folder = Utils.GreatestCommonDirectorySlsk(tracks.Select(t => t.FirstDownload.Filename));
                    goto case "complete_folder";
                case "cd":
                    var currentFolder = Utils.GreatestCommonDirectorySlsk(tracks.Select(t => t.FirstDownload.Filename));
                    if (options == "..")
                    {
                        if (!retrieveFolder)
                            goto Loop;

                        var parentFolder = Utils.GetDirectoryNameSlsk(currentFolder);
                        if (string.IsNullOrEmpty(parentFolder))
                        {
                            Console.WriteLine("This is the top directory");
                            goto Loop;
                        }
                        folder = parentFolder;
                        goto case "complete_folder";

                    }
                    else
                    {
                        var subdir = currentFolder + '\\' + options;
                        var subdirTracks = tracks.Where(t => t.FirstDownload.Filename.StartsWith(subdir, StringComparison.OrdinalIgnoreCase));

                        if (!subdirTracks.Any())
                        {
                            Console.WriteLine("No such directory");
                            goto Loop;
                        }

                        folder = subdir;
                        filterList[aidx].List.RemoveAll(t => !t.FirstDownload.Filename.StartsWith(subdir, StringComparison.OrdinalIgnoreCase));

                        if (retrievedFolders != null && retrievedFolders.Contains(username + '\\' + currentFolder))
                        {
                            retrievedFolders.Remove(username + '\\' + currentFolder);
                            retrievedFolders.Add(username + '\\' + subdir);
                        }

                        break;
                    }
                case "complete_folder":
                    if (retrieveFolder)
                    {
                        if (!retrievedFolders.Contains(username + '\\' + folder))
                        {
                            (var wasCancelled, var newFiles) = await app.RetrieveFullFolderCancellableAsync(tracks, response, folder);

                            if (wasCancelled)
                            {
                                goto Loop;
                            }

                            retrievedFolders.Add(username + '\\' + folder);
                            if (newFiles == 0)
                            {
                                Console.WriteLine("No more files found.");
                                goto Loop;
                            }
                            else
                            {
                                Console.WriteLine($"Found {newFiles} more files in the folder:");
                                ClearOutput(savedPos);
                                break;
                            }
                        }
                        else
                        {
                            Console.WriteLine($"Already retrieved this folder.");
                            goto Loop;
                        }
                    }
                    goto Loop;
                case "d":
                    if (options.Length == 0)
                        return (index, tracks, true, filterStr);
                    try
                    {
                        var indices = options.Split(',')
                            .SelectMany(option =>
                            {
                                if (option.Contains('-'))
                                {
                                    var parts = option.Split(new char[] { '-' });
                                    int start = string.IsNullOrEmpty(parts[0]) ? 1 : int.Parse(parts[0]);
                                    int end = string.IsNullOrEmpty(parts[1]) ? tracks.Count : int.Parse(parts[1]);
                                    return Enumerable.Range(start, end - start + 1);
                                }
                                return new[] { int.Parse(option) };
                            })
                            .Distinct()
                            .ToArray();
                        return (index, indices.Select(i => tracks[i - 1]).ToList(), false, filterStr);
                    }
                    catch
                    {
                        Console.WriteLine("Error: Invalid range");
                        goto Loop;
                    }
                case "f":
                    aidx = 0;
                    if (string.IsNullOrWhiteSpace(options))
                    {
                        filterList = original;
                        filterStr = null;
                        ClearOutput(savedPos);
                    }
                    else
                    {
                        filterList = original.Where(ls => ls.List.Any(track => track.FirstDownload.Filename.ContainsIgnoreCase(options))).ToList();
                        if (filterList.Count == 0)
                        {
                            Console.WriteLine($"No matches for query: {options}");
                            filterStr = null;
                            filterList = original;
                            goto Loop;
                        }
                        else
                        {
                            filterStr = options;
                            ClearOutput(savedPos);
                        }
                    }
                    break;
                case "h":
                    Console.ForegroundColor = ConsoleColor.Green;
                    Help.PrintHelp("shortcuts");
                    Console.ResetColor();
                    goto Loop;
                case "":
                    return (index, tracks, true, filterStr);
                default:
                    Console.WriteLine($"Error: Invalid input {userInputStr}");
                    goto Loop;
            }
        }
    }
}
