
// undocumented options
// --login, --random-login, --no-modify-share-count, --unknown-error-retries
// --invalid-replace-str, --cond, --pref, --danger-words, --pref-danger-words, --strict-title, --strict-artist, --strict-album
// --fast-search-delay, --fast-search-min-up-speed
// --min-album-track-count, --max-album-track-count, --extract-max-track-count, --extract-min-track-count 

public static class Help
{
    const string helpText = @"
    Usage: sldl <input> [OPTIONS]

      Required Arguments 
        <input>                        A url, search string, or path to a local CSV file.
                                       Run --help ""input"" to view the accepted inputs. 
                                       Can also be passed with -i, --input <input>            
        --user <username>              Soulseek username
        --pass <password>              Soulseek password

      General Options
        -p, --path <path>              Download directory
        -f, --folder <name>            Subfolder name. Set to '.' to output directly to --path
        --input-type <type>            Force set input type, [csv|youtube|spotify|bandcamp|string]
        --name-format <format>         Name format for downloaded tracks. See --help name-format
          
        -n, --number <maxtracks>       Download the first n tracks of a playlist
        -o, --offset <offset>          Skip a specified number of tracks
        -r, --reverse                  Download tracks in reverse order
        -c, --config <path>            Set config file location. Set to 'none' to ignore config
        --profile <names>              Configuration profile(s) to use. See --help ""config"".
        --concurrent-downloads <num>   Max concurrent downloads (default: 2)
        --m3u <option>                 Create an m3u8 playlist file in the output directory
                                       'none' (default for single inputs): Do not create 
                                       'index' (default): Write a line indexing all downloaded 
                                       files, required for skip-not-found or skip-existing=m3u
                                       'all': Write the index and a list of paths and fails
        --m3u-path <path>              Override default m3u path
            
        -s, --skip-existing            Skip if a track matching file conditions is found in the
                                       output folder or your music library (if provided)
        --skip-mode <mode>             [name|tag|m3u|name-cond|tag-cond|m3u-cond]
                                       See --help ""skip-existing"".
        --music-dir <path>             Specify to also skip downloading tracks found in a music
                                       library. Use with --skip-existing
        --skip-not-found               Skip searching for tracks that weren't found on Soulseek
                                       during the last run. Fails are read from the m3u file.
        --skip-existing-pref-cond      Use preferred instead of necessary conds for skip-existing
          
        --display-mode <option>        Changes how searches and downloads are displayed:
                                       'single' (default): Show transfer state and percentage
                                       'double': Transfer state and a large progress bar 
                                       'simple': No download bars or changing percentages
        --print <option>               Print tracks or search results instead of downloading:
                                       'tracks': Print all tracks to be downloaded
                                       'tracks-full': Print extended information about all tracks
                                       'results': Print search results satisfying file conditions
                                       'results-full': Print search results including full paths
        --debug                        Print extra debug info
          
        --listen-port <port>           Port for incoming connections (default: 49998)
        --on-complete <command>        Run a command whenever a file is downloaded.
                                       Available placeholders: {path} (local save path), {title},
                                       {artist},{album},{uri},{length},{failure-reason},{state}.
                                       Prepend a state number to only download in specific cases:
                                       1:, 2:, 3:, 4: for the Downloaded, Failed, Exists, and
                                       NotFoundLastTime states respectively. 
                                       E.g: '1:<cmd>' will only run the command if the file is
                                       downloaded successfully. Prepend 's:' to use the system
                                       shell to execute the command.
          
