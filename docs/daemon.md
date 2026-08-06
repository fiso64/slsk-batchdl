# Daemon mode

> [!WARNING]
> This document is currently fully AI-authored.

Daemon mode keeps Sockseek running in the background so it can accept work from
other terminals or applications. It also keeps job, search, and transfer history
across restarts.

Use daemon mode when you want to:

- submit downloads from another terminal or machine;
- keep one long-running connection to Soulseek;
- view previous jobs and search results;
- use Sockseek through its HTTP API or .NET client.

## Start the daemon

The daemon listens on `127.0.0.1:5030` by default:

```bash
sockseek daemon
```

Leave this terminal open, or run the process through your preferred service
manager.

To use another port:

```bash
sockseek daemon --server-port 8080
```

To listen on every network interface:

```bash
sockseek daemon --server-ip 0.0.0.0
```

The daemon uses the same Soulseek credentials, download settings, config file,
and profiles as the regular CLI.

Run `sockseek daemon --help` for a short overview. The complete option reference,
including database and retention settings, is available through:

```bash
sockseek --help
```

## Submit work remotely

Use `--remote` to send a normal Sockseek command to a running daemon:

```bash
sockseek "Artist - Title" --remote http://127.0.0.1:5030
```

Most download options can be used in the same command:

```bash
sockseek "Artist - Album" \
  --remote http://127.0.0.1:5030 \
  --album \
  --format flac
```

The daemon performs the work. Closing the remote CLI does not stop the daemon.

## Monitor daemon work

Use `--monitor` to follow every active workflow in the normal live renderer.
Input is optional; when supplied, its workflow appears alongside other daemon
work:

```bash
sockseek --remote http://127.0.0.1:5030 --monitor
sockseek "Artist - Title" --remote http://127.0.0.1:5030 --monitor
```

The normal `c`, `t`, and `i` shortcuts work daemon-wide by display ID. Choosing
all from the cancel prompt cancels active jobs without stopping the daemon.

## Sharing and transfer commands

Sharing, scans, and uploads belong to the running daemon. These commands require
`--remote`; they never start a temporary local daemon:

```bash
sockseek share status --remote http://127.0.0.1:5030
sockseek share scan --remote http://127.0.0.1:5030
sockseek share scan --cancel --remote http://127.0.0.1:5030
sockseek transfers --direction upload --remote http://127.0.0.1:5030
sockseek transfer cancel <id> --remote http://127.0.0.1:5030
```

`share status` reports the sharing health, published generation, public aliases,
aggregate catalog counts, recent scan state, and blocked-peer counts
without exposing local roots or blacklist contents. `transfers` is a paginated
durable-history query; use `--limit`, `--cursor`, `--state`, `--username`, and
`--direction` to narrow it. Add `--json` to these commands for typed JSON
output. Scan and transfer cancellation use the same advertised actions and HTTP
contracts as other API clients.

### Configure sharing and uploads

Sharing is off until at least one root is configured. A root may use its final
directory name as the public alias, or specify an alias explicitly:

```ini
share = /srv/music
share = + [Archive]/mnt/archive/audio
share-exclude = + /srv/music/private
share-filter = + \.(part|tmp)$

share-scan-on-start = true
# share-rescan-interval = 6h

upload-slots = 10
# upload-speed-limit-kib = 2048

peer-blocked-user = + unwanted-user
peer-blocked-ip = + 192.0.2.10
```

The leading `+` appends another value; an unprefixed list value replaces values
from an earlier configuration layer. Public aliases and relative remote paths
are visible to peers. Local roots and the contents of peer deny lists are not
returned by ordinary status APIs.

Scans skip Windows `Hidden` or `System` entries, Unix dotfiles and
dot-directories, and every symbolic link or reparse point. A hidden directory's
entire subtree is skipped. Attribute or entry-read failures skip that entry;
they do not invalidate the filesystem. Zero-byte files are indexed and served.
This hidden-file policy is fixed in v4 rather than configurable. A failed or
cancelled rescan leaves the previous complete generation active.

The initial scan runs in the background: daemon HTTP and ordinary download work
start without waiting for a large library. Until the first generation publishes,
sharing status reports the catalog unavailable and Sockseek advertises no
shares. When a prior valid generation exists, it remains searchable, browsable,
and upload-resolvable while the replacement is built.

The catalog and browse artifact live under the daemon data directory, separately
from durable history. A disk-full or write failure removes the incomplete
staging generation and leaves the previously published generation active.
Sockseek retains the current and rollback generations; an older generation is
deleted only after its outstanding request streams release it.
The dedicated sharing directory and its manifest, SQLite generations, and browse
artifacts are restricted to the daemon account (protected owner-only ACLs on
Windows and `0700`/`0600` modes on Unix).

### Sharing filesystem behavior

