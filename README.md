# slsk-batchdl

A barely-functional batch downloader for Soulseek. Accepts csv files.

```
Usage: slsk-batchdl.exe [OPTIONS]
Options:
  --output <path>              Downloaded files will be placed here
  --csv <path>                 The csv file containing track information (in case it's not in the output folder)
  --username <username>        Soulseek username
  --password <password>        Soulseek password
  
  --artist-col <column>        Specify if the csv file contains an artist name column
  --track-col <column>         Specify if if the csv file contains an track name column
  --full-title-col <column>    Specify only if there are no separate artist and track name columns in the csv
  --uploader-col <column>      Specify when using full title col if there is also an uploader column in the csv (fallback in case artist name cannot be extracted from title)
  --length-col <column>        Specify the name of the track duration column, if exists
  --time-unit <unit>           Time unit for the track duration column, ms or s (default: s)
  
  --skip-existing              Skip if a track matching the conditions is found in the output folder or your music library (if provided)
  --music-dir <path>           Specify to also skip downloading tracks which are in your library, use with --skip-existing
  --skip-if-pref-failed        Skip if preferred versions of a track exist but failed to download. If no pref. versions were found, download as normal.
  --create-m3u                 Create an m3u playlist file in the output dir
  --m3u-only                   Only create an m3u playlist file with existing tracks and exit
  
  --search-timeout <timeout>   Maximal search time (default: 15000)
  --download-max-stale-time <time> Maximal download time with no progress (default: 60000)
  --max-concurrent-processes <num> Max concurrent searches / downloads (default: 2)
  --max-retries-per-file <num> Maximum number of users to try downloading from before skipping track (default: 30)
  
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
```
- Provide either track-col & artist-col (ideally), or full-title-col in case separate artist and title names are unavailable (useful when downloading a csv of a YT playlist). You can also specify --uploader-col (channel names) in that case to use as artist names whenever full-title-col doesn't contain them.
- Always provide a length-col or get wrong results
- Files satisfying `pref` conditions will be preferred. Files not satisfying `nec` conditions will not be downloaded.  

Example use (with a csv from https://exportify.net/):
```
slsk-batchdl.exe --output "C:\Users\fiso64\Music\Playlists\test" --csv "C:\Users\fiso64\Downloads\test.csv" --username "fakename" --password "fakepass" --artist-col "Artist Name(s)" --track-col "Track Name" --length-col "Duration (ms)" --time-unit "ms" --skip-existing --create-m3u --pref-format "flac"
```

## Notes:
- The console output tends to break after a while
- Much of the code was written by ChatGPT