      Searching
        --fast-search                  Begin downloading as soon as a file satisfying the preferred
                                       conditions is found. Only for normal download mode.
        --remove-ft                    Remove 'feat.' and everything after before searching
        --no-remove-special-chars      Do not remove special characters before searching
        --remove-brackets              Remove square brackets and their contents before searching
        --regex <regex>                Remove a regexp from all track titles and artist names.
                                       Optionally specify a replacement regex after a semicolon.
                                       Add 'T:', 'A:' or 'L:' at the start to only apply this to
                                       the track title, artist, or album respectively.
        --artist-maybe-wrong           Performs an additional search without the artist name.
                                       Useful for sources like SoundCloud where the ""artist""
                                       could just be an uploader. Note that when downloading a
                                       YouTube playlist via url, this option is set automatically
                                       on a per-track basis, so it is best kept off in that case.
        -d, --desperate                Tries harder to find the desired track by searching for the
                                       artist/album/title only, then filtering. (slower search)
        --fails-to-downrank <num>      Number of fails to downrank a user's shares (default: 1)
        --fails-to-ignore <num>        Number of fails to ban/ignore a user's shares (default: 2)
          
        --yt-dlp                       Use yt-dlp to download tracks that weren't found on
                                       Soulseek. yt-dlp must be available from the command line.
        --yt-dlp-argument <str>        The command line arguments when running yt-dlp. Default:
                                       ""{id}"" -f bestaudio/best -cix -o ""{savepath}.%(ext)s""
                                       Available vars are: {id}, {savedir}, {savepath} (w/o ext).
                                       Note that with -x, yt-dlp will download webms in case
                                       ffmpeg is unavailable.
          
        --search-timeout <ms>          Max search time in ms (default: 6000)
        --max-stale-time <ms>          Max download time without progress in ms (default: 50000)
        --searches-per-time <num>      Max searches per time interval. Higher values may cause
                                       30-minute bans, see --help ""search"". (default: 34)
        --searches-renew-time <sec>    Controls how often available searches are replenished.
                                       See --help ""search"". (default: 220)
          
      Spotify
        --spotify-id <id>              Spotify client ID
        --spotify-secret <secret>      Spotify client secret
        --spotify-token <token>        Spotify access token
        --spotify-refresh <token>      Spotify refresh token
        --remove-from-source           Remove downloaded tracks from source playlist
          
      YouTube
        --youtube-key <key>            Youtube data API key
        --get-deleted                  Attempt to retrieve titles of deleted videos from wayback
                                       machine. Requires yt-dlp.
        --deleted-only                 Only retrieve & download deleted music.
          
      CSV Files
        --artist-col                   Artist column name
        --title-col                    Track title column name
        --album-col                    Album column name
        --length-col                   Track length column name
        --album-track-count-col        Album track count column name (sets --album-track-count)
        --yt-desc-col                  Youtube description column (improves --yt-parse)
        --yt-id-col                    Youtube video id column (improves --yt-parse)
            
        --time-format <format>         Time format in Length column of the csv file (e.g h:m:s.ms
                                       for durations like 1:04:35.123). Default: s
        --yt-parse                     Enable if the CSV contains YouTube video titles and channel
                                       names; attempt to parse them into title and artist names.
        --remove-from-source           Remove downloaded tracks from source CSV file 
          
      File Conditions
        --format <formats>             Accepted file format(s), comma-separated, without periods
        --length-tol <sec>             Length tolerance in seconds
        --min-bitrate <rate>           Minimum file bitrate
        --max-bitrate <rate>           Maximum file bitrate
        --min-samplerate <rate>        Minimum file sample rate
        --max-samplerate <rate>        Maximum file sample rate
        --min-bitdepth <depth>         Minimum bit depth
        --max-bitdepth <depth>         Maximum bit depth
        --banned-users <list>          Comma-separated list of users to ignore
          
        --pref-format <formats>        Preferred file format(s), comma-separated (default: mp3)
        --pref-length-tol <sec>        Preferred length tolerance in seconds (default: 3)
        --pref-min-bitrate <rate>      Preferred minimum bitrate (default: 200)
        --pref-max-bitrate <rate>      Preferred maximum bitrate (default: 2500)
        --pref-min-samplerate <rate>   Preferred minimum sample rate
        --pref-max-samplerate <rate>   Preferred maximum sample rate (default: 48000)
        --pref-min-bitdepth <depth>    Preferred minimum bit depth
        --pref-max-bitdepth <depth>    Preferred maximum bit depth
        --pref-banned-users <list>     Comma-separated list of users to downrank
          
