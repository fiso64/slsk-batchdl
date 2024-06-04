using System.Net;
using System.Text.RegularExpressions;

public static class Utils
{
    public static string[] musicExtensions = new string[] { ".mp3", ".wav", ".flac", ".ogg", ".aac", ".wma", ".m4a", ".alac", ".ape", ".opus" };
    public static string[] imageExtensions = new string[] { ".jpg", ".jpeg", ".png" };
    
    public static bool IsMusicExtension(string extension)
    {
        return musicExtensions.Contains(('.' + extension.TrimStart('.')).ToLower());
    }

    public static bool IsMusicFile(string fileName)
    {
        return musicExtensions.Contains(Path.GetExtension(fileName).ToLower());
    }

    public static bool IsImageFile(string fileName)
    {
        return imageExtensions.Contains(Path.GetExtension(fileName).ToLower());
    }

    public static decimal Normalize(this double value)
    {
        return ((decimal)value) / 1.000000000000000000000000000000000m;
    }

    public static int GetRecursiveFileCount(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        int count = Directory.GetFiles(directory).Length;
        foreach (string subDirectory in Directory.GetDirectories(directory))
            count += GetRecursiveFileCount(subDirectory);

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
        string[] temp;
        temp = s.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        return String.Join(newVal, temp);
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

    public static string ReplaceInvalidChars(this string str, string replaceStr, bool windows = false, bool removeSlash = true, bool alwaysRemoveSlash = false)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        if (windows)
            invalidChars = new char[] { ':', '|', '?', '>', '<', '*', '"', '/', '\\' };
        if (!removeSlash && !alwaysRemoveSlash)
            invalidChars = invalidChars.Where(c => c != '/' && c != '\\').ToArray();
        if (alwaysRemoveSlash)
        {
            var x = invalidChars.ToList();
            x.AddRange(new char[] { '/', '\\' });
            invalidChars = x.ToArray();
        }
        foreach (char c in invalidChars)
            str = str.Replace(c.ToString(), replaceStr);
        return str;
    }

    public static string ReplaceSpecialChars(this string str, string replaceStr)
    {
        string special = ";:'\"|?!<>*/\\[]{}()-–—&%^$#@+=`~_";
        foreach (char c in special)
            str = str.Replace(c.ToString(), replaceStr);
        return str;
    }

    public static string RemoveFt(this string str, bool removeParentheses = true, bool onlyIfNonempty = true)
    {
        string[] ftStrings = { "feat.", "ft." };
        string orig = str;
        foreach (string ftStr in ftStrings)
        {
            int ftIndex = str.IndexOf(ftStr, StringComparison.OrdinalIgnoreCase);

            if (ftIndex != -1)
            {
                if (removeParentheses)
                {
                    int openingParenthesesIndex = str.LastIndexOf('(', ftIndex);
                    int closingParenthesesIndex = str.IndexOf(')', ftIndex);
                    int openingBracketIndex = str.LastIndexOf('[', ftIndex);
                    int closingBracketIndex = str.IndexOf(']', ftIndex);

                    if (openingParenthesesIndex != -1 && closingParenthesesIndex != -1)
                        str = str.Remove(openingParenthesesIndex, closingParenthesesIndex - openingParenthesesIndex + 1);
                    else if (openingBracketIndex != -1 && closingBracketIndex != -1)
                        str = str.Remove(openingBracketIndex, closingBracketIndex - openingBracketIndex + 1);
                    else
                        str = str.Substring(0, ftIndex);
                }
                else
                    str = str.Substring(0, ftIndex);
            }
        }
        if (onlyIfNonempty)
            str = str.TrimEnd() == "" ? orig : str;
        return str.TrimEnd();
    }

