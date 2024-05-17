# slsk-batchdl

A batch downloader for Soulseek built with Soulseek.NET. Accepts CSV files or Spotify and YouTube urls.

## Examples

Download tracks from a csv file:
```
slsk-batchdl test.csv
```
<details>
  <summary>CSV details</summary>
  
The names of the columns in the csv should be: `Artist`, `Title`, `Album`, `Length`. Some alternatives are also accepted. You can use `--print tracks` before downloading to check if everything has been parsed correctly. Only the title or album column is required, but additional info may improve search results.  

</details> 

<br>

Download spotify likes while skipping songs that already exist in the output folder:
```
slsk-batchdl spotify-likes --skip-existing
```
<details>
  <summary>Spotify details</summary>

To download private playlists or liked songs you will need to provide a client id and secret, which you can get here https://developer.spotify.com/dashboard/applications. Create an app and add `http://localhost:48721/callback` as a redirect url in its settings.  

</details> 

<br>

Download from a youtube playlist with fallback to yt-dlp in case it is not found on soulseek, and retrieve deleted video titles from wayback machine:
```
slsk-batchdl --get-deleted --yt-dlp "https://www.youtube.com/playlist?list=PLI_eFW8NAFzYAXZ5DrU6E6mQ_XfhaLBUX"
```
<details>
  <summary>YouTube details</summary>

Playlists are retrieved using the YoutubeExplode library which unfortunately doesn't always return all videos. You can use the official API by providing a key with `--youtube-key`. Get it here https://console.cloud.google.com. Create a new project, click "Enable Api" and search for "youtube data", then follow the prompts.
Also note that due the high number of music videos in the above example playlist, it may be better to remove all text in parentheses and disable song duration checking: `--regex "[\[\(].*?[\]\)]" --length-tol -1 --pref-length-tol -1`.

</details> 

<br>

Search & download a specific song, preferring lossless:
```
slsk-batchdl "title=MC MENTAL @ HIS BEST,length=242" --pref-format "flac,wav"
```  

<br>

Interactive album download:
```
slsk-batchdl "album=Some Album" --interactive
```  
  
<br>

Print all songs by an artist which are not in your library:
```
slsk-batchdl "artist=MC MENTAL" --aggregate --print tracks-full --skip-existing --music-dir "path\to\music"
```

## Download Modes

Depending on the provided input, the download behaviour changes:

- Normal download: When the song title is set (in the CSV row, or in the string input), the program will download a single file for every entry.
- Album download: When the album name is set and the song title is NOT set, the program will search for the album and download the entire folder.
- Aggregate download: With `--aggregate`, the program will first perform an ordinary search for the input, then attempt to group the results into distinct songs and download one of each kind. This can be used to download an artist's entire discography (or simply printing it, like in the example above), finding remixes of a song, etc. Note that it is not 100% reliable, which is why `--min-users-aggregate` is set to 2 by default, i.e. any song that is shared by only one person will be ignored. Enabling `--relax` will give even more results.

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
  --nf --name-format <format>    Name format for downloaded tracks, e.g "{artist} - {title}"
  --fs --fast-search             Begin downloading as soon as a file satisfying the preferred
                                 conditions is found. Increases chance to download bad files.
  --m3u <option>                 Create an m3u8 playlist file
                                 'none': Do not create a playlist file
                                 'fails' (default): Write only failed downloads to the m3u
                                 'all': Write successes + fails as comments

  --spotify-id <id>              spotify client ID
  --spotify-secret <secret>      spotify client secret
  --remove-from-playlist         Remove downloaded tracks from playlist (spotify only)

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
 --album-art-only                Only download album art for the provided album

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
                                 artist/album/title only, then filtering. (slower search)
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
### File conditions
Files not satisfying the conditions will not be downloaded. For example, `--length-tol` is set to 3 by default, meaning that files whose duration differs from the supplied duration by more than 3 seconds will not be downloaded (can be disabled by setting it to -1).  
Files satisfying `pref-` conditions will be preferred; setting `--pref-format "flac,wav"` will make it download high quality files if they exist, and only download low quality files if there's nothing else. Conditions can also be supplied as a semicolon-delimited string to `--cond` and `--pref`, e.g `--cond "br>=320;f=mp3,ogg;sr<96000"`.\

**Important note**: Some info may be unavailable depending on the client used by the peer. For example, the default Soulseek client does not share the file bitrate. By default, if `--min-bitrate` is set, then files with unknown bitrate will still be downloaded. You can configure it to reject all files where one of the checked properties is unavailable by enabling `--strict`. (As a consequence, if `--strict` and `--min-bitrate` is set then any files shared by users with the default client will be ignored)

### Name format
Available tags are: artist, artists, album_artist, album_artists, title, album, year, track, disc, filename, default_foldername. Name format supports subdirectories as well as conditional expressions: `{str1|str2}` – If any tags in str1 are null, choose str2. String literals enclosed in parentheses are ignored in the null check.
```
{artist( - )title|album_artist( - )title|filename}
```
```
{album(/)}{track(. )}{artist|(unknown artist)} - {title|(unknown title)}
```
Here `{album(/)}` will conditionally put the download into a subfolder; if the album tag is null, then the slash won't be added to the path. An alternative is `{album|(missing album)}/` which will save all songs with unknown album under `missing album`.

### Speed vs Quality
The following options will make it go faster, but may decrease search result quality or cause instability:
- `--fast-search` skips waiting until the search completes and downloads as soon as a matching file is found
- `--concurrent-downloads` -  set it to 4 or more
- `--max-stale-time` is set to 50 seconds by default, so it will wait a long time before giving up on a file
- `--searches-per-time` increase at the risk of ban, see the notes section for details.

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
- The server will ban you for 30 minutes if too many searches are performed within a short timespan. The program has a search limiter which can be adjusted with `--searches-per-time` and `--searches-renew-time` (when limit is reached, the status of the downloads will be "Waiting"). By default it is configured to allow up to 34 searches every 220 seconds. These values were determined through experimentation as unfortunately I couldn't find any information regarding soulseek's rate limits, so they may be incorrect.