        --strict-conditions            Skip files with missing properties instead of accepting by
                                       default; if --min-bitrate is set, ignores any files with
                                       unknown bitrate.
          
      Album Download
        -a, --album                    Album download mode: Download a folder
        -t, --interactive              Interactive mode, allows to select the folder and images
        --album-track-count <num>      Specify the exact number of tracks in the album. Add a + or
                                       - for inequalities, e.g '5+' for five or more tracks.
        --album-ignore-fails           Do not skip to the next source and do not delete all 
                                       successfully downloaded files if one of the files in the 
                                       folder fails to download
        --album-art <option>           Retrieve additional images after downloading the album:
                                       'default': No additional images
                                       'largest': Download from the folder with the largest image
                                       'most': Download from the folder containing the most images
        --album-art-only               Only download album art for the provided album
        --no-browse-folder             Do not automatically browse user shares to get all files in
                                       in the folder
          
      Aggregate Download
        -g, --aggregate                Aggregate download mode: Find and download all distinct 
                                       songs associated with the provided artist, album, or title.
        --min-shares-aggregate <num>   Minimum number of shares of a track or album for it to be
                                       downloaded in aggregate mode. (Default: 2)
        --relax-filtering              Slightly relax file filtering in aggregate mode to include
                                       more results

      Help
        -h, --help [option]            [all|input|download-modes|search|name-format|
                                       file-conditions|skip-existing|config]
          
      Notes 
        Acronyms of two- and --three-word-flags are also accepted, e.g. --twf. If the option
        contains the word 'max' then the m should be uppercase. 'bitrate', 'sameplerate' and 
        'bitdepth' should be all treated as two separate words, e.g --Mbr for --max-bitrate.

        Flags can be explicitly disabled by setting them to false, e.g '--interactive false'
    ";

    const string inputHelp = @"
    Input types

      The input type is usually determined automatically. To force a specific input type, set
      --input-type [spotify|youtube|csv|string|bandcamp]. The following input types are available:
            
      CSV file            
        Path to a local CSV file: Use a csv file containing track info of the songs to download. 
        The names of the columns should be Artist, Title, Album, Length, although alternative names 
        are usually detected as well. Only the title or album column is required, but extra info may 
        improve search results. Every row that does not have a title column text will be treated as an
        album download.
            
      YouTube 
        A playlist url: Download songs from a youtube playlist.
        The default method to retrieve playlists doesn't always return all videos, especially not 
        the ones which are unavailable. To get all video titles, you can use the official API by 
        providing a key with --youtube-key. Get it here https://console.cloud.google.com. Create a 
        new project, click ""Enable Api"" and search for ""youtube data"", then follow the prompts. 
        
        Tip: For playlists containing music videos, it may be better to remove all text in parentheses 
        (to remove (Lyrics), (Official), etc) and disable song duration checking: 
        --regex ""[\[\(].*?[\]\)]"" --pref-length-tol -1     

      Spotify
        A playlist/album url or 'spotify-likes': Download a spotify playlist, album, or your 
        liked songs. --spotify-id and --spotify-secret are required in addition when downloading
        a private playlist or liked music.
        The id and secret can be obtained at https://developer.spotify.com/dashboard/applications. 
        Create an app and add http://localhost:48721/callback as a redirect url in its settings.

          Using Credentials
            Create a Spotify application at https://developer.spotify.com/dashboard/applications with a 
            redirect url http://localhost:48721/callback. Obtain an application ID and secret from the 
            created application dashboard.  
            Start sldl with the obtained credentials and an authorized action to trigger the Spotify 
            app login flow:

            sldl spotify-likes --spotify-id 123456 --spotify-secret 123456 -n 1 --print-tracks

