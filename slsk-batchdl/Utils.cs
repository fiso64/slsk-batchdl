using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Enums;

public static class Utils
{
    public static readonly string[] musicExtensions = new string[] { ".mp3", ".flac", ".ogg", ".m4a", ".opus", ".wav", ".aac", ".alac" };
    public static readonly string[] imageExtensions = new string[] { ".jpg", ".png", ".jpeg" };

    public static bool IsMusicExtension(string extension)
    {
        return musicExtensions.Contains(extension.ToLower());
    }

    public static bool IsMusicFile(string fileName)
    {
        return musicExtensions.Contains(Path.GetExtension(fileName).ToLower());
    }

    public static bool IsImageExtension(string extension)
    {
        return imageExtensions.Contains(extension.ToLower());
    }

    public static bool IsImageFile(string fileName)
    {
        return imageExtensions.Contains(Path.GetExtension(fileName).ToLower());
    }

    public static bool IsInternetUrl(this string str)
    {
        str = str.TrimStart();
        return str.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || str.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    public static void WriteAllLines(string path, IEnumerable<string> lines, char separator)
    {
        using (var writer = new StreamWriter(path))
        {
            foreach (var line in lines)
            {
                writer.Write(line);
                writer.Write(separator);
            }
        }
    }

    public static string GetAsPathSlsk(string fname)
    {
        return fname.Replace('\\', Path.DirectorySeparatorChar);
    }

    public static string GetFileNameSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFileName(fname);
    }

    public static string GetBaseNameSlsk(string path)
    {
        path = path.Replace('\\', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        return Path.GetFileName(path);
    }

    public static string GetFileNameWithoutExtSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFileNameWithoutExtension(fname);
    }

