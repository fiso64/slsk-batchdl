# slsk-batchdl

A batch downloader for Soulseek using Soulseek.NET. Accepts CSV files and Spotify or YouTube urls.

#### Download tracks from a csv file:
```
slsk-batchdl -i test.csv
```  
Use `--print tracks` before downloading to check if everything has been parsed correctly. The names of the columns should be: `Artist`, `Title`, `Album`, `Length`. Only the title column is required, but any additional info improves search.

#### Download spotify likes while skipping existing songs:
```
slsk-batchdl -i spotify-likes --skip-existing
```
To download private playlists or liked songs you will need to provide a client id and secret, which you can get here https://developer.spotify.com/dashboard/applications. Create an app and add `http://localhost:48721/callback` as a redirect url in its settings.  
  
#### Download youtube playlist (with fallback to yt-dlp), including deleted videos:
```
slsk-batchdl --get-deleted --yt-dlp -i "https://www.youtube.com/playlist?list=PLI_eFW8NAFzYAXZ5DrU6E6mQ_XfhaLBUX"
```
Playlists are retrieved using the YoutubeExplode library which unfortunately doesn't always return all videos. You can use the official API by providing a key with `--youtube-key`. Get it here https://console.cloud.google.com. Create a new project, click "Enable Api" and search for "youtube data", then follow the prompts.  

#### Search & download a specific song:
```
slsk-batchdl -i "title=MC MENTAL @ HIS BEST,length=242" --pref-format "flac,wav"
```  
  
#### Find an artist's songs which aren't in your library:
```
slsk-batchdl -i "artist=MC MENTAL" --aggregate --print tracks --skip-existing --music-dir "path\to\music"
```

### Options:
```
Usage: slsk-batchdl -i <input> [OPTIONS]

  -i --input <input>             <input> is one of the following:

                                 Spotify playlist url or "spotify-likes": Download a spotify
                                 playlist or your liked songs. --spotify-id and
                                 --spotify-secret may be required in addition.

                                 Youtube playlist url: Download songs from a youtube playlist.
                                 Provide a --youtube-key to include unavailabe uploads.

                                 Path to a local CSV file: Use a csv file containing track
                                 info to download. The names of the columns should be Artist,
                                 Title, Album, Length. Only the title column is required, but
                                 any extra info improves search results.

                                 Name of the track, album, or artist to search for:
                                 Can either be any typical search string or a comma-separated
                                 list like "title=Song Name,artist=Artist Name,length=215"
                                 Allowed properties are: title, artist, album, length (sec)

Options:
  --user <username>              Soulseek username
  --pass <password>              Soulseek password

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

  -a --aggregate                 When input is a string: Instead of downloading a single
                                 track matching the search string, find and download all
                                 distinct songs associated with the provided artist, album,
                                 or track title. Input string must be a list of properties.
  --min-users-aggregate <num>    Minimum number of users sharing a track before it is
                                 downloaded in aggregate mode. Setting it to higher values
                                 will significantly reduce false positives, but may introduce
                                 false negatives. Default: 2

  -p --path <path>               Download folder
  -f --folder <name>             Subfolder name (default: playlist/csv name)
  -n --number <maxtracks>        Download the first n tracks of a playlist
  -o --offset <offset>           Skip a specified number of tracks
  --reverse                      Download tracks in reverse order
  --remove-from-playlist         Remove downloaded tracks from playlist (for spotify only)
  --name-format <format>         Name format for downloaded tracks, e.g "{artist} - {title}"
  --m3u                          Create an m3u8 playlist file

  --format <format>              Accepted file format(s), comma-separated
  --length-tol <tol>             Length tolerance in seconds (default: 3)
  --min-bitrate <rate>           Minimum file bitrate
  --max-bitrate <rate>           Maximum file bitrate
  --max-samplerate <rate>        Maximum file sample rate
  --strict-title                 Only download if filename contains track title
  --strict-artist                Only download if filepath contains track artist
  --banned-users <list>          Comma-separated list of users to ignore
  --danger-words <list>          Comma-separated list of words that must appear in either
                                 both search result and track title or in neither of the
                                 two. Case-insensitive. (default:"remix, edit,cover")
  --pref-format <format>         Preferred file format(s), comma-separated (default: mp3)
  --pref-length-tol <tol>        Preferred length tolerance in seconds (default: 2)
  --pref-min-bitrate <rate>      Preferred minimum bitrate (default: 200)
  --pref-max-bitrate <rate>      Preferred maximum bitrate (default: 2200)
  --pref-max-samplerate <rate>   Preferred maximum sample rate (default: 96000)
  --pref-strict-artist           Prefer download if filepath contains track artist
  --pref-banned-users <list>     Comma-separated list of users to deprioritize
  --pref-danger-words <list>     Comma-separated list of words that should appear in either
                                 both search result and track title or in neither of the
                                 two. (default: see github)

  -s --skip-existing             Skip if a track matching file conditions is found in the
                                 output folder or your music library (if provided)
  --skip-mode <mode>             name: Use only filenames to check if a track exists
                                 name-precise (default): Use filenames and check conditions
                                 tag: Use file tags (slower)
                                 tag-precise: Use file tags and check file conditions
  --music-dir <path>             Specify to skip downloading tracks found in a music library
                                 Use with --skip-existing
  --skip-not-found               Skip searching for tracks that weren't found on Soulseek
                                 during the last run.
  --remove-ft                    Remove "ft." or "feat." and everything after from the
                                 track names before searching
  --remove-regex <regex>         Remove a regex from all track names and artist names
  --no-artist-search             Perform a search without artist name if nothing was
                                 found. Only use for sources such as youtube or soundcloud
                                 where the "artist" could just be an uploader.
  --artist-search                Also try to find track by searching for the artist only
  --no-diacr-search              Also perform a search without diacritics
  --no-regex-search <regex>      Also perform a search without a regex pattern
  --yt-dlp                       Use yt-dlp to download tracks that weren't found on
                                 Soulseek. yt-dlp must be available from the command line.

  --config <path>                Specify config file location
  --search-timeout <ms>          Max search time in ms (default: 6000)
  --max-stale-time <ms>          Max download time without progress in ms (default: 50000)
  --concurrent-downloads <num>   Max concurrent searches & downloads (default: 2)
  --display <option>             Changes how searches and downloads are displayed:
                                 single (default): Show transfer state and percentage
                                 double: Transfer state and a large progress bar
                                 simple: No download bars or changing percentages

  --print <option>               Print tracks or search results instead of downloading:
                                 tracks: Print all tracks to be downloaded
                                 tracks-full: Print extended information about all tracks
                                 results: Print search results satisfying file conditions
                                 results-full: Print search results including full paths
```
Files not satisfying the conditions will not be downloaded. For example, `--length-tol` is set to 3 by default, meaning that files whose duration differs from the supplied duration by more than 3 seconds will not be downloaded (disable it by setting it to 99999).  
Files satisfying `pref-` conditions will be preferred. For example, setting `--pref-format "flac,wav"` will make it download high quality files if they exist and only download low quality files if there's nothing else.
  
Configuration files: Create a file named `slsk-batchdl.conf` in the same directory as the executable and write your arguments there, e.g:
```
--username "fakename"
--password "fakepass"
--pref-format "flac"
```  
  
### Notes:
- The CSV file must use `"` as string delimiter and be encoded with UTF8
- `--display single` and especially `double` can cause the printed lines to be duplicated or overwritten on some configurations. Use `simple` if that's an issue.