    public static string RemoveConsecutiveWs(this string input)
    {
        return string.Join(' ', input.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
    }

    public static string RemoveSquareBrackets(this string str)
    {
        return Regex.Replace(str, @"\[[^\]]*\]", "").Trim();
    }

    public static bool RemoveDiacriticsIfExist(this string s, out string res)
    {
        res = s.RemoveDiacritics();
        return res != s;
    }

    public static bool ContainsIgnoreCase(this string s, string other)
    {
        return s.Contains(other, StringComparison.OrdinalIgnoreCase);
    }

    static char[] boundaryChars = { '-', '|', '.', '\\', '/', '_', '—', '(', ')', '[', ']', ',', '?', '!', ';',
            '@', ':', '*', '=', '+', '{', '}', '|', '\'', '"', '$', '^', '&', '#', '`', '~', '%', '<', '>' };
    static string boundaryPattern = "^|$|" + string.Join("|", boundaryChars.Select(c => Regex.Escape(c.ToString())));

    public static bool ContainsWithBoundary(this string str, string value, bool ignoreCase = false)
    {
        if (value == "")
            return true;
        if (str == "")
            return false;
        string bound = boundaryPattern + "|\\s";
        string pattern = $@"({bound}){Regex.Escape(value)}({bound})";
        RegexOptions options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        return Regex.IsMatch(str, pattern, options);
    }

    public static bool ContainsWithBoundaryIgnoreWs(this string str, string value, bool ignoreCase = false, bool acceptLeftDigit = false)
    {
        if (value == "")
            return true;
        if (str == "")
            return false;
        string patternLeft = acceptLeftDigit ? boundaryPattern + @"|\d\s+" : boundaryPattern;
        string pattern = $@"({patternLeft})\s*{Regex.Escape(value)}\s*({boundaryPattern})";
        RegexOptions options = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        return Regex.IsMatch(str, pattern, options);
    }


    public static bool ContainsInBrackets(this string str, string searchTerm, bool ignoreCase = false)
    {
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

    public static bool RemoveRegexIfExist(this string s, string reg, out string res)
    {
        res = Regex.Replace(s, reg, string.Empty);
        return res != s;
    }

    public static char RemoveDiacritics(this char c)
    {
        foreach (var entry in diacriticChars)
        {
            if (entry.Key.IndexOf(c) != -1)
                return entry.Value[0];
        }
        return c;
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

    public static string GreatestCommonPath(IEnumerable<string> paths, char dirsep = '-')
    {
        var commonPath = paths.FirstOrDefault();
        if (string.IsNullOrEmpty(commonPath))
            return "";
        foreach (var path in paths.Skip(1))
        {
            commonPath = GetCommonPath(commonPath, path, dirsep);
        }
        return commonPath;
    }

    private static string GetCommonPath(string path1, string path2, char dirsep = '-')
    {
        if (dirsep == '-')
            dirsep = Path.DirectorySeparatorChar;
        var minLength = Math.Min(path1.Length, path2.Length);
        var commonPathLength = 0;
        for (int i = 0; i < minLength; i++)
        {
            if (path1[i] != path2[i])
                break;
            if (path1[i] == dirsep)
                commonPathLength = i + 1;
        }
        return path1.Substring(0, commonPathLength);
    }

    public static string RemoveDiacritics(this string s)
    {
        string text = "";
        foreach (char c in s)
        {
            int len = text.Length;

            foreach (var entry in diacriticChars)
            {
                if (entry.Key.IndexOf(c) != -1)
                {
                    text += entry.Value;
                    break;
                }
            }

            if (len == text.Length)
                text += c;
        }
        return text;
    }

    static Dictionary<string, string> diacriticChars = new Dictionary<string, string>
    {
        { "ä", "a" },
        { "æǽ", "ae" },
        { "œ", "oe" },
        { "ö", "o" },
        { "ü", "u" },
        { "Ä", "A" },
        { "Ü", "U" },
        { "Ö", "O" },
        { "ÀÁÂÃÄÅǺĀĂĄǍΆẢẠẦẪẨẬẰẮẴẲẶ", "A" },
        { "àáâãåǻāăąǎảạầấẫẩậằắẵẳặа", "a" },
        { "ÇĆĈĊČ", "C" },
        { "çćĉċč", "c" },
        { "ÐĎĐ", "D" },
        { "ðďđ", "d" },
        { "ÈÉÊËĒĔĖĘĚΈẼẺẸỀẾỄỂỆ", "E" },
        { "èéêëēĕėęěẽẻẹềếễểệе", "e" },
        { "ĜĞĠĢ", "G" },
        { "ĝğġģ", "g" },
        { "ĤĦΉ", "H" },
        { "ĥħ", "h" },
        { "ÌÍÎÏĨĪĬǏĮİΊΪỈỊЇ", "I" },
        { "ìíîïĩīĭǐįıίϊỉịї", "i" },
        { "Ĵ", "J" },
        { "ĵ", "j" },
        { "Ķ", "K" },
        { "ķ", "k" },
        { "ĹĻĽĿŁ", "L" },
        { "ĺļľŀł", "l" },
        { "ÑŃŅŇ", "N" },
        { "ñńņňŉ", "n" },
        { "ÒÓÔÕŌŎǑŐƠØǾΌỎỌỒỐỖỔỘỜỚỠỞỢ", "O" },
        { "òóôõōŏǒőơøǿºόỏọồốỗổộờớỡởợ", "o" },
        { "ŔŖŘ", "R" },
        { "ŕŗř", "r" },
        { "ŚŜŞȘŠ", "S" },
        { "śŝşșš", "s" },
        { "ȚŢŤŦТ", "T" },
        { "țţťŧ", "t" },
        { "ÙÚÛŨŪŬŮŰŲƯǓǕǗǙǛŨỦỤỪỨỮỬỰ", "U" },
        { "ùúûũūŭůűųưǔǖǘǚǜủụừứữửự", "u" },
        { "ÝŸŶΎΫỲỸỶỴ", "Y" },
        { "Й", "Й" },
        { "й", "и" },
        { "ýÿŷỳỹỷỵ", "y" },
        { "Ŵ", "W" },
        { "ŵ", "w" },
        { "ŹŻŽ", "Z" },
        { "źżž", "z" },
        { "ÆǼ", "AE" },
        { "ß", "ss" },
        { "Ĳ", "IJ" },
        { "ĳ", "ij" },
        { "Œ", "OE" },
        { "Ё", "Е" },
        { "ё", "е" },
    };
}

