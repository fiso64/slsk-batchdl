<script lang="ts">
  import Icon from '../Icon.svelte';
  import SearchConfigPanel from '../SearchConfigPanel.svelte';
  import JobPreviewTree from './JobPreviewTree.svelte';
  import {
    createPrototypeSearchConditions,
    toSearchSettingsPatchForFamily,
    type PrototypeSearchConditions,
    type SearchConditionFamily,
  } from '../../prototype/search-config';
  import {
    commitPreview,
    emptyNewJobDraft,
    previewDirectJob,
    previewLeafRefs,
    previewSource,
    previewUploadedFile,
    type AutomaticJobRecord,
    type InlineExtractSourceType,
    type JobPreviewPlan,
    type NewJobChoice,
    type NewJobDraft,
  } from '../../prototype/jobs';
  import type { ProposedInputArtifactUploadDto, ProposedSubmitJobPreviewRequestDto } from '../../prototype/backend-contracts';

  interface Props {
    onclose: () => void;
    onstart: (records: AutomaticJobRecord[], rootId: string) => void;
  }

  let { onclose, onstart }: Props = $props();

  let draft = $state<NewJobDraft>({ ...emptyNewJobDraft, artist: 'Autechre', title: 'Gantz Graf' });
  let preview = $state<JobPreviewPlan | null>(null);
  let selectedLeaves = $state<Set<string>>(new Set());
  let optionsOpen = $state(false);
  let uploadActive = $state(false);
  let csvMappingOpen = $state(false);
  let trackConditions = $state<PrototypeSearchConditions>(createPrototypeSearchConditions('track'));
  let albumConditions = $state<PrototypeSearchConditions>(createPrototypeSearchConditions('album'));
  let mixedConditions = $state<PrototypeSearchConditions>(createPrototypeSearchConditions('track'));

  let selectedCount = $derived(selectedLeaves.size);
  let csvMappingCustomized = $derived(Object.values(draft.csvColumns).some((value) => value.trim().length > 0));
  let valid = $derived(
    draft.choice === 'song' ? Boolean(draft.title.trim())
      : draft.choice === 'album' ? Boolean(draft.album.trim())
        : draft.choice === 'csv' || draft.choice === 'list' ? Boolean(draft.uploadedFileName)
          : draft.choice === 'spotify' ? draft.spotifyInput !== 'url' || Boolean(draft.source.trim())
            : Boolean(draft.source.trim()),
  );

  const automaticChoices: Array<{ value: NewJobChoice; label: string; detail: string; icon: 'track' | 'album' }> = [
    { value: 'song', label: 'Song', detail: 'Find and download one song', icon: 'track' },
    { value: 'album', label: 'Album', detail: 'Find and download one album', icon: 'album' },
  ];
  const sourceChoices: Array<{ value: InlineExtractSourceType; label: string; detail: string; icon: 'spotify' | 'youtube' | 'bandcamp' | 'musicbrainz' | 'soulseek' }> = [
    { value: 'spotify', label: 'Spotify', detail: 'Playlist, album, or saved library', icon: 'spotify' },
    { value: 'youtube', label: 'YouTube', detail: 'Playlist URL', icon: 'youtube' },
    { value: 'bandcamp', label: 'Bandcamp', detail: 'Track, album, wishlist, or artist URL', icon: 'bandcamp' },
    { value: 'musicbrainz', label: 'MusicBrainz', detail: 'Release, release group, or collection URL', icon: 'musicbrainz' },
    { value: 'soulseek', label: 'Soulseek', detail: 'File or directory slsk:// link', icon: 'soulseek' },
  ];
  const fileChoices: Array<{ value: 'csv' | 'list'; label: string; detail: string; icon: 'upload-file' }> = [
    { value: 'csv', label: 'CSV file', detail: 'CSV of songs and albums', icon: 'upload-file' },
    { value: 'list', label: 'List file', detail: 'List of nested sources', icon: 'upload-file' },
  ];
  let choiceMetadata = $derived([...automaticChoices, ...sourceChoices, ...fileChoices].find((choice) => choice.value === draft.choice) ?? automaticChoices[0]!);

  function isInlineSourceChoice(value: NewJobChoice): value is InlineExtractSourceType {
    return value !== 'song' && value !== 'album' && value !== 'csv' && value !== 'list';
  }

  function sourcePreset(sourceType: InlineExtractSourceType): string {
    switch (sourceType) {
      case 'spotify': return 'https://open.spotify.com/playlist/37i9dQZEVXcExample';
      case 'youtube': return 'https://www.youtube.com/playlist?list=PLExample';
      case 'bandcamp': return 'https://artist.bandcamp.com/album/example';
      case 'musicbrainz': return 'https://musicbrainz.org/release-group/example';
      case 'soulseek': return 'slsk://nightshift/Jazz/Casiopea/Mint Jams';
    }
  }

  function sourcePlaceholder(sourceType: InlineExtractSourceType): string {
    switch (sourceType) {
      case 'spotify': return 'Paste a Spotify URL…';
      case 'youtube': return 'Paste a YouTube URL…';
      case 'bandcamp': return 'Paste a Bandcamp URL…';
      case 'musicbrainz': return 'Paste a MusicBrainz URL…';
      case 'soulseek': return 'Paste a slsk:// link…';
    }
  }

  function clearPreview(): void {
    preview = null;
    selectedLeaves = new Set();
  }

  function choose(value: NewJobChoice): void {
    draft.choice = value;
    optionsOpen = false;
    clearPreview();
    draft.uploadedFileName = '';
    csvMappingOpen = false;
    draft.uploadedFileType = '';
    if (value === 'song') Object.assign(draft, { artist: 'Autechre', title: 'Gantz Graf', album: '' });
    else if (value === 'album') Object.assign(draft, { artist: 'Nujabes', album: 'Modal Soul', title: '' });
    else if (isInlineSourceChoice(value)) {
      if (value === 'spotify') draft.spotifyInput = 'url';
      draft.source = sourcePreset(value);
    }
  }

  function setField(field: 'artist' | 'title' | 'album' | 'source', value: string): void {
    draft[field] = value;
    clearPreview();
  }

  function setSpotifyInput(value: NewJobDraft['spotifyInput']): void {
    draft.spotifyInput = value;
    clearPreview();
  }

  function setCsvColumn(field: keyof NewJobDraft['csvColumns'], value: string): void {
    draft.csvColumns[field] = value;
    clearPreview();
  }

  function resolvedSourceInput(): string {
    if (draft.choice !== 'spotify') return draft.source;
    if (draft.spotifyInput === 'likes') return 'spotify-likes';
    if (draft.spotifyInput === 'albums') return 'spotify-albums';
    return draft.source;
  }

  function currentFamily(): SearchConditionFamily {
    if (draft.choice === 'song') return 'track';
    if (draft.choice === 'album') return 'album';
    return 'mixed';
  }

  function currentConditions(): PrototypeSearchConditions {
    if (draft.choice === 'song') return trackConditions;
    if (draft.choice === 'album') return albumConditions;
    return mixedConditions;
  }

  function currentDownloadSettings() {
    const settings = { search: toSearchSettingsPatchForFamily(currentFamily(), currentConditions()) } as {
      search: ReturnType<typeof toSearchSettingsPatchForFamily>;
      csv?: {
        artistCol: string;
        albumCol: string;
        titleCol: string;
        lengthCol: string;
        descCol: string;
        ytIdCol: string;
        trackCountCol: string;
      };
    };
    if (draft.choice === 'csv') settings.csv = { ...draft.csvColumns };
    return settings;
  }

  function buildPlan(): JobPreviewPlan {
    if (isInlineSourceChoice(draft.choice)) return previewSource(resolvedSourceInput(), draft.choice);
    if (draft.choice === 'csv' || draft.choice === 'list') return previewUploadedFile(draft.uploadedFileName || (draft.choice === 'csv' ? 'import.csv' : 'import.list'), draft.choice);
    return previewDirectJob(draft);
  }

  function review(): void {
    if (!valid) return;
    preview = buildPlan();
    selectedLeaves = new Set(previewLeafRefs(preview));
  }

  function acceptFile(file: File): void {
    const request: ProposedInputArtifactUploadDto = {
      filename: file.name,
      contentType: file.type || 'application/octet-stream',
      sizeBytes: file.size,
      purpose: 'job-extraction-input',
    };
    void request;
    draft.uploadedFileName = file.name;
    draft.uploadedFileType = file.type;
    clearPreview();
  }

  function chooseFile(event: Event): void {
    const file = (event.currentTarget as HTMLInputElement).files?.[0];
    if (file) acceptFile(file);
  }

  function handleDrop(event: DragEvent): void {
    event.preventDefault();
    uploadActive = false;
    const file = event.dataTransfer?.files?.[0];
    if (file) acceptFile(file);
  }

  function submitPlan(plan: JobPreviewPlan, leaves: Set<string>): void {
    if (!leaves.size) return;
    const request: ProposedSubmitJobPreviewRequestDto = {
      downloadSettings: currentDownloadSettings(),
      selection: { mode: 'only', leafRefs: [...leaves] },
    };
    void request;
    const result = commitPreview(plan, leaves);
    if (!result.rootId || !result.records.length) return;
    onstart(result.records, result.rootId);
  }

  function startNow(): void {
    if (!valid) return;
    const plan = buildPlan();
    submitPlan(plan, new Set(previewLeafRefs(plan)));
  }

  function startReviewed(): void {
    if (!preview) return;
    submitPlan(preview, selectedLeaves);
  }