            sldl will try to open a browser automatically but will fallback to logging the login flow 
            URL to output. After login flow is complete sldl will output a token and refresh token and 
            finish running the current command.  
            To skip requiring login flow every time sldl is used the token and refresh token can be 
            provided to sldl (hint: store this info in the config file to make commands less verbose):

            sldl spotify-likes --spotify-id 123456 --spotify-secret 123456 --spotify-refresh 123456 --spotify-token 123456 -n 1 --pt

            spotify-token access is only valid for 1 hour. spotify-refresh will enable sldl to renew 
            access every time it is run (and can be used without including spotify-token).

      Bandcamp 
        A bandcamp url: Download a single track, an album, or an artist's entire discography. 
        Extracts the artist name, album name and sets --album-track-count=""n+"", where n is the 
        number of visible tracks on the bandcamp page.

      Search string
        Name of the track, album, or artist to search for: Can either be any typical search string 
        (like what you would enter into the soulseek search bar), or a comma-separated list of
        properties like 'title=Song Name, artist=Artist Name, length=215'. 

        The following properties are allowed: 
          title
          artist
          album
          length (in seconds)
          artist-maybe-wrong 

        Example inputs and their interpretations:
          Input String                            | Artist   | Title    | Album    | Length
          ---------------------------------------------------------------------------------
          'Foo Bar'   (without any hyphens)       |          | Foo Bar  |          |
          'Foo - Bar'                             | Foo      | Bar      |          |
          'Foo - Bar' (with --album enabled)      | Foo      |          | Bar      |
          'Artist - Title, length=42'             | Artist   | Title    |          | 42
          'artist=AR, title=T, album=AL'          | AR       | T        | AL       |
    ";

    const string downloadModesHelp = @"
    Download modes
          
      Normal
        The program will download a single file for every input entry.

      Album
        sldl will search for the album and download an entire folder including non-audio files.
        Activated when the input is a link to a spotify or bandcamp album, when the input string
        or csv row has no track title, or when -a/--album is enabled.
        
      Aggregate
        With -g/--aggregate, sldl will first perform an ordinary search for the input, then attempt to
        group the results into distinct songs and download one of each kind. A common use case is
        finding all remixes of a song or printing all songs by an artist that are not your music dir.  
        Two files are considered equal if their inferred track title and artist name are equal 
        (ignoring case and some special characters), and their lengths are within --length-tol of each
        other.
        Note that this mode is not 100% reliable, which is why --min-shares-aggregate is set to 2 by
        default, i.e. any song that is shared only once will be ignored.

      Album Aggregate
        Activated when --album and --aggregate are enabled, in this mode sldl searches for the query
        and groups results into distinct albums. Two folders are considered same if they have the
        same number of audio files, and the durations of the files are within --length-tol of each 
        other (or within 3 seconds if length-tol is not configured). If both folders have exactly one
        audio file with similar lengths, also checks if the inferred title and artist name coincide.
        More reliable than normal aggregate due to much simpler grouping logic.
        Note that --min-shares-aggregate is 2 by default, which means that folders shared only once
        will be ignored.
    ";

    const string searchHelp = @"
    Searching

      Search Query
        The search query is determined as follows:

        - For album downloads: If the album field is non-empty, search for 'Artist Album'.
          Otherwise, search for 'Artist Title'
        - For all other download types: If the title field is non-empty, search for 'Artist Title'.
          Otherwise, search for 'Artist Album'

      Soulseek's rate limits
        The server will ban you for 30 minutes if too many searches are performed within a short 
        timespan. The program has a search limiter which can be adjusted with --searches-per-time 
        and --searches-renew-time (when the limit is reached, the status of the downloads will be 
        ""Waiting""). By default it is configured to allow up to 34 searches every 220 seconds. 
        The default values were determined through experimentation, so they may be incorrect.

