## TODO

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

- In interactive mode, show search results (for albums or individual files) immediately as soon as they arrive instead of waiting for the search to complete. Sort every time before showing the updated results. Show a loading indicator while the search is in progress. When the user has e.g. some result selected, updates should be handled cleanly:
    - If a new result arrives that will be sorted before the currently selected result, set the selected result index to the minimal index of the new results after updating.
    - If all new results are to be sorted AFTER the currently selected result, there is no need to change the currently selected index. 
    E.g.: When the current selected index is 5 and a new result arrives: If after sorting the new result has index <= 5, set the selection to its index. If the new result has index > 5, keep the current selection index at 5. 

- Fix printing issues in windows terminal (https://github.com/fiso64/slsk-batchdl/issues/55)
