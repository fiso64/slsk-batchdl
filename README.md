# slsk-batchdl

A batch downloader for Soulseek built with Soulseek.NET. Accepts CSV files or Spotify and YouTube urls.

## Examples

### Download tracks from a csv file:
```
slsk-batchdl test.csv
```  
The names of the columns in the csv should be: `Artist`, `Title`, `Album`, `Length`, though alternatives can sometimes be inferred as well. You can use `--print tracks` before downloading to check if everything has been parsed correctly. Only the title or album column is required, but additional info may improve search results.  
  
### Download spotify likes while skipping existing songs:
```
slsk-batchdl spotify-likes --skip-existing
```
To download private playlists or liked songs you will need to provide a client id and secret, which you can get here https://developer.spotify.com/dashboard/applications. Create an app and add `http://localhost:48721/callback` as a redirect url in its settings.  
  
### Download from youtube playlist (w. yt-dlp fallback), including deleted videos:
```
slsk-batchdl --get-deleted --yt-dlp "https://www.youtube.com/playlist?list=PLI_eFW8NAFzYAXZ5DrU6E6mQ_XfhaLBUX"
```
Playlists are retrieved using the YoutubeExplode library which unfortunately doesn't always return all videos. You can use the official API by providing a key with `--youtube-key`. Get it here https://console.cloud.google.com. Create a new project, click "Enable Api" and search for "youtube data", then follow the prompts.  

### Search & download a specific song:
```
slsk-batchdl "title=MC MENTAL @ HIS BEST,length=242" --pref-format "flac,wav"
```  

### Interactive album download:
```
slsk-batchdl "album=Some Album" --interactive
```  
  
### Find an artist's songs which are not in your library:
```
slsk-batchdl "artist=MC MENTAL" --aggregate --print tracks-full --skip-existing --music-dir "path\to\music"
```

## Download Modes

Depending on the provided input, the download behaviour changes:

- Normal download: When the song title is set (in the CSV row, or in the string input), the program will download a single file for every entry.
- Album download: When the album name is set and the song title is NOT set, the program will search for the album and download the entire folder.
- Aggregate download: With `--aggregate`, the program will first perform an ordinary search for the input, then attempt to group the results into distinct songs and download one of each kind. This can be used to download an artist's entire discography (or simply printing it, like in the example above).

