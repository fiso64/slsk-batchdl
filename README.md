# slsk-batchdl

A batch downloader for Soulseek using Soulseek.NET. Accepts CSV files, Spotify & YouTube urls.

- Download tracks from a csv file:
```
slsk-batchdl --csv test.csv --artist-col "Artist Name(s)" --track-col "Track Name" --length-col "Duration (ms)" --time-unit ms
```  
You can omit the column names provided they are named predictably (like in this example). Use `--print-tracks` before downloading to check if everything has been parsed correctly.

- Download spotify likes while skipping existing songs, and create an m3u file:
```
slsk-batchdl --spotify likes --m3u --skip-existing
```
You might need to provide an id and secret when using spotify (e.g when downloading a private playlist), which you can get here https://developer.spotify.com/dashboard/applications. Create an app, then select it and add `http://localhost:48721/callback` as a redirect url in the settings.  
  
- Download the first 10 songs of a youtube playlist:
```
slsk-batchdl -n 10 --youtube "https://www.youtube.com/playlist?list=PLI_eFW8NAFzYAXZ5DrU6E6mQ_XfhaLBUX"
```
To include unavailable videos, you will need to provide an api key with `--youtube-key`. Get it here https://console.cloud.google.com. Create a new project, click "Enable Api" and search for "youtube data", then follow the prompts.  

- Search & download a specific song, preferring flac and wav files:
```
slsk-batchdl "title=MC MENTAL @ HIS BEST,duration=242" --pref-format "flac,wav"
```

### Options:
```
Usage: slsk-batchdl [OPTIONS]
Options:
  --user <username>              Soulseek username
  --pass <password>              Soulseek password

  --spotify <url>                Download a spotify playlist ("likes" for liked music)
  --spotify-id <id>              Your spotify client id (required for private playlists)
  --spotify-secret <sec>         Your spotify client secret (required for private playlists)

  --youtube <url>                Get tracks from a YouTube playlist
  --youtube-key <key>            Provide an API key to include unavailable uploads

  --csv <path>                   Use a csv file containing track info to download
  --artist-col <column>          Artist or uploader column name
  --title-col <column>           Title or track name column name
  --album-col <column>           Track album column name (optional for more results)
  --length-col <column>          Track duration column name (optional for better accuracy)
  --time-unit <unit>             Time unit in track duration column, ms or s (default: s)
  --yt-desc-col <column>         YT description column name (optional, use with --yt-parse)
  --yt-id-col <column>           Youtube video ID column (optional, use with --yt-parse)
  --yt-parse                     Enable if you have a csv file of YouTube video titles and
                                 channel names; attempt to parse them into title and artist

  -s --single <str>              Search & download a specific track. <str> is a simple
                                 search string, or a comma-separated list of properties:
                                 "title=Song Name,artist=Artist Name,length=215"

  -p --path <path>               Place downloaded files in custom path
  -f --folder <name>             Custom folder name (default: provided playlist name)
  -n --number <maxtracks>        Download at most n tracks of a playlist
  -o --offset <offset>           Skip a specified number of tracks
  --reverse                      Download tracks in reverse order
  --name-format <format>         Name format for downloaded tracks, e.g "{artist} - {title}"
  --m3u                          Create an m3u8 playlist file

  --pref-format <format>         Preferred file format(s), comma-separated (default: mp3)
  --pref-length-tol <tol>        Preferred length tolerance in seconds (default: 3)
  --pref-min-bitrate <rate>      Preferred minimum bitrate (default: 200)
  --pref-max-bitrate <rate>      Preferred maximum bitrate (default: 2200)
  --pref-max-samplerate <rate>   Preferred maximum sample rate (default: 96000)
  --pref-strict-title            Prefer download if filename contains track title
  --pref-strict-artist           Prefer download if filepath contains track artist
  --pref-danger-words <list>     Comma-separated list of words that must appear in either
                                 both search result and track title, or in neither of the
                                 two, case-insensitive (default:"mix, edit, dj, cover")
  --nec-format <format>          Necessary file format(s), comma-separated
  --nec-length-tol <tol>         Necessary length tolerance in seconds (default: 3)
  --nec-min-bitrate <rate>       Necessary minimum bitrate
  --nec-max-bitrate <rate>       Necessary maximum bitrate
  --nec-max-samplerate <rate>    Necessary maximum sample rate
  --nec-strict-title             Only download if filename contains track title
  --nec-strict-artist            Only download if filepath contains track artist
  --nec-danger-words <list>      Comma-separated list of words that must appear in either
                                 both search result and track title, or in neither of the
                                 two. Case-insensitive. (default:"mix, edit, dj, cover")

  --skip-existing                Skip if a track matching nec. conditions is found in the
                                 output folder or your music library (if provided)
  --skip-mode <mode>             "name": Use only filenames to check if a track exists
                                 "name-precise": Use filenames and check nec-cond (default)
                                 "tag": Use tags (slower)
                                 "tag-precise": Use tags and check all nec. cond. (slower)
  --music-dir <path>             Specify to skip downloading tracks found in a music library
                                 Use with --skip-existing
  --skip-not-found               Skip searching for tracks that weren't found on Soulseek
                                 last run
  --remove-ft                    Remove "ft." or "feat." and everything after from the
                                 track names before searching.
  --album-search                 Also search for album name before filtering for track name.
                                 Sometimes helps to find more, but slower.
  --artist-search                Also search for artist, before filtering for track name.
                                 Sometimes helps to find more, but slower.
  --no-artist-search             Also perform a search without artist name if nothing was
                                 found. Only use if the source is imprecise
                                 and the provided "artist" is possibly wrong (yt, sc)
  --no-regex-search <reg>        Also perform a search with a regex pattern removed from the
                                 titles and artist names
  --no-diacr-search              Also perform a search without diacritics
  -d --desperate                 Equivalent to enabling all additional searches
  --yt-dlp                       Use yt-dlp to download tracks that weren't found on
                                 Soulseek. yt-dlp must be available from the command line.

  --search-timeout <ms>          Maximal search time (ms, default: 6000)
  --max-stale-time <ms>          Maximal download time with no progress (ms, default: 50000)
  --concurrent-processes <num>   Max concurrent searches & downloads (default: 2)
  --display <str>                "single" (default): Show transfer state and percentage.
                                 "double": Also show a progress bar. "simple": simple

  --print-tracks                 Do not search, only print all tracks to be downloaded
  --print-results                Do not download, print search results satisfying nec. cond.
  --print-results-full           Do not download, print all search results with full path
```
Files satisfying `pref-` conditions will be preferred. Files not satisfying `nec-` conditions will not be downloaded. For example, `--nec-length-tol` is set to 3 by default, which means that files whose duration differs from the supplied duration by more than 3 seconds will not be downloaded. Increase it to download e.g a youtube playlist of music videos with intros/outros, or disable it entirely by setting it to 99999.
  
Supports .conf files: Create a file named `slsk-batchdl.conf` in the same directory as the exe and write your arguments there, e.g:
```
--username "fakename"
--password "fakepass"
--pref-format "flac"
```  
  
### Notes:
- The CSV file must be saved with `,` as field delimiter and `"` as string delimiter, encoded with UTF8
- `--display single` and especially `double` can cause the printed lines to be duplicated or overwritten on some configurations. Use `simple` if that's an issue. In my testing on Windows, the terminal app seems to be affected by this (unlike the old command prompt).
