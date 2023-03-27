# slsk-batchdl

A batch downloader for Soulseek using Soulseek.NET. Accepts csv files and spotify playlist urls.

```
Usage: slsk-batchdl.exe [OPTIONS]
Options:
  -p --parent <path>           Downloaded music will be placed here
  -n --name <name>             Folder / playlist name. If not specified, the name of the csv file / spotify playlist is used.
  --username <username>        Soulseek username
  --password <password>        Soulseek password

  --spotify <url>              Download a spotify playlist
  --spotify-id <id>            Your spotify client id (use if the default fails or if playlist private)
  --spotify-secret <sec>       Your spotify client secret (use if the default fails or if playlist private)

  --csv <path>                 Use a csv file containing track info to download
  --artist-col <column>        Specify if the csv file contains an artist name column
  --track-col <column>         Specify if if the csv file contains an track name column
  --album-col <unit>           CSV album column name. Optional, may improve searching
  --full-title-col <column>    Specify only if there are no separate artist and track name columns in the csv
  --uploader-col <column>      Specify when using full title col if there is also an uploader column in the csv (fallback in case artist name cannot be extracted from title)
  --length-col <column>        CSV duration column name. Recommended, will improve accuracy
  --time-unit <unit>           Time unit for the track duration column, ms or s (default: s)

  --pref-format <format>       Preferred file format (default: mp3)
  --pref-length-tolerance <tol> Preferred length tolerance (if length col provided) (default: 3)
  --pref-min-bitrate <rate>    Preferred minimum bitrate (default: 200)
  --pref-max-bitrate <rate>    Preferred maximum bitrate (default: 2200)
  --pref-max-sample-rate <rate> Preferred maximum sample rate (default: 96000)
  --nec-format <format>        Necessary file format
  --nec-length-tolerance <tol> Necessary length tolerance (default: 3)
  --nec-min-bitrate <rate>     Necessary minimum bitrate
  --nec-max-bitrate <rate>     Necessary maximum bitrate
  --nec-max-sample-rate <rate> Necessary maximum sample rate

  --skip-existing              Skip if a track matching the conditions is found in the output folder or your music library (if provided)
  --music-dir <path>           Specify to also skip downloading tracks which are in your library, use with --skip-existing
  --skip-if-pref-failed        Skip if preferred versions of a track exist but failed to download. If no pref. versions were found, download as normal.
  --create-m3u                 Create an m3u playlist file
  --m3u-only                   Only create an m3u playlist file with existing tracks and exit
  --m3u <path>                 Where to place created m3u files (--parent by default)
  --yt-dlp                     Use yt-dlp to download tracks that weren't found on Soulseek. yt-dlp must be availble from the command line.
  --yt-dlp-f <format>          yt-dlp audio format (default: "bestaudio/best")

  --search-timeout <timeout>   Maximal search time (default: 15000)
  --download-max-stale-time <time> Maximal download time with no progress (default: 80000)
  --max-concurrent-processes <num> Max concurrent searches / downloads (default: 2)
  --max-retries-per-file <num> Maximum number of users to try downloading from before skipping track (default: 30)
```
- Files satisfying `pref` conditions will be preferred. Files not satisfying `nec` conditions will not be downloaded.  
- When using csv, provide either both a track-col and artist-col (ideally), or full-title-col in case separate artist and track names are unavailable. You can also specify --uploader-col (channel names) in that case to use as artist names whenever full-title-col doesn't contain them. Always provide a length-col or get wrong results  

Download tracks from a csv file and create m3u:
```
slsk-batchdl.exe -p "C:\Users\fiso64\Music\Playlists" --csv "C:\Users\fiso64\Downloads\test.csv" --username "fakename" --password "fakepass" --artist-col "Artist Name(s)" --track-col "Track Name" --length-col "Duration (ms)" --time-unit "ms" --skip-existing --create-m3u --pref-format "flac"
```
Download spotify playlist with fallback to yt-dlp and create a m3u:
```
slsk-batchdl.exe --spotify <url> -p "C:\Users\fiso64\Music\Playlists" --m3u "C:\Users\fiso64\Documents\MusicBee\Playlists" --music-dir "C:\Users\fiso64\Music" --username "fakename" --password "fakepass" --skip-existing --pref-format "flac" --yt-dlp
```
You might need to provide an id and secret when using spotify (which you can get here https://developer.spotify.com/dashboard/applications, under "Create an app").

## Notes:
- The console output tends to break after a while
- Much of the code was written by ChatGPT