## Options
```
Usage: slsk-batchdl <input> [OPTIONS]

  <input>                        <input> is one of the following:

                                 Spotify playlist url or 'spotify-likes': Download a spotify
                                 playlist or your liked songs. --spotify-id and
                                 --spotify-secret may be required in addition.

                                 Youtube playlist url: Download songs from a youtube playlist.
                                 Provide a --youtube-key to include unavailabe uploads.

                                 Path to a local CSV file: Use a csv file containing track
                                 info to download. The names of the columns should be Artist,
                                 Title, Album, Length. Only the title or album column is
                                 required, but extra info may improve search results.

                                 Name of the track, album, or artist to search for:
                                 Can either be any typical search string or a comma-separated
                                 list like 'title=Song Name,artist=Artist Name,length=215'
                                 Allowed properties are: title, artist, album, length (sec)
                                 Specify artist and album only to download an album.

Options:
  --user <username>              Soulseek username
  --pass <password>              Soulseek password

  -p --path <path>               Download folder
  -f --folder <name>             Subfolder name. Set to '.' to output directly to the
                                 download folder (default: playlist/csv name)
  -n --number <maxtracks>        Download the first n tracks of a playlist
  -o --offset <offset>           Skip a specified number of tracks
  -r --reverse                   Download tracks in reverse order
  --remove-from-playlist         Remove downloaded tracks from playlist (spotify only)
  --name-format <format>         Name format for downloaded tracks, e.g "{artist} - {title}"
  --fast-search                  Begin downloading as soon as a file satisfying the preferred
                                 conditions is found. Increases chance to download bad files.
  --m3u <option>                 Create an m3u8 playlist file
                                 'none': Do not create a playlist file
                                 'fails' (default): Write only failed downloads to the m3u
                                 'all': Write successes + fails as comments

  --spotify-id <id>              spotify client ID
  --spotify-secret <secret>      spotify client secret

  --youtube-key <key>            Youtube data API key
  --get-deleted                  Attempt to retrieve titles of deleted videos from wayback
                                 machine. Requires yt-dlp.

  --time-format <format>         Time format in Length column of the csv file (e.g h:m:s.ms
                                 for durations like 1:04:35.123). Default: s
  --yt-parse                     Enable if the csv file contains YouTube video titles and
                                 channel names; attempt to parse them into title and artist
                                 names.

  --format <format>              Accepted file format(s), comma-separated
  --length-tol <sec>             Length tolerance in seconds (default: 3)
  --min-bitrate <rate>           Minimum file bitrate
  --max-bitrate <rate>           Maximum file bitrate
  --min-samplerate <rate>        Minimum file sample rate
  --max-samplerate <rate>        Maximum file sample rate
  --min-bitdepth <depth>         Minimum bit depth
  --max-bitdepth <depth>         Maximum bit depth
  --strict-title                 Only download if filename contains track title
  --strict-artist                Only download if filepath contains track artist
  --banned-users <list>          Comma-separated list of users to ignore

  --pref-format <format>         Preferred file format(s), comma-separated (default: mp3)
  --pref-length-tol <sec>        Preferred length tolerance in seconds (default: 2)
  --pref-min-bitrate <rate>      Preferred minimum bitrate (default: 200)
  --pref-max-bitrate <rate>      Preferred maximum bitrate (default: 2200)
  --pref-min-samplerate <rate>   Preferred minimum sample rate
  --pref-max-samplerate <rate>   Preferred maximum sample rate (default: 96000)
  --pref-min-bitdepth <depth>    Preferred minimum bit depth
  --pref-max-bitdepth <depth>    Preferred maximum bit depth
  --pref-strict-artist           Prefer download if filepath contains track artist
  --pref-banned-users <list>     Comma-separated list of users to deprioritize
  --strict                       Skip files with missing properties instead of accepting by
                                 default; if --min-bitrate is set, ignores any files with
                                 unknown bitrate.

  -a --aggregate                 Instead of downloading a single track matching the input,
                                 find and download all distinct songs associated with the
                                 provided artist, album, or track title.
  --min-users-aggregate <num>    Minimum number of users sharing a track before it is
                                 downloaded in aggregate mode. Setting it to higher values
                                 will significantly reduce false positives, but may introduce
                                 false negatives. Default: 2
  --relax                        Slightly relax file filtering in aggregate mode to include
                                 more results

  --interactive                  When downloading albums: Allows to select the wanted album
  --album-track-count <num>      Specify the exact number of tracks in the album. Folders
                                 with a different number of tracks will be ignored. Append
                                 a '+' or '-' after the number for the inequalities >= and <=
  --album-ignore-fails           When downloading an album and one of the files fails, do not
                                 skip to the next source and do not delete all successfully
                                 downloaded files
  --album-art <option>           When downloading albums, optionally retrieve album images
                                 from another location:
                                 'default': Download from the same folder as the music
                                 'largest': Download from the folder with the largest image
                                 'most': Download from the folder containing the most images

  -s --skip-existing             Skip if a track matching file conditions is found in the
                                 output folder or your music library (if provided)
  --skip-mode <mode>             'name': Use only filenames to check if a track exists
                                 'name-precise' (default): Use filenames and check conditions
                                 'tag': Use file tags (slower)
                                 'tag-precise': Use file tags and check file conditions
  --music-dir <path>             Specify to skip downloading tracks found in a music library
                                 Use with --skip-existing
  --skip-not-found               Skip searching for tracks that weren't found on Soulseek
                                 during the last run. Fails are read from the m3u file.

  --no-remove-special-chars      Do not remove special characters before searching
  --remove-ft                    Remove 'feat.' and everything after before searching
  --remove-brackets              Remove square brackets and their contents before searching
  --regex <regex>                Remove a regexp from all track titles and artist names.
                                 Optionally specify the replacement regex after a semicolon
  --artist-maybe-wrong           Performs an additional search without the artist name.
                                 Useful for sources like SoundCloud where the "artist"
                                 could just be an uploader. Note that when downloading a
                                 YouTube playlist via url, this option is set automatically
                                 on a per track basis, so it is best kept off in that case.
  -d --desperate                 Tries harder to find the desired track by searching for the
                                 artist/album/title only, then filtering the results.
  --yt-dlp                       Use yt-dlp to download tracks that weren't found on
                                 Soulseek. yt-dlp must be available from the command line.

  --config <path>                Manually specify config file location
  --search-timeout <ms>          Max search time in ms (default: 5000)
  --max-stale-time <ms>          Max download time without progress in ms (default: 50000)
  --concurrent-downloads <num>   Max concurrent downloads (default: 2)
  --searches-per-time <num>      Max searches per time interval. Higher values may cause
                                 30-minute bans. (default: 34)
  --searches-renew-time <sec>    Controls how often available searches are replenished.
                                 Lower values may cause 30-minute bans. (default: 220)
  --display <option>             Changes how searches and downloads are displayed:
                                 'single' (default): Show transfer state and percentage
                                 'double': Transfer state and a large progress bar
                                 'simple': No download bars or changing percentages
  --listen-port <port>           Port for incoming connections (default: 50000)

  --print <option>               Print tracks or search results instead of downloading:
                                 'tracks': Print all tracks to be downloaded
                                 'tracks-full': Print extended information about all tracks
                                 'results': Print search results satisfying file conditions
                                 'results-full': Print search results including full paths
  --debug                        Print extra debug info
```
### File conditions:
Files not satisfying the conditions will not be downloaded. For example, `--length-tol` is set to 3 by default, meaning that files whose duration differs from the supplied duration by more than 3 seconds will not be downloaded (disable it by setting it to 99999).  
Files satisfying `pref-` conditions will be preferred. For example, setting `--pref-format "flac,wav"` will make it download high quality files if they exist and only download low quality files if there's nothing else.