      Quality vs Speed
        The following options will make it go faster, but may decrease search result quality or cause
        instability:
          --fast-search skips waiting until the search completes and downloads as soon as a file 
            matching the preferred conditions is found
          --concurrent-downloads - set it to 4 or more
          --max-stale-time is set to 50 seconds by default, so it will wait a long time before giving 
            up on a file
          --searches-per-time increase at the risk of bans.
        
      Quality vs Quantity
        The options --strict-title, --strict-artist and --strict-album will filter any file that 
        does not contain the title/artist/album in the filename (ignoring case, bounded by boundary 
        chars). 
        Another way to prevent false downloads is to set --length-tol to 3 or less to make it ignore 
        any songs that differ from the input by more than 3 seconds. However, all 4 options are already 
        enabled as 'preferred' conditions by default, meaning that such files will only be downloaded 
        as a last resort anyways. Hence it is only recommended to enable them if you need to minimize
        false downloads as much as possible.
    ";

    const string fileConditionsHelp = @"
    File conditions

      Files not satisfying the required conditions will not be downloaded. Files satisfying pref- 
      conditions will be preferred; setting --pref-format ""flac,wav"" will make it download lossless 
      files if available, and only download lossy files if there's nothing else. 
        
      There are no default required conditions. The default preferred conditions are:

        pref-format = mp3
        pref-length-tol = 3
        pref-min-bitrate = 200
        pref-max-bitrate = 2500
        pref-max-samplerate = 48000
        pref-strict-title = true
        pref-strict-album = true
        pref-accept-no-length = false

      sldl will therefore prefer mp3 files with bitrate between 200 and 2500 kbps, and whose length
      differs from the supplied length by no more than 3 seconds. It will also prefer files whose
      paths contain the supplied title and album (ignoring case, and bounded by boundary characters)
      and which have non-null length. Changing the last three preferred conditions is not recommended.
      Note that files satisfying a subset of the preferred conditions will still be preferred over files
      that don't satisfy any condition, but some conditions have precedence over others. For instance,
      a file that only satisfies strict-title (if enabled) will always be preferred over a file that
      only satisfies the format condition. Run with --print ""results-full"" to reveal the sorting logic.
        
      Important note
        Some info may be unavailable depending on the client used by the peer. For example, the standard 
        Soulseek client does not share the file bitrate. If (e.g) --min-bitrate is set, then sldl will 
        still accept any file with unknown bitrate. You can configure it to reject all files where one 
        or more of the checked properties is null (unknown) by enabling --strict-conditions. 
        As a consequence, if --min-bitrate is also set then any files shared by users with the default 
        client will be ignored. Also note that the default preferred conditions will already affect
        ranking with this option due to the bitrate and samplerate checks.
        
      Conditions can also be supplied as a semicolon-delimited string with --cond and --pref, e.g 
      --cond ""br>=320;f=mp3,ogg;sr<96000""
    ";

    const string nameFormatHelp = @"
    Name format
      
      Variables enclosed in {} will be replaced by the corresponding file tag value.         
      Name format supports subdirectories as well as conditional expressions like {tag1|tag2} - If
      tag1 is null, use tag2. String literals enclosed in parentheses are ignored in the null check. 
        
      Examples:
        ""{artist} - {title}""
            Always name it 'Artist - Title'. Because some files on Soulseek are untagged, the 
            following is generally preferred:
        ""{artist( - )title|filename}""
            If artist and title are not null, name it 'Artist - Title', otherwise use the original 
            filename.
        ""{albumartist(/)album(/)track(. )title|(missing-tags/)filename}""  
            Sort files into artist/album folders if all tags are present, otherwise put them in
            the 'missing-tags' folder. 
        
      Available variables:
        artist                          First artist (from the file tags)
        sartist                         Source artist (as on CSV/Spotify/YouTube/etc)
        artists                         Artists, joined with '&'
        albumartist                     First album artist
        albumartists                    Album artists, joined with '&'
        title                           Track title
        stitle                          Source track title
        album                           Album name
        salbum                          Source album name
        year                            Track year or date
        track                           Track number
        disc                            Disc number
        filename                        Soulseek filename without extension
        foldername                      Soulseek folder name
        default-foldername              Default sldl folder name
        extractor                       Name of the extractor used (CSV/Spotify/YouTube/etc)
    ";