    public static string GetExtensionSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetExtension(fname);
    }

    public static string GetDirectoryNameSlsk(string fname)
    {
        fname = fname.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetDirectoryName(fname);
    }

    public static string ExpandUser(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        path = path.Trim();

        if (path[0] == '~' && (path.Length == 1 || path[1] == '\\' || path[1] == '/'))
        {
            string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Join(homeDirectory, path.Substring(1).TrimStart('/').TrimStart('\\'));
            
            if (path.Length > 0)
                path = Path.GetFullPath(path);
        }

        return path;
    }

    public static List<string> FromCsv(string csvLine)
    {
        var items = new List<string>();
        var currentItem = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < csvLine.Length; i++)
        {
            char c = csvLine[i];

            if (c == '"' && (i == 0 || csvLine[i - 1] != '\\'))
            {
                if (inQuotes && i + 1 < csvLine.Length && csvLine[i + 1] == '"')
                {
                    currentItem.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                items.Add(currentItem.ToString());
                currentItem.Clear();
            }
            else
            {
                currentItem.Append(c);
            }
        }
        items.Add(currentItem.ToString());

        return items;
    }

    public static decimal Normalize(this double value)
    {
        return ((decimal)value) / 1.000000000000000000000000000000000m;
    }

    public static int FileCountRecursive(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        int count = Directory.GetFiles(directory).Length;
        foreach (string subDirectory in Directory.GetDirectories(directory))
            count += FileCountRecursive(subDirectory);

        return count;
    }

    public static void Move(string sourceFilePath, string destinationFilePath)
    {
        if (File.Exists(sourceFilePath))
        {
            if (File.Exists(destinationFilePath))
                File.Delete(destinationFilePath);
            File.Move(sourceFilePath, destinationFilePath);
        }
    }

    public static void DeleteAncestorsIfEmpty(string startDir, string root)
    {
        string x = NormalizedPath(Path.GetFullPath(root));
        string y = NormalizedPath(startDir);

        if (x.Length == 0)
            return;

        while (y.StartsWith(x + '/') && FileCountRecursive(y) == 0)
        {
            Directory.Delete(y, true);
            
            string prev = y;
            y = NormalizedPath(Path.GetDirectoryName(y) ?? "");
            
            if (prev.Length == y.Length)
                break;
        }
    }

    public static bool EqualsAny(this string input, string[] values, StringComparison comparison = StringComparison.Ordinal)
    {
        foreach (var value in values)
        {
            if (input.Equals(value, comparison))
                return true;
        }
        return false;
    }

    public static string Replace(this string s, string[] separators, string newVal)
    {
        if (s.Length == 0)
            return s;

        foreach (var sep in separators)
            s = s.Replace(sep, newVal);

        return s;
    }

    public static string UnHtmlString(this string s)
    {
        s = WebUtility.HtmlDecode(s);
        string[] zeroWidthChars = { "\u200B", "\u200C", "\u200D", "\u00AD", "\u200E", "\u200F" };
        foreach (var zwChar in zeroWidthChars)
            s = s.Replace(zwChar, "");

        s = s.Replace('\u00A0', ' ');
        return s;
    }

    public static string ReplaceInvalidChars(this string str, string replaceStr, bool windows = false, bool removeSlash = true)
    {
        if (str.Length == 0)
            return str;

        char[] invalidChars;

        if (windows)
            invalidChars = new char[] { ':', '|', '?', '>', '<', '*', '"' }; // forward- and backslash are always included
        else
            invalidChars = Path.GetInvalidFileNameChars();

        if (removeSlash)
        {
            str = str.Replace("/", replaceStr);
            str = str.Replace("\\", replaceStr);
        }

        foreach (var c in invalidChars)
        {
            if (!removeSlash && (c == '/' || c == '\\'))
                continue;
            str = str.Replace(c.ToString(), replaceStr);
        }

        return str;
    }

    public static string ReplaceInvalidChars(this string str, char replaceChar, bool windows = false, bool removeSlash = true)
    {
        if (str.Length == 0)
            return str;

        char[] invalidChars;

        if (windows)
            invalidChars = new char[] { ':', '|', '?', '>', '<', '*', '"' }; // forward- and backslash are always included
        else
            invalidChars = Path.GetInvalidFileNameChars();

        if (removeSlash)
        {
            str = str.Replace('/', replaceChar);
            str = str.Replace('\\', replaceChar);
        }

        foreach (var c in invalidChars)
        {
            if (!removeSlash && (c == '/' || c == '\\'))
                continue;
            str = str.Replace(c, replaceChar);
        }

        return str;
    }

    public static string ReplaceSpecialChars(this string str, string replaceStr)
    {
        if (str.Length == 0)
            return str;

        string special = ";:'\"|?!<>*/\\[]{}()-–—&%^$#@+=`~_";
        foreach (char c in special)
            str = str.Replace(c.ToString(), replaceStr);
        return str;
    }

    public static string RemoveFt(this string str, bool removeParentheses = true)
    {
        if (str.Length == 0)
            return str;

        var ftStrings = new string[] { "feat.", "ft." };
        var open = new char[] { '(', '[' };
        var close = new char[] { ')', ']' };
        bool changed = false;

        foreach (string ftStr in ftStrings)
        {
            int ftIndex = str.IndexOf(ftStr, StringComparison.OrdinalIgnoreCase);

            if (ftIndex != -1)
            {
                changed = true;
                if (removeParentheses && ftIndex > 0)
                {
                    bool any = false;

                    for (int i = 0; i < 2; i++)
                    {
                        if (str[ftIndex - 1] == open[i])
                        {
                            int openIdx = ftIndex - 1;
                            int closeIdx = str.IndexOf(close[i], ftIndex);
                            if (closeIdx != -1)
                            {
                                int add = 0;
                                if (openIdx > 0 && closeIdx < str.Length - 1 && str[openIdx - 1] == ' ' && str[closeIdx + 1] == ' ')
                                    add = 1;
                                str = str.Remove(openIdx, closeIdx - openIdx + 1 + add);
                                any = true;
                                break;
                            }
                        }
                    }

                    if (!any)
                    {
                        str = str[..ftIndex];
                    }
                }
                else
                {
                    str = str[..ftIndex];
                }
                break;
            }
        }
        return changed ? str.TrimEnd() : str;
    }

    public static string RemoveConsecutiveWs(this string input)
    {
        if (input.Length == 0)
            return string.Empty;

        int index = 0;
        var src = input.ToCharArray();
        bool skip = false;
        char ch;
        for (int i = 0; i < input.Length; i++)
        {
            ch = src[i];
            if (ch == ' ')
            {
                if (skip) continue;
                src[index++] = ch;
                skip = true;
            }
            else
            {
                skip = false;
                src[index++] = ch;
            }
        }

        return new string(src, 0, index);
    }

    public static string RemoveSquareBrackets(this string str)
    {
        if (str.Length == 0)
            return str;
        if (!str.Contains('['))
            return str;
        return Regex.Replace(str, @"\[[^\]]*\]", "").Trim();
    }

    public static bool ContainsIgnoreCase(this string s, string other)
    {
        return s.Contains(other, StringComparison.OrdinalIgnoreCase);
    }

    static readonly HashSet<char> boundarySet = new("-|.\\/_—()[],:?!;@#:*=+{}|'\"$^&`~%<>".ToCharArray());

    public static bool ContainsWithBoundary(this string str, string value, bool ignoreCase = false)
    {
        if (value.Length == 0)
            return true;
        if (str.Length == 0)
            return false;

        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        int index = 0;
        while ((index = str.IndexOf(value, index, comp)) != -1)
        {
            bool hasLeftBoundary = index == 0 || str[index - 1] == ' ' || boundarySet.Contains(str[index - 1]);
            bool hasRightBoundary = index + value.Length >= str.Length || str[index + value.Length] == ' ' || boundarySet.Contains(str[index + value.Length]);

            if (hasLeftBoundary && hasRightBoundary)
                return true;

            index += value.Length;
        }

        return false;
    }

    public static bool ContainsWithBoundaryIgnoreWs(this string str, string value, bool ignoreCase = false, bool acceptLeftDigit = false)
    {
        if (value.Length == 0)
            return true;
        if (str.Length == 0)
            return false;

        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        int index = 0;
        while ((index = str.IndexOf(value, index, comp)) != -1)
        {
            int leftIndex = index - 1;
            while (leftIndex >= 0 && str[leftIndex] == ' ')
                leftIndex--;

            bool hasLeftBoundary = leftIndex <  0 || acceptLeftDigit && leftIndex < index - 1 && char.IsDigit(str[leftIndex]) || boundarySet.Contains(str[leftIndex]);

            int rightIndex = index + value.Length;
            while (rightIndex < str.Length && str[rightIndex] == ' ')
                rightIndex++;

            bool hasRightBoundary = rightIndex >= str.Length || boundarySet.Contains(str[rightIndex]);

            if (hasLeftBoundary && hasRightBoundary)
                return true;

            index = rightIndex;
        }

        return false;
    }

    public static bool ContainsInBrackets(this string str, string searchTerm, bool ignoreCase = false)
    {
        if (str.Length == 0 && searchTerm.Length > 0)
            return false;

        var regex = new Regex(@"\[(.*?)\]|\((.*?)\)");
        var matches = regex.Matches(str);
        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (Match match in matches)
        {
            if (match.Value.Contains(searchTerm, comp))
                return true;
        }

        return false;
    }

    public static bool ContainsInBracketsOptimized(this string str, string searchTerm, bool ignoreCase = false)
    {
        if (str.Length == 0 && searchTerm.Length > 0)
            return false;

        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int depth = 0;
        int searchTermLen = searchTerm.Length;

        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];

            if (c == '[' || c == '(')
            {
                depth++;
            }
            else if (c == ']' || c == ')')
            {
                depth--;
            }

            if (depth > 0 && i + searchTermLen <= str.Length)
            {
                if (str.Substring(i, searchTermLen).Equals(searchTerm, comp))
                    return true;
            }
        }

        return false;
    }

    public static bool RemoveRegexIfExist(this string s, string reg, out string res)
    {
        res = Regex.Replace(s, reg, string.Empty);
        return res != s;
    }

    public static Dictionary<K, V> ToSafeDictionary<T, K, V>(this IEnumerable<T> source, Func<T, K> keySelector, Func<T, V> valSelector)
    {
        var d = new Dictionary<K, V>();
        foreach (var element in source)
        {
            if (!d.ContainsKey(keySelector(element)))
                d.Add(keySelector(element), valSelector(element));
        }
        return d;
    }

    public static int Levenshtein(string source, string target)
    {
        if (source.Length == 0)
            return target.Length;
        if (target.Length == 0)
            return source.Length;

        var distance = new int[source.Length + 1, target.Length + 1];

        for (var i = 0; i <= source.Length; i++)
            distance[i, 0] = i;

        for (var j = 0; j <= target.Length; j++)
            distance[0, j] = j;

        for (var i = 1; i <= source.Length; i++)
        {
            for (var j = 1; j <= target.Length; j++)
            {
                var cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                distance[i, j] = Math.Min(
                    Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + cost);
            }
        }

        return distance[source.Length, target.Length];
    }

    public static string GreatestCommonPath(IEnumerable<string> paths)
    {
        string? path = paths.FirstOrDefault();

        if (path == null || path.Length == 0)
            return "";

        static int commonPathIndex(string path1, string path2, int maxIndex)
        {
            var minLength = Math.Min(path1.Length, Math.Min(path2.Length, maxIndex));
            var commonPathLength = 0;
            for (int i = 0; i < minLength; i++)
            {
                if ((path1[i] == '/' || path1[i] == '\\') && (path2[i] == '/' || path2[i] == '\\'))
                    commonPathLength = i + 1;
                else if (path1[i] != path2[i])
                    break;
            }
            return commonPathLength;
        }

        int index = path.Length;

        foreach (var p in paths.Skip(1))
            index = commonPathIndex(path, p, index);

        return path[..index];
    }

    public static string GreatestCommonDirectory(IEnumerable<string> paths)
    {
        if (paths.Skip(1).Any())
            return NormalizedPath(GreatestCommonPath(paths));
        else
            return NormalizedPath(Path.GetDirectoryName(paths.First().TrimEnd('/').TrimEnd('\\')) ?? "");
    }

    public static string GreatestCommonDirectorySlsk(IEnumerable<string> paths)
    {
        if (paths.Skip(1).Any())
            return Utils.GreatestCommonPath(paths).Replace('/', '\\').TrimEnd('\\');
        else
            return Utils.GetDirectoryNameSlsk(paths.First()).Replace('/', '\\').TrimEnd('\\');
    }

    public static string NormalizedPath(string path)
    {
        return path.Replace('\\', '/').TrimEnd('/').Trim();
    }

    public static bool SequenceEqualUpToPermutation<T>(this IEnumerable<T> list1, IEnumerable<T> list2)
    {
        var cnt = new Dictionary<T, int>();
        foreach (T s in list1)
        {
            if (cnt.ContainsKey(s))
                cnt[s]++;
            else
                cnt.Add(s, 1);
        }
        foreach (T s in list2)
        {
            if (cnt.ContainsKey(s))
                cnt[s]--;
            else
                return false;
        }
        return cnt.Values.All(c => c == 0);
    }

    public static bool RemoveDiacriticsIfExist(this string s, out string res)
    {
        res = s.RemoveDiacritics();
        return res != s;
    }

    public static char RemoveDiacritics(this char c)
    {
        if (diacriticChars.TryGetValue(c, out var res)) return res;
        return c;
    }

    public static string RemoveDiacritics(this string s)
    {
        if (s.Length == 0)
            return s;

        var textBuilder = new StringBuilder();
        foreach (char c in s)
        {
            if (diacriticChars.TryGetValue(c, out char o))
                textBuilder.Append(o);
            else
                textBuilder.Append(c);
        }
        return textBuilder.ToString();
    }

    static readonly Dictionary<char, char> diacriticChars = new()
    {
        { 'ä', 'a' }, { 'æ', 'a' }, { 'ǽ', 'a' }, { 'œ', 'o' }, { 'ö', 'o' }, { 'ü', 'u' },
        { 'Ä', 'A' }, { 'Ü', 'U' }, { 'Ö', 'O' }, { 'À', 'A' }, { 'Á', 'A' }, { 'Â', 'A' },
        { 'Ã', 'A' }, { 'Å', 'A' }, { 'Ǻ', 'A' }, { 'Ā', 'A' }, { 'Ă', 'A' }, { 'Ą', 'A' },
        { 'Ǎ', 'A' }, { 'Ά', 'A' }, { 'Ả', 'A' }, { 'Ạ', 'A' }, { 'Ầ', 'A' }, { 'Ấ', 'A' },
        { 'Ẫ', 'A' }, { 'Ẩ', 'A' }, { 'Ậ', 'A' }, { 'à', 'a' }, { 'á', 'a' }, { 'â', 'a' },
        { 'ã', 'a' }, { 'å', 'a' }, { 'ǻ', 'a' }, { 'ā', 'a' }, { 'ă', 'a' }, { 'ą', 'a' },
        { 'ǎ', 'a' }, { 'ả', 'a' }, { 'ạ', 'a' }, { 'Ç', 'C' }, { 'Ć', 'C' }, { 'Ĉ', 'C' },
        { 'Ċ', 'C' }, { 'Č', 'C' }, { 'ç', 'c' }, { 'ć', 'c' }, { 'ĉ', 'c' }, { 'ċ', 'c' },
        { 'č', 'c' }, { 'Ð', 'D' }, { 'Ď', 'D' }, { 'Đ', 'D' }, { 'ð', 'd' }, { 'ď', 'd' },
        { 'đ', 'd' }, { 'È', 'E' }, { 'É', 'E' }, { 'Ê', 'E' }, { 'Ë', 'E' }, { 'Ē', 'E' },
        { 'Ĕ', 'E' }, { 'Ė', 'E' }, { 'Ę', 'E' }, { 'Ě', 'E' }, { 'Έ', 'E' }, { 'Ẽ', 'E' },
        { 'Ẻ', 'E' }, { 'Ẹ', 'E' }, { 'Ề', 'E' }, { 'Ế', 'E' }, { 'Ễ', 'E' }, { 'Ể', 'E' },
        { 'Ệ', 'E' }, { 'è', 'e' }, { 'é', 'e' }, { 'ê', 'e' }, { 'ë', 'e' }, { 'ē', 'e' },
        { 'ĕ', 'e' }, { 'ė', 'e' }, { 'ę', 'e' }, { 'ě', 'e' }, { 'ẽ', 'e' }, { 'ẻ', 'e' },
        { 'ẹ', 'e' }, { 'Ĝ', 'G' }, { 'Ğ', 'G' }, { 'Ġ', 'G' }, { 'Ģ', 'G' }, { 'ĝ', 'g' },
        { 'ğ', 'g' }, { 'ġ', 'g' }, { 'ģ', 'g' }, { 'Ĥ', 'H' }, { 'Ħ', 'H' }, { 'Ή', 'H' },
        { 'ĥ', 'h' }, { 'ħ', 'h' }, { 'Ì', 'I' }, { 'Í', 'I' }, { 'Î', 'I' }, { 'Ï', 'I' },
        { 'Ĩ', 'I' }, { 'Ī', 'I' }, { 'Ĭ', 'I' }, { 'Ǐ', 'I' }, { 'Į', 'I' }, { 'İ', 'I' },
        { 'Ί', 'I' }, { 'Ϊ', 'I' }, { 'Ỉ', 'I' }, { 'Ị', 'I' }, { 'Ї', 'I' }, { 'ì', 'i' },
        { 'í', 'i' }, { 'î', 'i' }, { 'ï', 'i' }, { 'ĩ', 'i' }, { 'ī', 'i' }, { 'ĭ', 'i' },
        { 'ǐ', 'i' }, { 'į', 'i' }, { 'ı', 'i' }, { 'ΰ', 'y' }, { 'Ĵ', 'J' }, { 'ĵ', 'j' },
        { 'Ķ', 'K' }, { 'ķ', 'k' }, { 'Ĺ', 'L' }, { 'Ļ', 'L' }, { 'Ľ', 'L' }, { 'Ŀ', 'L' },
        { 'Ł', 'L' }, { 'ĺ', 'l' }, { 'ļ', 'l' }, { 'ľ', 'l' }, { 'ŀ', 'l' }, { 'ł', 'l' },
        { 'Ñ', 'N' }, { 'Ń', 'N' }, { 'Ņ', 'N' }, { 'Ň', 'N' }, { 'ñ', 'n' }, { 'ń', 'n' },
        { 'ņ', 'n' }, { 'ň', 'n' }, { 'ŉ', 'n' }, { 'Ò', 'O' }, { 'Ó', 'O' }, { 'Ô', 'O' },
        { 'Õ', 'O' }, { 'Ō', 'O' }, { 'Ŏ', 'O' }, { 'Ǒ', 'O' }, { 'Ő', 'O' }, { 'Ơ', 'O' },
        { 'Ø', 'O' }, { 'Ǿ', 'O' }, { 'Ό', 'O' }, { 'Ỏ', 'O' }, { 'Ọ', 'O' }, { 'Ồ', 'O' },
        { 'Ố', 'O' }, { 'Ỗ', 'O' }, { 'Ổ', 'O' }, { 'Ộ', 'O' }, { 'Ờ', 'O' }, { 'Ớ', 'O' },
        { 'Ỡ', 'O' }, { 'Ở', 'O' }, { 'Ợ', 'O' }, { 'ò', 'o' }, { 'ó', 'o' }, { 'ô', 'o' },
        { 'õ', 'o' }, { 'ō', 'o' }, { 'ŏ', 'o' }, { 'ǒ', 'o' }, { 'ő', 'o' }, { 'ơ', 'o' },
        { 'ø', 'o' }, { 'ǿ', 'o' }, { 'º', 'o' }, { 'ό', 'o' }, { 'ỏ', 'o' }, { 'ọ', 'o' },
        { 'ồ', 'o' }, { 'ố', 'o' }, { 'ỗ', 'o' }, { 'ổ', 'o' }, { 'ộ', 'o' }, { 'ờ', 'o' },
        { 'ớ', 'o' }, { 'ỡ', 'o' }, { 'ở', 'o' }, { 'ợ', 'o' }, { 'Ŕ', 'R' }, { 'Ŗ', 'R' },
        { 'Ř', 'R' }, { 'ŕ', 'r' }, { 'ŗ', 'r' }, { 'ř', 'r' }, { 'Ś', 'S' }, { 'Ŝ', 'S' },
        { 'Ş', 'S' }, { 'Ș', 'S' }, { 'Š', 'S' }, { 'ś', 's' }, { 'ŝ', 's' }, { 'ş', 's' },
        { 'ș', 's' }, { 'š', 's' }, { 'Ț', 'T' }, { 'Ţ', 'T' }, { 'Ť', 'T' }, { 'Ŧ', 'T' },
        { 'Т', 'T' }, { 'ț', 't' }, { 'ţ', 't' }, { 'ť', 't' }, { 'ŧ', 't' }, { 'Ù', 'U' },
        { 'Ú', 'U' }, { 'Û', 'U' }, { 'Ũ', 'U' }, { 'Ū', 'U' }, { 'Ŭ', 'U' }, { 'Ů', 'U' },
        { 'Ű', 'U' }, { 'Ų', 'U' }, { 'Ư', 'U' }, { 'Ǔ', 'U' }, { 'Ǖ', 'U' }, { 'Ǘ', 'U' },
        { 'Ǚ', 'U' }, { 'Ǜ', 'U' }, { 'Ủ', 'U' }, { 'Ụ', 'U' }, { 'Ừ', 'U' }, { 'ё', 'e' },
        { 'Ứ', 'U' }, { 'Ữ', 'U' }, { 'Ử', 'U' }, { 'Ự', 'U' }, { 'ù', 'u' }, { 'ú', 'u' },
        { 'û', 'u' }, { 'ũ', 'u' }, { 'ū', 'u' }, { 'ŭ', 'u' }, { 'ů', 'u' }, { 'ű', 'u' },
        { 'ų', 'u' }, { 'ư', 'u' }, { 'ǔ', 'u' }, { 'ǖ', 'u' }, { 'ǘ', 'u' }, { 'ǚ', 'u' },
        { 'ǜ', 'u' }, { 'ủ', 'u' }, { 'ụ', 'u' }, { 'ừ', 'u' }, { 'ứ', 'u' }, { 'ữ', 'u' },
        { 'ử', 'u' }, { 'ự', 'u' }, { 'Ý', 'Y' }, { 'Ÿ', 'Y' }, { 'Ŷ', 'Y' }, { 'Ύ', 'Y' },
        { 'Ϋ', 'Y' }, { 'Ỳ', 'Y' }, { 'Ỹ', 'Y' }, { 'Ỷ', 'Y' }, { 'Ỵ', 'Y' }, { 'й', 'и' },
        { 'ý', 'y' }, { 'ÿ', 'y' }, { 'ŷ', 'y' }, { 'ỳ', 'y' }, { 'ỹ', 'y' }, { 'ỷ', 'y' },
        { 'ỵ', 'y' }, { 'Ŵ', 'W' }, { 'ŵ', 'w' }, { 'Ź', 'Z' }, { 'Ż', 'Z' }, { 'Ž', 'Z' },
        { 'ź', 'z' }, { 'ż', 'z' }, { 'ž', 'z' }, { 'Æ', 'A' }, { 'ß', 's' }, { 'Œ', 'O' },
        { 'Ё', 'E' },
    };
}


