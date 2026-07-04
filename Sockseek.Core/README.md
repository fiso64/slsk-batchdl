# Sockseek.Core Structure

Sockseek.Core is organized by product capability first. Put code where a future
reader would look for the feature, not in a broad "services" or "engine" bucket.

## Current Areas

- `Transfers/Downloads/` contains download transfer behavior and the smart
  download workflow: `DownloadEngine`, job routing, discovery, manual selection,
  output completion, per-run download state, source mutation, and download
  fallback handling.
- `Transfers/Downloads/DownloadFallback/` contains optional non-Soulseek download
  fallback paths, currently the yt-dlp integration.
- `Transfers/Downloads/StaleDetection/` contains stale peer-transfer detection
  and the public stale-download failure shape.
- `Transfers/` is the home for Soulseek transfer capabilities. Add future upload
  queueing, slot management, and upload policy under `Transfers/Uploads/`.
- `Search/` contains Soulseek search execution and projection/sorting of results.
- `Search/Queries/` and `Search/Results/` contain search-domain data shapes
  that jobs, extractors, and download workflows can reuse.
- `Files/` contains reusable local filesystem placement, playlist/index editing,
  preprocessing, and file-template helpers. Keep download workflow state in
  `Transfers/Downloads/`.
- `Matching/` contains reusable condition parsing and quality/matching policy.
- `Matching/Conditions/` contains condition and local/search file metadata shapes.
- `Jobs/Policies/` and `Jobs/Provenance/` contain job-owned policy/provenance
  shapes used by orchestration and external adapters.
- `Soulseek/` contains connection/client management around Soulseek.NET.
- `Jobs/` and `Settings/` contain shared job objects and configuration shapes.
- `Common/` and `Diagnostics/` contain small cross-cutting utilities.

## Future Areas

Use top-level capability folders for full-client features:

- `Shares/` for share scanning, share cache/indexing, browse responses, and
  upload eligibility.
- `Messaging/` for private messages, rooms, and chat state.
- `Users/` for user lists, profiles, browse requests, and user-level actions.

## Dependency Rule

Feature folders may depend on shared domain folders such as `Jobs`, `Settings`,
`Common`, `Diagnostics`, `Files`, and `Soulseek`, and on explicitly named domain
folders such as `Search/Queries`, `Search/Results`, and `Matching/Conditions`.
Avoid dependencies from shared folders back into feature orchestration. If two
features need the same logic, move that logic to a named shared domain folder
instead of reaching sideways.
Avoid generic registries for mixed session state; split state by ownership so
shared code never has to depend on a feature folder just to read one concern.
Do not add a top-level `Models` folder; place data shapes beside the domain that
owns their meaning.

## Namespace Compatibility

Some relocated public data shapes still use the legacy `Sockseek.Core.Models`
namespace for source compatibility. New internal types should use the namespace
of their owning feature folder. Rename public namespaces only in a deliberate
compatibility pass.