</script>

<section class="new-job-composer" aria-label="New job">
  <header class="new-job-heading">
    <h2>Create new job</h2>
    <button type="button" class="new-job-close" aria-label="Close new job" onclick={onclose}><Icon name="x" /></button>
  </header>

  {#if optionsOpen}
    <div class="new-job-options-view">
      {#if draft.choice === 'song'}
        <SearchConfigPanel mode="track" bind:conditions={trackConditions} title="Conditions & ranking" initialTab="conditions" onclose={() => (optionsOpen = false)} />
      {:else if draft.choice === 'album'}
        <SearchConfigPanel mode="album" bind:conditions={albumConditions} title="Conditions & ranking" initialTab="conditions" onclose={() => (optionsOpen = false)} />
      {:else}
        <SearchConfigPanel mode="track" configurationFamily="mixed" bind:conditions={mixedConditions} title="Conditions & ranking" initialTab="conditions" onclose={() => (optionsOpen = false)} />
      {/if}
    </div>
  {:else}
    <div class="new-job-layout">
      <aside class="new-job-type-nav" aria-label="Job type">
        <section>
          <h3>Download automatically</h3>
          {#each automaticChoices as choice (choice.value)}
            <button type="button" class:active={draft.choice === choice.value} class="new-job-type-choice" onclick={() => choose(choice.value)}>
              <Icon name={choice.icon} />
              <span><strong>{choice.label}</strong><small>{choice.detail}</small></span>
            </button>
          {/each}
        </section>
        <section>
          <h3>From source</h3>
          {#each sourceChoices as choice (choice.value)}
            <button type="button" class:active={draft.choice === choice.value} class="new-job-type-choice" onclick={() => choose(choice.value)}>
              <Icon name={choice.icon} />
              <span><strong>{choice.label}</strong><small>{choice.detail}</small></span>
            </button>
          {/each}
        </section>
        <section>
          <h3>From file</h3>
          {#each fileChoices as choice (choice.value)}
            <button type="button" class:active={draft.choice === choice.value} class="new-job-type-choice" onclick={() => choose(choice.value)}>
              <Icon name={choice.icon} />
              <span><strong>{choice.label}</strong><small>{choice.detail}</small></span>
            </button>
          {/each}
        </section>
      </aside>

      <div class="new-job-workspace">
        <header class="new-job-current-choice">
          <div><strong>{choiceMetadata.label}</strong><small>{choiceMetadata.detail}</small></div>
        </header>

        <div class="new-job-config">
          {#if draft.choice === 'song'}
            <label><span>Artist <small>(optional)</small></span><input value={draft.artist} oninput={(event) => setField('artist', (event.currentTarget as HTMLInputElement).value)} /></label>
            <label><span>Title</span><input value={draft.title} oninput={(event) => setField('title', (event.currentTarget as HTMLInputElement).value)} /></label>
          {:else if draft.choice === 'album'}
            <label><span>Artist <small>(optional)</small></span><input value={draft.artist} oninput={(event) => setField('artist', (event.currentTarget as HTMLInputElement).value)} /></label>
            <label><span>Album</span><input value={draft.album} oninput={(event) => setField('album', (event.currentTarget as HTMLInputElement).value)} /></label>
          {:else if isInlineSourceChoice(draft.choice)}
            {#if draft.choice === 'spotify'}
              <div class="new-job-source-mode">
                <span>Input</span>
                <div class="new-job-segmented" role="group" aria-label="Spotify input">
                  <button type="button" class:active={draft.spotifyInput === 'url'} onclick={() => setSpotifyInput('url')}>Playlist / album URL</button>
                  <button type="button" class:active={draft.spotifyInput === 'likes'} onclick={() => setSpotifyInput('likes')}>Liked songs</button>
                  <button type="button" class:active={draft.spotifyInput === 'albums'} onclick={() => setSpotifyInput('albums')}>Liked albums</button>
                </div>
              </div>
              {#if draft.spotifyInput === 'url'}
                <label class="new-job-source-field"><span>URL</span><input value={draft.source} placeholder={sourcePlaceholder(draft.choice)} oninput={(event) => setField('source', (event.currentTarget as HTMLInputElement).value)} /></label>
              {:else}
                <p class="new-job-field-help">Uses the Spotify account configured for the daemon.</p>
              {/if}
            {:else}
              <label class="new-job-source-field"><span>URL</span><input value={draft.source} placeholder={sourcePlaceholder(draft.choice)} oninput={(event) => setField('source', (event.currentTarget as HTMLInputElement).value)} /></label>
            {/if}
          {:else}
            <label
              class:dragging={uploadActive}
              class="new-job-drop-zone"
              ondragover={(event) => { event.preventDefault(); uploadActive = true; }}
              ondragleave={() => (uploadActive = false)}
              ondrop={handleDrop}
            >
              <input type="file" accept={draft.choice === 'csv' ? '.csv,text/csv' : '.txt,.list,text/plain'} onchange={chooseFile} />
              <Icon name="upload-file" />
              <span>
                <strong>{draft.uploadedFileName || `Drop a ${draft.choice === 'csv' ? 'CSV' : 'list'} file here`}</strong>
                <small>{draft.uploadedFileName ? 'Choose another file or continue' : 'or click to choose a local file'}</small>
              </span>
            </label>
            {#if draft.choice === 'csv'}
              <section class="new-job-csv-options">
                <button
                  type="button"
                  class="new-job-csv-options-toggle"
                  aria-expanded={csvMappingOpen}
                  onclick={() => (csvMappingOpen = !csvMappingOpen)}
                >
                  <span><Icon name="settings" /><strong>Column mapping</strong></span>
                  <small>{csvMappingCustomized ? 'Custom' : 'Auto-detect'}</small>
                </button>
                {#if csvMappingOpen}
                  <div class="new-job-csv-mapping">
                    <p>Override header names only when the CSV does not use common column names.</p>
                    <div class="new-job-csv-grid">
                      <label><span>Artist</span><input placeholder="Auto" value={draft.csvColumns.artistCol} oninput={(event) => setCsvColumn('artistCol', (event.currentTarget as HTMLInputElement).value)} /></label>
                      <label><span>Title</span><input placeholder="Auto" value={draft.csvColumns.titleCol} oninput={(event) => setCsvColumn('titleCol', (event.currentTarget as HTMLInputElement).value)} /></label>
                      <label><span>Album</span><input placeholder="Auto" value={draft.csvColumns.albumCol} oninput={(event) => setCsvColumn('albumCol', (event.currentTarget as HTMLInputElement).value)} /></label>
                      <label><span>Length</span><input placeholder="Auto" value={draft.csvColumns.lengthCol} oninput={(event) => setCsvColumn('lengthCol', (event.currentTarget as HTMLInputElement).value)} /></label>
                      <label><span>Track count</span><input placeholder="Auto" value={draft.csvColumns.trackCountCol} oninput={(event) => setCsvColumn('trackCountCol', (event.currentTarget as HTMLInputElement).value)} /></label>
                      <label><span>YouTube URL / ID</span><input placeholder="Auto" value={draft.csvColumns.ytIdCol} oninput={(event) => setCsvColumn('ytIdCol', (event.currentTarget as HTMLInputElement).value)} /></label>
                      <label><span>Description</span><input placeholder="Auto" value={draft.csvColumns.descCol} oninput={(event) => setCsvColumn('descCol', (event.currentTarget as HTMLInputElement).value)} /></label>
                    </div>
                  </div>
                {/if}
              </section>
            {/if}
          {/if}
        </div>

        <div class="new-job-config-actions">
          <button type="button" class="new-job-options-button" aria-expanded="false" onclick={() => (optionsOpen = true)}><Icon name="settings" /> Conditions & ranking</button>
          <span class="new-job-action-spacer"></span>
          <button type="button" class="new-job-review-button" disabled={!valid} onclick={review}>Review</button>
          <button type="button" class="new-job-start-button" disabled={!valid} onclick={startNow}>Start</button>
        </div>

        {#if preview}
          {@const currentPreview = preview}
          <section class="new-job-preview">
            <header>
              <div><p class="eyebrow">Review</p><h3>{currentPreview.title}</h3><small>{currentPreview.sourceLabel}</small></div>
              <button type="button" class="new-job-select-toggle" onclick={() => (selectedLeaves = selectedCount === previewLeafRefs(currentPreview).length ? new Set() : new Set(previewLeafRefs(currentPreview)))}>{selectedCount === previewLeafRefs(currentPreview).length ? 'Deselect all' : 'Select all'}</button>
            </header>
            <p class="new-job-preview-note">Review resolves extraction and preprocessing only. Soulseek candidate discovery starts after submission.</p>
            <JobPreviewTree roots={currentPreview.roots} {selectedLeaves} onselectionchange={(next) => (selectedLeaves = next)} />
            <footer class="new-job-preview-actions">
              <span>{selectedCount} selected</span>
              <button type="button" class="new-job-start-button" disabled={!selectedCount} onclick={startReviewed}>Start {selectedCount || ''} {selectedCount === 1 ? 'job' : 'jobs'}</button>
            </footer>
          </section>
        {/if}
      </div>
    </div>
  {/if}
</section>
