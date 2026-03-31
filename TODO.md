
## TODO

- Downloads always fail in the trimmed build (long Soulseek.NET error). A change between commit 88336b8c567d8f3778839a01dce980b7accfc450 and abe86e906257d9ce18d46983b161c4c01c8ef600 broke it, most likely the change from net 6 to 8. Expected it to work after excluding Soulseek.NET from trimming. Look for a way to restore old net 6 trimming logic (the new aggressive trim does not appear to save much more space, but breaks many things).

- Bug: Album downloads go stale and cancel when there are multiple files downloading from the same user and one triggers the stale timeout. To repro:
    1. Find an album share by a user whose client is configured to allow multiple simultaneous uploads to the same user
    2. Download the album. The following must now happen for the bug to occur:
    3. File A from the folder starts downloading, then stops at incomplete%. 
    4. File B starts downloading while file A is still stopped at incomplete%.
    5. After some time, file A's stale timer will trigger the whole album to go stale and get cancelled, even though file B is still downloading.

- Add {outputdir} variable

- Replace the --failed-album-path option by a new option called --album-fail-action. Can be
    - ""/"default" - move all album files to {configured output dir}/failed when not in interactive mode. In interactive mode, ask what to do, with the same default action.
    - "move:{path, with possible {} variables}" - move to specified path. 
    - "delete" - delete the downloaded files
    - "keep" - do nothing, keep files where they are
    - "ask" - Ask what to do: Can be delete, keep, move, or retry. If move is selected ask for the path in a second prompt. Retry will reattempt to download the incomplete files.

- Make album download mode the default, add -s/--song flag. Don't forget to update it for lists as well (add s: prefix). Also explain that the previous default behavior (default to song search, album with -a) can be restored by adding `song = true` to the config (ensure this works).

- Why do all active downloads always go stale after disconnecting and reconnecting?

- Improve reconnection logic (more than 3 attempts, increasing delay)

- Skip retrieve full folder contents whenever it's already guaranteed to contain all files (e.g. when it was `cd`'d into).

## Advanced TODO

- Big refactor. The refactor should be done with the below todos in mind, and (in particular) make them much easier to implement. Here is everything wrong with the current code:
    1. DownloaderApplication.cs is a god class that does too much. Some functions (e.g. MainLoop) are also way too long and unreadable. Global variables are bad.
    2. Searcher.SearchAndDownload is too long, messy, and unreadable. 
    3. State managment with `searches`, `downloads`, `downloadedFiles`, etc. needs rethinking. The way downloads are marked as stale and cancelled might also need rethinking.
    4. Data models (`TrackListEntry`, `Track`, etc.) are poorly designed. Rethink from scratch.
    5. Config class needs to be refactored. Adding new flags is annoying. Will probably have to write a custom parser library as existing ones on nuget are unlikely to support all features of the current code. Use [Attributes]. Might also want to split config into several subclasses (search config, youtube config, etc.) (optional).

- Make it usable as a library/backend for GUIs.

- fast-search for albums

- Interactive mode for individual songs

- Parallel album downloads. The tool should be able to download both single songs and albums in parallel together (right now, song download lists and album downloads are processed separately and sequentially).

- In interactive mode, show search results (for albums or individual files) immediately as soon as they arrive instead of waiting for the search to complete. Sort every time before showing the updated results. Show a loading indicator while the search is in progress. When the user has e.g. some result selected, updates should be handled cleanly:
    - If a new result arrives that will be sorted before the currently selected result, set the selected result index to the minimal index of the new results after updating.
    - If all new results are to be sorted AFTER the currently selected result, there is no need to change the currently selected index. 
    E.g.: When the current selected index is 5 and a new result arrives: If after sorting the new result has index <= 5, set the selection to its index. If the new result has index > 5, keep the current selection index at 5. 

- Fix printing issues in windows terminal (https://github.com/fiso64/slsk-batchdl/issues/55)