### Name format:
Available tags are: artist, artists, album_artist, album_artists, title, album, year, track, disc, filename, default_foldername. Name format supports subdirectories as well as conditional expressions: `{str1|str2}` – If any tags in str1 are null, choose str2. String literals enclosed in parentheses are ignored in the null check.
```
{artist( - )title|album_artist( - )title|filename}
{album(/)}{track(. )}{artist|(unknown artist)} - {title|(unknown title)}
```

## Configuration  
Create a file named `slsk-batchdl.conf` in the same directory as the executable and write your arguments there, e.g:
```
--username "fakename"
--password "fakepass"
--pref-format "flac"
```  
  
## Notes
- For macOS builds you can use publish.sh to build the app. Download dotnet from https://dotnet.microsoft.com/en-us/download/dotnet/6.0, then run `chmod +x publish.sh && sh publish.sh`
- `--display single` and especially `double` can cause the printed lines to be duplicated or overwritten on some configurations. Use `simple` if that's an issue.
- The server will ban you for 30 minutes if too many searches are performed within a short timespan. Adjust `--searches-per-time` and `--searches-renew-time` in case it happens. By default it's configured to allow up to 34 searches every 220 seconds. These values were determined through experimentation as unfortunately I couldn't find any information regarding soulseek's rate limits, so they may be incorrect. You can also use `--random-login` to re-login with a random username and password automatically.
- An issue I've not been able to resolve is audio files not appearing in the search results, even though they exist in the shown folders. This happens in soulseek clients as well; search for "AD PIANO IV Monochrome". You will find a few users whose folders only contain non-audio files. However, when you browse their shares, you can see that they do have audio in those exact folders. If you know why this is happening, please open an issue.