    const string skipExistingHelp = @"
    Skip existing

      sldl can skip downloads that exist in the output directory or a specified directory configured
      with --music-dir.
      The following modes are available for --skip-mode:

      m3u
        Default when checking in the output directory.  
        Checks whether the output m3u file contains the track in the '#SLDL' line. Does not check if
        the audio file exists or satisfies the file conditions (use m3u-cond for that). m3u and
        m3u-cond are the only modes that can skip album downloads.

      name
        Default when checking in the music directory.  
        Compares filenames to the track title and artist name to determine if a track already exists.
        Specifically, a track will be skipped if there exists a file whose name contains the title
        and whose full path contains the artist name.

      tag
        Compares file tags to the track title and artist name. A track is skipped if there is a file 
        whose artist tag contains the track artist and whose title tag equals the track title 
        (ignoring case and ws). Slower than name mode as it needs to read all file tags.

      m3u-cond, name-cond, tag-cond
        Same as the above modes but also checks whether the found file satisfies the configured 
        conditions. Uses necessary conditions by default, run with --skip-existing-pref-cond to use 
        preferred conditions instead. Equivalent to the above modes if no necessary conditions have 
        been specified (except m3u-cond, which always checks if the file exists). 
        May be slower and use a lot of memory for large libraries.
    ";

    const string configHelp = @"
    Configuration
      Config Location:
        sldl will look for a file named sldl.conf in the following locations:

          ~/AppData/Roaming/sldl/sldl.conf
          ~/.config/sldl/sldl.conf

        as well as in the directory of the executable.
        
      Syntax:
        Example config file:
        
          username = your-username
          password = your-password
          pref-format = flac
          fast-search = true
        
        Lines starting with hashtags (#) will be ignored. Tildes in paths are expanded as the user 
        directory.

      Configuration profiles:
        Profiles are supported:

          [lossless]
          pref-format = flac,wav

        To activate the above profile, run --profile ""lossless"". To list all available profiles,
        run --profile ""help"".
        Profiles can be activated automatically based on a few simple conditions:
        
          [no-stale]
          profile-cond = interactive && download-mode == ""album""
          max-stale-time = 999999
          # album downloads will never be automatically cancelled in interactive mode
            
          [youtube]
          profile-cond = input-type == ""youtube""
          path = ~/downloads/sldl-youtube
          # download to another location for youtube

        The following operators are supported for use in profile-cond: &&, ||, ==, !=, !{bool}.
        The following variables are available for use in profile-cond:

          input-type        (""youtube""|""csv""|""string""|""bandcamp""|""spotify"")
          download-mode     (""normal""|""aggregate""|""album""|""album-aggregate"")
          interactive       (bool)
    ";

    public static void PrintHelp(string option = "")
    {
        string text = helpText;

        var dict = new Dictionary<string, string>() 
        {
            { "input", inputHelp },
            { "download-modes", downloadModesHelp },
            { "search", searchHelp },
            { "file-conditions", fileConditionsHelp },
            { "name-format", nameFormatHelp },
            { "skip-existing", skipExistingHelp },
            { "config", configHelp },
        };

        if (dict.ContainsKey(option))
            text = dict[option];
        else if (option == "all")
            text = $"{helpText}\n{string.Join('\n', dict.Values)}";
        else if (option.Length > 0)
            Console.WriteLine($"Unrecognized help option '{option}'");

        var lines = text.Split('\n').Skip(1);
        int minIndent = lines.Where(line => line.Trim().Length > 0).Min(line => line.TakeWhile(char.IsWhiteSpace).Count());
        text = string.Join("\n", lines.Select(line => line.Length > minIndent ? line[minIndent..] : line));
        Console.WriteLine(text);
    }
}

