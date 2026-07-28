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

For application integration, see [API and client integration](api.md) and the
generated [OpenAPI document](openapi.json).

## Network security

The daemon API does not currently provide authentication.

The default `127.0.0.1` address accepts connections only from the same computer.
Binding to `0.0.0.0`, `::`, or another network address makes the API reachable
from that network. Only do this on a trusted network or when access is protected
by a VPN, firewall, or authenticated reverse proxy.

Anyone who can reach the API may be able to view download history and submit
work using the daemon's configured credentials.

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
