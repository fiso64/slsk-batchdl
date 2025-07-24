using Models;
using System.Text;
using System.Text.RegularExpressions;

public static class TrackTemplateParser
{
    // Cache compiled regex patterns for performance if the same template is used often
    private static readonly object cacheLock = new();
    private static Dictionary<string, Tuple<Regex, List<string>>>? regexCache = null;

    /// <summary>
    /// Creates a Track object by parsing an input string based on a template.
    /// </summary>
    /// <param name="input">The string containing the track data (e.g., "Foo - Bar").</param>
    /// <param name="template">The template defining the structure with placeholders like {artist}, {title}, {album} (e.g., "{artist} - {title}").</param>
    /// <returns>A Track object populated with the parsed data, or null if the input does not match the template.</returns>
    /// <exception cref="ArgumentNullException">Thrown if input or template is null.</exception>
    /// <exception cref="ArgumentException">Thrown if the template is invalid (e.g., mismatched braces).</exception>
    public static Track? CreateFromString(string input, string template)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (template == null) throw new ArgumentNullException(nameof(template));

        Regex patternRegex;
        List<string> fieldNames;

        // --- 1. Get or Create the Regex pattern and Field List ---
        try
        {
            (patternRegex, fieldNames) = GetOrCreateRegexAndFields(template);
        }
        catch (ArgumentException ex) // Catch potential issues during regex build
        {
            // Re-throw with more context or handle as needed
            throw new ArgumentException($"Invalid template format: {ex.Message}", nameof(template), ex);
        }


        // --- 2. Match the input string against the pattern ---
        Match match = patternRegex.Match(input);

        if (!match.Success)
        {
            return null; // Input string doesn't match the template structure
        }

        // --- 3. Populate the Track object ---
        Track track = new Track();
        PopulateTrackFields(track, fieldNames, match);

        return track;
    }

    /// <summary>
    /// Attempts to update an existing Track object by parsing an input string based on a template.
    /// If the input does not match the template, the track object is not modified.
    /// </summary>
    /// <param name="input">The string containing the track data.</param>
    /// <param name="template">The template defining the structure.</param>
    /// <param name="track">The Track object to update.</param>
    /// <returns>True if the input matched the template and the track was updated, false otherwise.</returns>
    /// <exception cref="ArgumentNullException">Thrown if input, template, or track is null.</exception>
    /// <exception cref="ArgumentException">Thrown if the template is invalid.</exception>
    public static bool TryUpdateTrack(string input, string template, Track track)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (template == null) throw new ArgumentNullException(nameof(template));
        if (track == null) throw new ArgumentNullException(nameof(track));

        Regex patternRegex;
        List<string> fieldNames;

        // --- 1. Get or Create the Regex pattern and Field List ---
        try
        {
            (patternRegex, fieldNames) = GetOrCreateRegexAndFields(template);
        }
        catch (ArgumentException ex) // Catch potential issues during regex build
        {
            // Re-throw with more context or handle as needed
            throw new ArgumentException($"Invalid template format: {ex.Message}", nameof(template), ex);
        }

        // --- 2. Match the input string against the pattern ---
        Match match = patternRegex.Match(input);

        if (!match.Success)
        {
            return false; // Input string doesn't match the template structure, do nothing
        }

        // --- 3. Populate the existing Track object ---
        PopulateTrackFields(track, fieldNames, match);

        return true; // Update successful
    }


    /// <summary>
    /// Gets the compiled Regex and field list from cache or builds them if not present.
    /// </summary>
    private static (Regex, List<string>) GetOrCreateRegexAndFields(string template)
    {
        lock (cacheLock) // Protect cache access
        {
            regexCache ??= new Dictionary<string, Tuple<Regex, List<string>>>();
            if (!regexCache.TryGetValue(template, out var cachedData))
            {
                // Build the regex pattern and extract field names from the template
                // BuildRegexFromTemplate handles its own ArgumentException for invalid templates
                (Regex patternRegex, List<string> fieldNames) = BuildRegexFromTemplate(template);
                regexCache[template] = Tuple.Create(patternRegex, fieldNames);
                return (patternRegex, fieldNames);
            }
            else
            {
                return (cachedData.Item1, cachedData.Item2);
            }
        }
    }

    /// <summary>
    /// Populates the fields of a Track object based on the matched regex groups.
    /// </summary>
    private static void PopulateTrackFields(Track track, List<string> fieldNames, Match match)
    {
        // Group 0 is the full match, groups 1+ correspond to our captures
        for (int i = 0; i < fieldNames.Count; i++)
        {
            string fieldName = fieldNames[i].ToLowerInvariant(); // Case-insensitive matching
            string fieldValue = match.Groups[i + 1].Value.Trim(); // Get captured value and trim whitespace

            // Assign to the correct field in the Track object
            // This part needs modification if you add more fields to Track
            switch (fieldName)
            {
                case "artist":
                    track.Artist = fieldValue;
                    break;
                case "title":
                    track.Title = fieldValue;
                    break;
                case "album":
                    track.Album = fieldValue;
                    break;
                default:
                    // Unknown field found in template - currently ignored.
                    // Consider logging this or throwing an exception if strictness is required.
                    break;
            }
        }
    }


    /// <summary>
    /// Helper method to convert the template string into a Regex object
    /// and a list of corresponding field names.
    /// </summary>
    private static (Regex, List<string>) BuildRegexFromTemplate(string template)
    {
        var fieldNames = new List<string>();
        var patternBuilder = new StringBuilder("^\\s*"); // Anchor at the start, allow leading whitespace

        // Use regex to find placeholders like {name}
        // This regex finds '{' followed by one or more characters that are not '{' or '}', then '}'
        string placeholderPattern = @"\{([^{}]+)\}";
        int lastIndex = 0;

        foreach (Match match in Regex.Matches(template, placeholderPattern))
        {
            // Append the literal text part before this placeholder, escaping it for regex
            if (match.Index > lastIndex)
            {
                patternBuilder.Append(Regex.Escape(template.Substring(lastIndex, match.Index - lastIndex)));
            }

            // Extract the field name (content inside {})
            string fieldName = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                throw new ArgumentException("Template contains an empty placeholder '{}'.");
            }
            fieldNames.Add(fieldName);

            // Append a non-greedy capturing group for this placeholder
            patternBuilder.Append("(.*?)"); // Non-greedy match

            lastIndex = match.Index + match.Length;
        }

        // Append any remaining literal text after the last placeholder
        if (lastIndex < template.Length)
        {
            patternBuilder.Append(Regex.Escape(template.Substring(lastIndex)));
        }

        patternBuilder.Append("\\s*$"); // Allow trailing whitespace, anchor at the end

        // Compile the regex for performance
        RegexOptions options = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase; // Case-insensitive matching for literals
        return (new Regex(patternBuilder.ToString(), options), fieldNames);
    }
}
