# slsk-batchdl

A batch downloader for Soulseek using Soulseek.NET. Accepts CSV files, Spotify & YouTube urls.

```
Usage: slsk-batchdl.exe [OPTIONS]
Options:
  -p --parent <path>           Downloaded music will be placed here
  -n --name <name>             Folder / playlist name. If not specified, the name of the csv file / spotify / yt playlist is used.
  --username <username>        Soulseek username
  --password <password>        Soulseek password

  --spotify <url>              Download a spotify playlist. "likes" to download all your liked music.
  --spotify-id <id>            Your spotify client id (use if the default fails or if playlist private)
  --spotify-secret <sec>       Your spotify client secret (use if the default fails or if playlist private)

  --youtube <url>              Get tracks from a YouTube playlist
  --youtube-key <key>          Provide an API key if you also want to search for unavailable uploads
  --no-channel-search          Enable to also perform a search without channel name if nothing was found (only for yt).

  --csv <path>                 Use a csv file containing track info to download
  --artist-col <column>        Artist or uploader name column
  --title-col <column>         Title or track name column
  --album-col <column>         CSV album column name. Optional, may improve searching, slower
  --length-col <column>        CSV duration column name. Recommended, will improve accuracy
  --time-unit <unit>           Time unit for the track duration column, ms or s (default: s)
  --yt-desc-col <column>       Description column name. Use with --yt-parse.
  --yt-id-col <column>         Youtube video ID column (only needed if length-col or yt-desc-col don't exist). Use with --yt-parse.
  --yt-parse                   Enable if you have a csv file of YouTube video titles and channel names; attempt to parse.

  --pref-format <format>       Preferred file format (default: mp3)
  --pref-length-tol <tol>      Preferred length tolerance (if length col provided) (default: 3)
  --pref-min-bitrate <rate>    Preferred minimum bitrate (default: 200)
  --pref-max-bitrate <rate>    Preferred maximum bitrate (default: 2200)
  --pref-max-samplerate <rate> Preferred maximum sample rate (default: 96000)
  --pref-danger-words <list>   Comma separated list of words that must appear in either both search result and track title, or in neither of the two. Case-insensitive. (default: "mix, edit,dj ,cover")
  --nec-format <format>        Necessary file format
  --nec-length-tolerance <tol> Necessary length tolerance (default: 3)
  --nec-min-bitrate <rate>     Necessary minimum bitrate
  --nec-max-bitrate <rate>     Necessary maximum bitrate
  --nec-max-samplerate <rate>  Necessary maximum sample rate
  --nec-danger-words <list>    Comma separated list of words that must appear in either both search result and track title, or in neither of the two. Case-insensitive. (default: "mix, edit,dj ,cover")

  --album-search               Also search for "[Album name] [track name]". Occasionally helps to find more, slower.
  --no-diacr-search            Also perform a search without diacritics
  --skip-existing              Skip if a track matching the conditions is found in the output folder or your music library (if provided)
  --skip-notfound              Skip searching for tracks that weren't found in Soulseek last time
  --remove-ft                  Remove "ft." or "feat." and everything after from the track names.
  --remove-strings <strings>   Comma separated list of strings to remove when searching for tracks. Case insesitive.
  --music-dir <path>           Specify to also skip downloading tracks which are in your library, use with --skip-existing
  --reverse                    Download tracks in reverse order
  --skip-if-pref-failed        Skip if preferred versions of a track exist but failed to download. If no pref. versions were found, download as normal.
  --create-m3u                 Create an m3u playlist file
  --m3u-only                   Only create an m3u playlist file with existing tracks and exit
  --m3u <path>                 Where to place created m3u files (--parent by default)
  --yt-dlp                     Use yt-dlp to download tracks that weren't found on Soulseek. yt-dlp must be available from the command line.
  --yt-dlp-f <format>          yt-dlp audio format (default: "bestaudio/best")

  --search-timeout <ms>        Maximal search time (default: 10000)
  --max-stale-time <ms>        Maximal download time with no progress (default: 60000)
  --concurrent-processes <num> Max concurrent searches / downloads (default: 2)
  --max-retries <num>          Maximum number of users to try downloading from before skipping track (default: 30)

  --slow-output                Enable if the progress bars aren't properly updated (bug)
```
Files satisfying `pref` conditions will be preferred. Files not satisfying `nec` conditions will not be downloaded.  
  
Download tracks from a csv file and create m3u:
```
slsk-batchdl.exe -p "C:\Users\fiso64\Music\Playlists" --csv "C:\Users\fiso64\Downloads\test.csv" --username "fakename" --password "fakepass" --artist-col "Artist Name(s)" --track-col "Track Name" --length-col "Duration (ms)" --time-unit "ms" --skip-existing --create-m3u --pref-format "flac"
```  
  
Download spotify playlist with fallback to yt-dlp and create a m3u:
```
slsk-batchdl.exe --spotify <url> -p "C:\Users\fiso64\Music\Playlists" --m3u "C:\Users\fiso64\Documents\MusicBee\Playlists" --music-dir "C:\Users\fiso64\Music" --username "fakename" --password "fakepass" --skip-existing --pref-format "flac" --yt-dlp
```
You might need to provide an id and secret when using spotify, which you can get here https://developer.spotify.com/dashboard/applications. Create an app, then select it and add `http://localhost:48721/callback` as a redirect url in the settings.  
  
Download youtube playlist:
```
--youtube "https://www.youtube.com/playlist?list=PLI_eFW8NAFzYAXZ5DrU6E6mQ_XfhaLBUX" -p "C:\Users\fiso64\Music\Playlists" --username "fakename" --password "fakepass"
```
To include unavailable videos, you will need to provide an api key with `--youtube-key`. Get it here https://console.cloud.google.com. Create a new project, click "Enable Api" and search for "youtube data", then follow the prompts.  
  
## Notes:
- YouTube playlist downloading is unreliable since there are no track name / artist tags
- The CSV file must be saved with `,` as field delimiter and `"` as string delimiter, encoded with UTF8
- 40% of the code was written by ChatGPT