class RateLimitedSemaphore
{
    private readonly int maxCount;
    private readonly TimeSpan resetTimeSpan;
    private readonly SemaphoreSlim semaphore;
    private long nextResetTimeTicks;
    private readonly object resetTimeLock = new object();

    public RateLimitedSemaphore(int maxCount, TimeSpan resetTimeSpan)
    {
        this.maxCount = maxCount;
        this.resetTimeSpan = resetTimeSpan;
        this.semaphore = new SemaphoreSlim(maxCount, maxCount);
        this.nextResetTimeTicks = (DateTimeOffset.UtcNow + this.resetTimeSpan).UtcTicks;
    }

    private void TryResetSemaphore()
    {
        if (!(DateTimeOffset.UtcNow.UtcTicks > Interlocked.Read(ref this.nextResetTimeTicks)))
            return;

        lock (this.resetTimeLock)
        {
            var currentTime = DateTimeOffset.UtcNow;
            if (currentTime.UtcTicks > Interlocked.Read(ref this.nextResetTimeTicks))
            {
                int releaseCount = this.maxCount - this.semaphore.CurrentCount;
                if (releaseCount > 0)
                    this.semaphore.Release(releaseCount);

                var newResetTimeTicks = (currentTime + this.resetTimeSpan).UtcTicks;
                Interlocked.Exchange(ref this.nextResetTimeTicks, newResetTimeTicks);
            }
        }
    }

    public async Task WaitAsync()
    {
        TryResetSemaphore();
        var semaphoreTask = this.semaphore.WaitAsync();

        while (!semaphoreTask.IsCompleted)
        {
            var ticks = Interlocked.Read(ref this.nextResetTimeTicks);
            var nextResetTime = new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
            var delayTime = nextResetTime - DateTimeOffset.UtcNow;
            var delayTask = delayTime >= TimeSpan.Zero ? Task.Delay(delayTime) : Task.CompletedTask;

            await Task.WhenAny(semaphoreTask, delayTask);
            TryResetSemaphore();
        }
    }
}

public static class Logger
{
    public static Verbosity verbosity { get; set; } = Verbosity.Normal;

    public static void Log(string message, Verbosity messageLevel)
    {
        if (messageLevel <= verbosity)
        {
            Console.WriteLine(message);
        }
    }

    public static void Error(string message)
    {
        Log(message, Verbosity.Error);
    }

    public static void Warning(string message)
    {
        Log(message, Verbosity.Warning);
    }

    public static void Info(string message)
    {
        Log(message, Verbosity.Normal);
    }

    public static void Verbose(string message)
    {
        Log(message, Verbosity.Verbose);
    }
}