Sockseek does not maintain a filesystem allowlist. A configured root is eligible
when the daemon account can enumerate and open it through .NET, including common
NAS mounts, ZFS/Btrfs, Docker bind mounts, and FUSE-backed storage. A volume root
such as `/` or `C:\` requires an explicit public alias. Local roots may overlap
when their aliases are distinct.

Peer paths are resolved only through the published catalog, checked for canonical
containment, and never concatenated into an unchecked filesystem probe. Symbolic
links and reparse points are excluded. Immediately before an upload, Sockseek
opens the file read-only and validates its current size and modification time;
native stable-file identity is optional hardening rather than a platform gate.
Individual inaccessible or changed files fail without disabling the whole root.

The catalog database itself should still live on reliable local storage under
the daemon data directory. That operational recommendation is independent of
where shared media is mounted.

For application integration, see [API and client integration](api.md) and the
generated [OpenAPI document](openapi.json).

## Network security

The daemon API does not currently provide authentication.

The default `127.0.0.1` address accepts connections only from the same computer.
Binding to `0.0.0.0`, `::`, or another network address makes the API reachable
from that network. Only do this on a trusted network or when access is protected
by a VPN, firewall, or authenticated reverse proxy.

Anyone who can reach the API can use its current pass-through operator trust
domain: they may view history, submit work, start/cancel share scans, and cancel
eligible jobs or uploads using the daemon's configured credentials. The
`Sockseek.Operator` endpoint marker is an integration seam for the planned v4
authentication work; it is not access control today.

## History and restarts

Daemon mode retains jobs, workflows, search results, downloads, and download
attempts. This makes it possible to inspect earlier activity or start a new
download from a retained search result after restarting Sockseek.

Historical jobs are not resumed automatically. Starting a download from an old
search result creates a new job. Commands such as cancel or manual selection
only apply to work that is currently running.

Stop the daemon with `Ctrl+C` when possible. Sockseek then finishes saving
pending history before exiting. After a crash or forced shutdown, the most
recent history may be incomplete and unfinished work is shown as interrupted.

Search history may be labelled:

- `Complete` when all results were saved;
- `Incomplete` when some results could not be retained;
- `Pruned` when old raw results were removed by retention;
- `Interrupted` when the daemon stopped before the search finished.

## Database and privacy

Sockseek stores daemon history in `sockseek.db` inside its data directory. The
default data directory depends on the operating system:

| System | Default data directory |
| --- | --- |
| Linux | `${XDG_DATA_HOME:-$HOME/.local/share}/sockseek` |
| Windows | `%LOCALAPPDATA%\sockseek` |
| macOS | `~/Library/Application Support/sockseek` |

Set a different directory with:

```bash
sockseek daemon --data-dir /path/to/sockseek-data
```

The setting can also be kept in `sockseek.conf`:

```ini
data-dir = /path/to/sockseek-data
```

Sockseek chooses the database filename inside that directory. You do not need to
configure a database file separately. For a container, mount a persistent
directory and set `data-dir` to that mount, such as `/data`.

Keep the database on a local disk. Network shares and cloud-synchronized folders
are not supported database locations.

The database and its backups may contain:

- peer usernames;
- search queries;
- remote and local file paths;
- job and transfer outcomes;
- error messages.

Treat these files as private download history. On Windows, custom paths inherit
their folder permissions. On Unix, Sockseek restricts newly created database and
backup files to the current user.

Only one daemon or database command can use a database at a time.

## Retention

Sockseek periodically removes old history so the database does not grow forever.
The defaults are:

| History | Retained for |
| --- | ---: |
| Successful jobs | 90 days |
| Failed, cancelled, or interrupted jobs | 180 days |
| Search results | 30 days |
| Transfers | 90 days |

The daemon also keeps at most 100,000 completed jobs by default. Active work is
never removed.

Each type of history can be configured independently:

```ini
successful-job-retention-days = 90
unsuccessful-job-retention-days = 180
transfer-retention-days = 90
search-result-retention-days = 30
```

Use `--no-retention` to disable automatic cleanup. Retention periods also accept
`forever`, for example:

```bash
sockseek daemon --search-result-retention-days forever
```

Setting either job retention period to `forever` also removes the default
100,000-job limit.

Retention does not delete backup files.

## Backup and restore

Do not copy the live database file directly while the daemon is running. Use
Sockseek's backup command or maintenance API so the backup is complete and
verified.

For offline maintenance, stop the daemon first.

Create a backup:

```bash
sockseek database backup --backup /safe/location/sockseek-backup.db
```

Check a database:

```bash
sockseek database integrity
```

Restore a backup:

```bash
sockseek database restore --backup /safe/location/sockseek-backup.db
```

These commands read `data-dir` from the normal Sockseek config. Use
`--data-dir /path/to/sockseek-data` to operate on another data directory, or
`--config /path/to/sockseek.conf` to select a particular configuration.

Sockseek verifies backups and restored databases before reporting success. Keep
at least one backup outside the data directory and test restoring it
occasionally.

Database upgrades are normally applied when the daemon starts. To apply them
while the daemon is stopped:

```bash
sockseek database migrate
```

## Status and troubleshooting

Daemon status, including database health, is available from:

```text
GET /api/server/status
```

Common problems:

- **The address or port is already in use:** stop the other service or select a
  different `--server-port`.
- **The database is already in use:** stop the other daemon or maintenance
  command that owns the same database.
- **Remote commands cannot connect:** confirm the daemon address, port, firewall,
  and `--remote` URL.
- **History reports a database error:** active downloads can continue, but new
  history may not be saved. Check the daemon log and run an integrity check after
  stopping the daemon.
- **Startup reports an integrity or upgrade error:** do not delete the database.
  Make a copy, run the offline integrity command, and restore a known-good backup
  if necessary.
