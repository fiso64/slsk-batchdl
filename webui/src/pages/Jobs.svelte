<script lang="ts">
  import SearchConditionPills from '../components/SearchConditionPills.svelte';
  import UsernameLink from '../components/UsernameLink.svelte';
  import ResourceStateNotice from '../components/ResourceStateNotice.svelte';
  import LoadMoreButton from '../components/LoadMoreButton.svelte';
  import MutationStatus from '../components/MutationStatus.svelte';
  import ResultFilterControl from '../components/ResultFilterControl.svelte';
  import SelectionToolbar from '../components/SelectionToolbar.svelte';
  import FileItemCard from '../components/items/FileItemCard.svelte';
  import FolderItemCard from '../components/items/FolderItemCard.svelte';
  import PeerItemGroup from '../components/items/PeerItemGroup.svelte';
  import NewJobComposer from '../components/jobs/NewJobComposer.svelte';
  import JobCompactRow from '../components/jobs/JobCompactRow.svelte';
  import AutomaticJobDetail from '../components/jobs/AutomaticJobDetail.svelte';
  import SearchConfigPanel from '../components/SearchConfigPanel.svelte';
  import { hasAppliedConditions, type PrototypeSearchConditions } from '../prototype/search-config';
  import Icon from '../components/Icon.svelte';
  import { groupAdjacentBy } from '../prototype/grouping';
  import type { ScenarioId } from '../mock/types';
  import type { PrototypeDownloadSelectionSummary, PrototypeMutationState, ProposedHistoryDeleteRequestDto } from '../prototype/backend-contracts';
  import type { SearchDraft } from '../prototype/search';
  import { isAggregateSearchMode, searchModeFamily, searchModeLabel } from '../prototype/search';
  import type { UserLinkActions } from '../prototype/navigation';
  import {
    extractSourceLabel,
    isAutomaticJobActive,
    isSemanticRoot,
    presentationChildren,
    presentationParent,
    presentationTarget,
    type AutomaticJobRecord,
  } from '../prototype/jobs';
  import { resourceStateForScenario, type PrototypeResourceState } from '../prototype/resource-state';
  import {
    aggregateGroupsForRecord,
    buildAlbumFolderRetrievalRequest,
    buildGenericDirectoryRetrievalRequest,
    buildGenericFileDownloadRequest,
    buildSearchResultProjectionRequest,
    requestSearchResultProjection,
    retrieveAlbumFolderFixture,
    retrieveGenericDirectoryFixture,
    type AggregateSearchGroup,
    type AlbumFileResult,
    type AlbumSearchResult,
    type GenericDirectoryResult,
    type GenericFileResult,
    type ProjectedSearchResult,
    type SearchRecord,
    type SearchSort,
    type SearchView,
    type SizeSortDirection,
    type TrackSearchResult,
  } from '../prototype/search-results';

  interface Props {
    search: SearchDraft;
    scenarioId: ScenarioId;
    searches: SearchRecord[];
    view: SearchView;
    automaticJobs: AutomaticJobRecord[];
    activeJobId: string | null;
    userActions: UserLinkActions;
    onopenrecord: (record: SearchRecord) => void;
    onshowlist: () => void;
    onsearchagain: (record: SearchRecord) => void;
    onopenjob: (job: AutomaticJobRecord) => void;
    onstartjobs: (records: AutomaticJobRecord[], rootId: string) => void;
  }

  let {
    search,
    scenarioId,
    searches = $bindable(),
    view = $bindable(),
    automaticJobs = $bindable(),
    activeJobId = $bindable(),
    userActions,
    onopenrecord,
    onshowlist,
    onsearchagain,
    onopenjob,
    onstartjobs,
  }: Props = $props();

  let filterText = $state('');
  let sort = $state<SearchSort>('relevance');
  let sizeDirection = $state<SizeSortDirection>('desc');
  let selected = $state<Set<string>>(new Set());
  let conditionsOpen = $state(false);
  const JOB_HISTORY_PAGE_SIZE = 8;

  let resultPagesRequested = $state(1);
  let projectionRequestKey = '';
  let historyLimit = $state(JOB_HISTORY_PAGE_SIZE);
  let mutation = $state<PrototypeMutationState>({ phase: 'idle' });
  let aggregateRepresentativeIds = $state<Record<string, string>>({});
  let selectedAggregateGroups = $state<Set<string>>(new Set());
  let selectedAggregateFiles = $state<Set<string>>(new Set());
  let aggregateOptionsGroupId = $state<string | null>(null);
  let albumRetrievalOverrides = $state<Record<string, { state: 'retrieving' | 'retrieved' | 'failed'; result?: AlbumSearchResult }>>({});
  let genericRetrievalOverrides = $state<Record<string, { state: 'retrieving' | 'retrieved' | 'failed'; result?: GenericDirectoryResult }>>({});
  let newJobOpen = $state(false);

  let activeRecord = $derived(searches.find((item) => item.id === activeJobId) ?? null);
  let activeAutomaticJob = $derived(automaticJobs.find((item) => item.id === activeJobId) ?? null);
  let automaticRoots = $derived(automaticJobs.filter((job) => isSemanticRoot(job, automaticJobs)));
  let listEntries = $derived([
    ...searches.map((record) => ({ type: 'search' as const, id: record.id, createdAtUtc: record.createdAtUtc, record })),
    ...automaticRoots.map((root) => ({ type: 'automatic' as const, id: root.id, createdAtUtc: root.createdAtUtc, root, job: presentationTarget(root, automaticJobs) })),
  ].sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc)));

  let activeMode = $derived(activeRecord?.draft.resultMode ?? search.resultMode);
  let aggregateMode = $derived(isAggregateSearchMode(activeMode));
  let genericMode = $derived(activeMode === 'generic');
  let listResourceState = $derived(resourceStateForScenario(scenarioId, 'search-list'));

  $effect(() => {
    scenarioId;
    historyLimit = JOB_HISTORY_PAGE_SIZE;
    mutation = { phase: 'idle' };
    aggregateRepresentativeIds = {};
    selectedAggregateGroups = new Set();
    selectedAggregateFiles = new Set();
    aggregateOptionsGroupId = null;
    albumRetrievalOverrides = {};
    genericRetrievalOverrides = {};
  });

  $effect(() => {
    const key = JSON.stringify({
      activeJobId,
      filterText,
      sort,
      sizeDirection,
      conditions: activeRecord?.conditions,
    });
    if (key === projectionRequestKey) return;
    projectionRequestKey = key;
    resultPagesRequested = 1;
  });

  function openSearch(record: SearchRecord): void {
    onopenrecord(record);
    filterText = '';
    sort = 'relevance';
    selected = new Set();
    selectedAggregateGroups = new Set();
    selectedAggregateFiles = new Set();
    aggregateRepresentativeIds = {};
    aggregateOptionsGroupId = null;
    conditionsOpen = false;
    resultPagesRequested = 1;
  }

  function removeSearch(id: string): void {
    const request: ProposedHistoryDeleteRequestDto = { resourceKind: 'job', resourceIds: [id], semantics: 'archive-from-history' };
    void request;
    mutation = { phase: 'pending', label: 'Removing search history…' };
    searches = searches.filter((item) => item.id !== id);
    mutation = { phase: 'succeeded', label: 'Search removed' };
    if (activeJobId !== id) return;
    activeJobId = listEntries.find((entry) => entry.id !== id)?.id ?? null;
    view = 'list';
    onshowlist();
  }

  function searchIsActive(record: SearchRecord): boolean {
    return record.status === 'pending' || record.status === 'searching' || record.status === 'receiving';
  }

  function cancelSearch(record: SearchRecord): void {
    mutation = { phase: 'pending', label: 'Cancelling search…' };
    searches = searches.map((item) => item.id === record.id ? { ...item, status: 'cancelled' as const } : item);
    mutation = { phase: 'succeeded', label: 'Search cancelled' };
  }

  function handleSearchAction(record: SearchRecord): void {
    if (searchIsActive(record)) cancelSearch(record);
    else removeSearch(record.id);
  }

  function automaticSubtreeIds(root: AutomaticJobRecord): Set<string> {
    const ids = new Set<string>();
    const visit = (job: AutomaticJobRecord) => {
      if (ids.has(job.id)) return;
      ids.add(job.id);
      for (const child of presentationChildren(job, automaticJobs)) visit(child);
    };
    visit(root);
    return ids;
  }

  function cancelAutomaticJob(job: AutomaticJobRecord): void {
    const target = presentationTarget(job, automaticJobs);
    const ids = automaticSubtreeIds(target);
    mutation = { phase: 'pending', label: `Cancelling ${target.title}…` };
    automaticJobs = automaticJobs.map((candidate) => ids.has(candidate.id) && isAutomaticJobActive(candidate, automaticJobs)
      ? { ...candidate, status: 'cancelled' as const, lifetime: 'retained' as const }
      : candidate);
    mutation = { phase: 'succeeded', label: `${target.title} cancelled` };
  }

  function removeAutomaticJob(job: AutomaticJobRecord): void {
    const target = presentationTarget(job, automaticJobs);
    const isRoot = presentationParent(target, automaticJobs) === null;
    const ids = isRoot ? new Set(automaticJobs.filter((candidate) => candidate.workflowId === target.workflowId).map((candidate) => candidate.id)) : automaticSubtreeIds(target);
    const request: ProposedHistoryDeleteRequestDto = { resourceKind: 'job', resourceIds: [...ids], semantics: 'archive-from-history' };
    void request;
    mutation = { phase: 'pending', label: `Removing ${target.title}…` };
    automaticJobs = automaticJobs.filter((candidate) => !ids.has(candidate.id));
    mutation = { phase: 'succeeded', label: `${target.title} removed` };
    if (activeJobId && ids.has(activeJobId)) {
      activeJobId = null;
      view = 'list';
      onshowlist();
    }
  }

  function handleAutomaticJobAction(job: AutomaticJobRecord): void {
    const target = presentationTarget(job, automaticJobs);
    if (isAutomaticJobActive(target, automaticJobs)) cancelAutomaticJob(target);
    else removeAutomaticJob(target);
  }

  function startPreviewJobs(records: AutomaticJobRecord[], rootId: string): void {
    onstartjobs(records, rootId);
    newJobOpen = false;
  }

  function statusLabel(status: SearchRecord['status']): string {
    const labels: Record<SearchRecord['status'], string> = {
      pending: 'Pending',
      searching: 'Searching',
      receiving: 'Receiving',
      complete: 'Complete',
      failed: 'Failed',
      cancelled: 'Cancelled',
      skipped: 'Skipped',
      interrupted: 'Interrupted',
    };
    return labels[status];
  }

  function resultResourceState(record: SearchRecord): PrototypeResourceState {
    if (record.resultState === 'pruned') return { phase: 'pruned', title: 'Results unavailable', blocking: true };
    if (record.resultState === 'not-persisted') return { phase: 'unavailable', title: 'Results unavailable', blocking: true };
    return resourceStateForScenario(scenarioId, 'search-results');
  }

  interface PeerGroup {
    key: string;
    peer: ProjectedSearchResult['peer'];
    preferred: boolean;
    items: ProjectedSearchResult[];
  }

  function groupAdjacent(results: ProjectedSearchResult[]): PeerGroup[] {
    return groupAdjacentBy(
      results,
      (result) => `${sort === 'relevance' ? (result.preferred ? 'preferred' : 'other') : 'all'}:${result.peer.username}`,
      `${activeJobId ?? 'search'}:`,
    ).map((group) => ({
      key: group.key,
      peer: group.items[0]!.peer,
      preferred: group.items[0]!.preferred,
      items: group.items,
    }));
  }

  function selectedKey(result: TrackSearchResult): string {
    return `track:${result.id}`;
  }

  function selectedAlbumFileKey(album: AlbumSearchResult, file: AlbumFileResult): string {
    return `album:${album.id}:${file.id}`;
  }

  function isAlbumFullySelected(album: AlbumSearchResult): boolean {
    return album.files.length > 0 && album.files.every((file) => selected.has(selectedAlbumFileKey(album, file)));
  }

  function isAlbumPartiallySelected(album: AlbumSearchResult): boolean {
    const count = album.files.filter((file) => selected.has(selectedAlbumFileKey(album, file))).length;
    return count > 0 && count < album.files.length;
  }


  function selectedFileIdsForAlbum(album: AlbumSearchResult): Set<string> {
    return new Set(album.files.filter((file) => selected.has(selectedAlbumFileKey(album, file))).map((file) => file.id));
  }

  function selectedGenericFileKey(directory: GenericDirectoryResult, file: GenericFileResult): string {
    return `generic:${directory.id}:${file.id}`;
  }

  function isGenericDirectoryFullySelected(directory: GenericDirectoryResult): boolean {
    return directory.files.length > 0 && directory.files.every((file) => selected.has(selectedGenericFileKey(directory, file)));
  }

  function isGenericDirectoryPartiallySelected(directory: GenericDirectoryResult): boolean {
    const count = directory.files.filter((file) => selected.has(selectedGenericFileKey(directory, file))).length;
    return count > 0 && count < directory.files.length;
  }

  function selectedFileIdsForGenericDirectory(directory: GenericDirectoryResult): Set<string> {
    return new Set(directory.files.filter((file) => selected.has(selectedGenericFileKey(directory, file))).map((file) => file.id));
  }

  function toggleGenericFiles(directory: GenericDirectoryResult, files: Array<{ id: string }>, checked: boolean): void {
    const fileIds = new Set(files.map((file) => file.id));
    const next = new Set(selected);
    for (const file of directory.files) {
      if (!fileIds.has(file.id)) continue;
      const key = selectedGenericFileKey(directory, file);
      if (checked) next.add(key);
      else next.delete(key);
    }
    selected = next;
  }

  function toggleGenericDirectory(directory: GenericDirectoryResult, checked: boolean): void {
    toggleGenericFiles(directory, directory.files, checked);
  }
  function indeterminate(node: HTMLInputElement, value: boolean) {
    node.indeterminate = value;
    return {
      update(next: boolean) { node.indeterminate = next; },
    };
  }

  function toggleSelection(key: string, checked: boolean): void {
    const next = new Set(selected);
    if (checked) next.add(key);
    else next.delete(key);
    selected = next;
  }

  function toggleAlbum(album: AlbumSearchResult, checked: boolean): void {
    const current = albumWithRetrieval(album);
    const next = new Set(selected);
    for (const file of current.files) {
      const key = selectedAlbumFileKey(current, file);
      if (checked) next.add(key);
      else next.delete(key);
    }
    selected = next;
  }

  function albumWithRetrieval(album: AlbumSearchResult): AlbumSearchResult {
    const override = albumRetrievalOverrides[album.id];
    if (!override) return album;
    if (override.result) return override.result;
    return { ...album, retrievalState: override.state };
  }

  function albumContentsState(album: AlbumSearchResult): 'partial' | 'retrieving' | 'complete' | 'failed' {
    const state = albumWithRetrieval(album).retrievalState;
    if (state === 'retrieved') return 'complete';
    if (state === 'retrieving') return 'retrieving';
    if (state === 'failed') return 'failed';
    return 'partial';
  }

  function loadFullAlbumFolder(album: AlbumSearchResult, group?: AggregateSearchGroup): void {
    const current = albumWithRetrieval(album);
    if (current.retrievalState === 'retrieving' || current.retrievalState === 'retrieved') return;

    const request = buildAlbumFolderRetrievalRequest(
      current,
      group?.kind === 'album-aggregate'
        ? { artist: group.artist, album: group.album, artistMaybeWrong: false }
        : undefined,
    );
    void request;

    const preserveNormalSelection = !group && isAlbumFullySelected(current);
    const preserveAggregateSelection = Boolean(group)
      && aggregateRepresentative(group!).id === current.id
      && aggregateGroupSelected(group!);

    albumRetrievalOverrides = {
      ...albumRetrievalOverrides,
      [current.id]: { state: 'retrieving' },
    };

    const finish = () => {
      const retrieved = retrieveAlbumFolderFixture(current);
      albumRetrievalOverrides = {
        ...albumRetrievalOverrides,
        [current.id]: { state: 'retrieved', result: retrieved },
      };

      if (preserveNormalSelection) {
        const next = new Set(selected);
        for (const file of retrieved.files) next.add(selectedAlbumFileKey(retrieved, file));
        selected = next;
      }
      if (group && preserveAggregateSelection) {
        const next = new Set(selectedAggregateFiles);
        for (const file of retrieved.files) next.add(aggregateAlbumFileKey(group, file));
        selectedAggregateFiles = next;
      }
    };

    if (typeof window === 'undefined') finish();
    else window.setTimeout(finish, 650);
  }

  function genericWithRetrieval(directory: GenericDirectoryResult): GenericDirectoryResult {
    const override = genericRetrievalOverrides[directory.id];
    if (!override) return directory;
    if (override.result) return override.result;
    return { ...directory, retrievalState: override.state };
  }

  function genericContentsState(directory: GenericDirectoryResult): 'partial' | 'retrieving' | 'complete' | 'failed' {
    const state = genericWithRetrieval(directory).retrievalState;
    if (state === 'retrieved') return 'complete';
    if (state === 'retrieving') return 'retrieving';
    if (state === 'failed') return 'failed';
    return 'partial';
  }

  function loadFullGenericDirectory(directory: GenericDirectoryResult): void {
    const current = genericWithRetrieval(directory);
    if (current.retrievalState === 'retrieving' || current.retrievalState === 'retrieved') return;

    const request = buildGenericDirectoryRetrievalRequest(current);
    void request;
    const preserveSelection = isGenericDirectoryFullySelected(current);
    genericRetrievalOverrides = {
      ...genericRetrievalOverrides,
      [current.id]: { state: 'retrieving' },
    };

    const finish = () => {
      const retrieved = retrieveGenericDirectoryFixture(current);
      genericRetrievalOverrides = {
        ...genericRetrievalOverrides,
        [current.id]: { state: 'retrieved', result: retrieved },
      };
      if (preserveSelection) {
        const next = new Set(selected);
        for (const file of retrieved.files) next.add(selectedGenericFileKey(retrieved, file));
        selected = next;
      }
    };

    if (typeof window === 'undefined') finish();
    else window.setTimeout(finish, 650);
  }

  function aggregateGroups(record: SearchRecord): AggregateSearchGroup[] {
    return aggregateGroupsForRecord(record, filterText);
  }

  function aggregateRepresentative(group: AggregateSearchGroup): TrackSearchResult | AlbumSearchResult {
    const selectedId = aggregateRepresentativeIds[group.id];
    const option = group.options.find((candidate) => candidate.id === selectedId) ?? group.options[0]!;
    return option.kind === 'album' ? albumWithRetrieval(option) : option;
  }

  function aggregateAlbumFileKey(group: AggregateSearchGroup, file: AlbumFileResult): string {
    return `aggregate:${group.id}:${file.id}`;
  }

  function aggregateGroupSelected(group: AggregateSearchGroup): boolean {
    const representative = aggregateRepresentative(group);
    if (representative.kind === 'track') return selectedAggregateGroups.has(group.id);
    return representative.files.length > 0 && representative.files.every((file) => selectedAggregateFiles.has(aggregateAlbumFileKey(group, file)));
  }

  function aggregateGroupPartial(group: AggregateSearchGroup): boolean {
    const representative = aggregateRepresentative(group);
    if (representative.kind === 'track') return false;
    const selectedCount = representative.files.filter((file) => selectedAggregateFiles.has(aggregateAlbumFileKey(group, file))).length;
    return selectedCount > 0 && selectedCount < representative.files.length;
  }

  function aggregateSelectedFileIds(group: AggregateSearchGroup): Set<string> {
    const representative = aggregateRepresentative(group);
    if (representative.kind !== 'album') return new Set();
    return new Set(representative.files.filter((file) => selectedAggregateFiles.has(aggregateAlbumFileKey(group, file))).map((file) => file.id));
  }

  function toggleAggregateGroup(group: AggregateSearchGroup, checked: boolean): void {
    const representative = aggregateRepresentative(group);
    if (representative.kind === 'track') {
      const next = new Set(selectedAggregateGroups);
      if (checked) next.add(group.id);
      else next.delete(group.id);
      selectedAggregateGroups = next;
      return;
    }

    const next = new Set(selectedAggregateFiles);
    for (const file of representative.files) {
      const key = aggregateAlbumFileKey(group, file);
      if (checked) next.add(key);
      else next.delete(key);
    }
    selectedAggregateFiles = next;
  }

  function toggleAggregateAlbumFile(group: AggregateSearchGroup, file: AlbumFileResult, checked: boolean): void {
    const next = new Set(selectedAggregateFiles);
    const key = aggregateAlbumFileKey(group, file);
    if (checked) next.add(key);
    else next.delete(key);
    selectedAggregateFiles = next;
  }

  function setAllAggregate(record: SearchRecord, checked: boolean): void {
    const nextGroups = new Set(selectedAggregateGroups);
    const nextFiles = new Set(selectedAggregateFiles);
    for (const group of aggregateGroups(record)) {
      const representative = aggregateRepresentative(group);
      if (representative.kind === 'track') {
        if (checked) nextGroups.add(group.id);
        else nextGroups.delete(group.id);
        continue;
      }
      for (const file of representative.files) {
        const key = aggregateAlbumFileKey(group, file);
        if (checked) nextFiles.add(key);
        else nextFiles.delete(key);
      }
    }
    selectedAggregateGroups = nextGroups;
    selectedAggregateFiles = nextFiles;
  }

  function allAggregateSelected(record: SearchRecord): boolean {
    const groups = aggregateGroups(record);
    return groups.length > 0 && groups.every((group) => aggregateGroupSelected(group));
  }

  function chooseAggregateOption(group: AggregateSearchGroup, option: TrackSearchResult | AlbumSearchResult): void {
    aggregateRepresentativeIds = { ...aggregateRepresentativeIds, [group.id]: option.id };
    if (option.kind === 'track') {
      const next = new Set(selectedAggregateGroups);
      next.add(group.id);
      selectedAggregateGroups = next;
    } else {
      const prefix = `aggregate:${group.id}:`;
      const next = new Set([...selectedAggregateFiles].filter((key) => !key.startsWith(prefix)));
      for (const file of option.files) next.add(aggregateAlbumFileKey(group, file));
      selectedAggregateFiles = next;
    }
    aggregateOptionsGroupId = null;
  }

  function aggregateSelectionSummary(record: SearchRecord): PrototypeDownloadSelectionSummary {
    let requestedCount = 0;
    let lockedCount = 0;
    for (const group of aggregateGroups(record)) {
      const representative = aggregateRepresentative(group);
      if (representative.kind === 'track') {
        if (!selectedAggregateGroups.has(group.id)) continue;
        requestedCount += 1;
        if (representative.locked) lockedCount += 1;
        continue;
      }
      for (const file of representative.files) {
        if (!selectedAggregateFiles.has(aggregateAlbumFileKey(group, file))) continue;
        requestedCount += 1;
        if (representative.locked || file.locked) lockedCount += 1;
      }
    }
    return {
      requestedCount,
      uniqueFileCount: requestedCount,
      resolvablePublicCount: requestedCount - lockedCount,
      lockedCount,
      skippedCount: lockedCount,
    };
  }

  function handleWindowKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Escape') return;
    if (aggregateOptionsGroupId) aggregateOptionsGroupId = null;
    else if (newJobOpen) newJobOpen = false;
  }

  function currentResultProjection(record: SearchRecord) {
    let cursor: string | null = null;
    let items: ProjectedSearchResult[] = [];
    let page = requestSearchResultProjection(
      record,
      buildSearchResultProjectionRequest(
        record,
        filterText,
        sort,
        sizeDirection,
        cursor,
        record.pagination.resultPageSize,
      ),
    );
    items = page.items.map((result) => result.kind === 'album' ? albumWithRetrieval(result) : result.kind === 'generic-directory' ? genericWithRetrieval(result) : result);
    for (let pageIndex = 1; pageIndex < resultPagesRequested && page.nextCursor; pageIndex += 1) {
      cursor = page.nextCursor;
      page = requestSearchResultProjection(
        record,
        buildSearchResultProjectionRequest(
          record,
          filterText,
          sort,
          sizeDirection,
          cursor,
          record.pagination.resultPageSize,
        ),
      );
      items.push(...page.items.map((result) => result.kind === 'album' ? albumWithRetrieval(result) : result.kind === 'generic-directory' ? genericWithRetrieval(result) : result));
    }
    return { ...page, items };
  }

  function selectionSummary(): PrototypeDownloadSelectionSummary {
    if (activeRecord && isAggregateSearchMode(activeRecord.draft.resultMode)) return aggregateSelectionSummary(activeRecord);
    let requestedCount = 0;
    let lockedCount = 0;
    const received = activeRecord ? currentResultProjection(activeRecord).items : [];
    if (activeMode === 'track') {
      for (const result of received) {
        if (result.kind !== 'track' || !selected.has(selectedKey(result))) continue;
        requestedCount += 1;
        if (result.locked) lockedCount += 1;
      }
    } else if (activeMode === 'generic') {
      for (const result of received) {
        if (result.kind !== 'generic-directory') continue;
        for (const file of result.files) {
          if (!selected.has(selectedGenericFileKey(result, file))) continue;
          requestedCount += 1;
          if (file.locked) lockedCount += 1;
        }
      }
    } else {
      for (const result of received) {
        if (result.kind !== 'album') continue;
        for (const file of result.files) {
          if (!selected.has(selectedAlbumFileKey(result, file))) continue;
          requestedCount += 1;
          if (file.locked) lockedCount += 1;
        }
      }
    }
    return { requestedCount, uniqueFileCount: requestedCount, resolvablePublicCount: requestedCount - lockedCount, lockedCount, skippedCount: lockedCount };
  }

  function requestSelectedDownload(): void {
    const summary = selectionSummary();
    if (!summary.resolvablePublicCount) {
      mutation = { phase: 'rejected', label: 'Nothing downloadable', detail: `${summary.lockedCount} selected ${aggregateMode ? 'option' : 'file'}${summary.lockedCount === 1 ? '' : 's'} locked.` };
      return;
    }
    if (activeRecord && activeMode === 'generic') {
      const files = currentResultProjection(activeRecord).items
        .filter((result): result is GenericDirectoryResult => result.kind === 'generic-directory')
        .flatMap((directory) => directory.files
          .filter((file) => selected.has(selectedGenericFileKey(directory, file)) && !file.locked)
          .map((file) => file.candidateRef));
      const request = buildGenericFileDownloadRequest(files);
      void request;
    }
    const unit = aggregateMode ? 'selection' : 'file';
    mutation = { phase: 'pending', label: `Requesting ${summary.resolvablePublicCount} ${unit}${summary.resolvablePublicCount === 1 ? '' : 's'}…` };
    mutation = summary.skippedCount
      ? { phase: 'partially-succeeded', label: `${summary.resolvablePublicCount} requested`, detail: `${summary.skippedCount} locked ${unit}${summary.skippedCount === 1 ? '' : 's'} skipped.` }
      : { phase: 'succeeded', label: `${summary.resolvablePublicCount} download${summary.resolvablePublicCount === 1 ? '' : 's'} requested` };
  }

  function changeSort(event: Event): void {
    const next = (event.currentTarget as HTMLSelectElement).value as SearchSort;
    sort = next;
    if (!genericMode) return;
    if (next === 'name') sizeDirection = 'asc';
    else if (next === 'size' || next === 'count') sizeDirection = 'desc';
  }

  function tierGroups(groups: PeerGroup[], preferred: boolean): PeerGroup[] {
    return groups.filter((group) => group.preferred === preferred);
  }

  function tierItemCount(groups: PeerGroup[]): number {
    return groups.reduce((total, group) => total + group.items.length, 0);
  }
</script>

<svelte:window onkeydown={handleWindowKeydown} />

<section class="page page-search redesigned-search-page">
  {#if view === 'list'}
    <header class="page-heading search-list-heading jobs-list-heading">
      <div><p class="eyebrow">Work</p><h1>Jobs</h1></div>
      <button type="button" class="new-job-button" onclick={() => (newJobOpen = !newJobOpen)}><span>+</span> New job</button>
    </header>

    {#if listResourceState.blocking}
      <div class="empty-state"><strong>{listResourceState.title}</strong><p>{listResourceState.detail}</p></div>
    {:else}
      <ResourceStateNotice state={listResourceState} />
      <MutationStatus state={mutation} />
    <div class="search-history-list mixed-job-history-list">
      {#each listEntries.slice(0, historyLimit) as entry (entry.id)}
        {#if entry.type === 'search'}
          {@const record = entry.record}
          <div class="search-history-row">
            <button type="button" class="search-history-open" onclick={() => openSearch(record)}>
              <span class="search-history-query">{record.displayQuery}</span>
              <span class={`search-status-badge ${record.status}`}><i></i>{statusLabel(record.status)}</span>
              <span class="search-history-context">
                <Icon name={record.draft.resultMode} class="search-kind-icon" />
                <span>{searchModeLabel(record.draft.resultMode)}</span>
                <span class="stat-separator">·</span>
                <span>{record.when}</span>
              </span>
              <span class="search-history-stats">
                {#if isAggregateSearchMode(record.draft.resultMode)}
                  <span><strong>{record.aggregateGroupCount ?? 0}</strong> groups</span>
                  <span class="stat-separator">·</span>
                {/if}
                <span><strong>{record.foundFiles}</strong> files</span>
                <span class="stat-separator">·</span>
                <span><strong>{record.lockedFiles}</strong> locked</span>
                <span class="stat-separator">·</span>
                <span><strong>{record.distinctPeers}</strong> peers</span>
              </span>
            </button>
            <button type="button" class="search-history-remove" aria-label={`${searchIsActive(record) ? 'Cancel' : 'Remove'} ${record.displayQuery}`} title={searchIsActive(record) ? 'Cancel' : 'Remove'} onclick={() => handleSearchAction(record)}>
              <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M6 6l8 8M14 6l-8 8" /></svg>
            </button>
          </div>
        {:else}
          <JobCompactRow
            job={entry.job}
            allJobs={automaticJobs}
            titleOverride={entry.root.title}
            contextOverride={entry.root.kind === 'extract' ? `${extractSourceLabel(entry.root.payload.sourceType)} import` : undefined}
            whenOverride={entry.root.when}
            onclick={() => onopenjob(entry.job)}
            onaction={() => handleAutomaticJobAction(entry.job)}
          />
        {/if}
      {:else}
        <div class="empty-state">No jobs yet.</div>
      {/each}
    </div>
    {#if listEntries.length > historyLimit}
      <LoadMoreButton label="Load earlier jobs" onclick={() => (historyLimit = Math.min(listEntries.length, historyLimit + JOB_HISTORY_PAGE_SIZE))} />
    {/if}
    {/if}
  {:else if activeRecord}
    {@const resultState = resultResourceState(activeRecord)}
    {@const aggregateResults = aggregateMode ? aggregateGroups(activeRecord) : []}
    {@const projection = aggregateMode ? null : currentResultProjection(activeRecord)}
    {@const allVisibleResults = projection?.items ?? []}
    {@const groups = groupAdjacent(allVisibleResults)}
    <header class="search-results-heading">
      <button type="button" class="icon-button back-button" aria-label="Back to jobs" onclick={onshowlist}>
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M12.5 4.5L7 10l5.5 5.5M7.5 10H16" /></svg>
      </button>
      <div class="search-results-title">
        <p class="eyebrow">{searchModeLabel(activeMode)}</p>
        <h1>{activeRecord.displayQuery}</h1>
      </div>
      <div class="search-results-summary">
        <span class={`search-status-badge ${activeRecord.status}`}><i></i>{statusLabel(activeRecord.status)}</span>
        {#if genericMode}<span>{projection?.totalCount ?? 0} directories</span>{/if}
        {#if aggregateMode}<span>{aggregateResults.length} groups</span>{/if}
        <span>{activeRecord.foundFiles} files</span>
        <span>{activeRecord.lockedFiles} locked</span>
        <span>{activeRecord.distinctPeers} peers</span>
      </div>
      <div class="search-results-actions">
        <button type="button" class="search-again-button" title="Run this search again" onclick={() => onsearchagain(activeRecord)}>
          <Icon name="search" />
          <span>Search again</span>
        </button>
        <button type="button" class="delete-search-button" aria-label={`${searchIsActive(activeRecord) ? 'Cancel' : 'Delete'} ${activeRecord.displayQuery}`} title={searchIsActive(activeRecord) ? 'Cancel job' : 'Delete search'} onclick={() => handleSearchAction(activeRecord)}>
          <Icon name={searchIsActive(activeRecord) ? 'x' : 'trash'} />
          <span>{searchIsActive(activeRecord) ? 'Cancel' : 'Delete'}</span>
        </button>
      </div>
    </header>

    {#if resultState.blocking}
      <div class="empty-state"><strong>{resultState.title}</strong><p>{resultState.detail}</p></div>
    {:else}
      <ResourceStateNotice state={resultState} />
      <MutationStatus state={mutation} />

    <div class="result-refine-wrap">
      <div class="result-refine-row">
        <ResultFilterControl bind:value={filterText} placeholder={genericMode ? "Filter files or directories…" : "Filter results…"} ariaLabel="Filter search results" />

        {#if aggregateMode}
          {@const allAggregatesSelected = allAggregateSelected(activeRecord)}
          <button
            type="button"
            class="aggregate-select-all-button"
            disabled={aggregateResults.length === 0}
            onclick={() => setAllAggregate(activeRecord, !allAggregatesSelected)}
          >{allAggregatesSelected ? 'Deselect all' : 'Select all'}</button>
        {:else}
          <button type="button" class:active={conditionsOpen} class="edit-conditions-button" aria-expanded={conditionsOpen} onclick={() => (conditionsOpen = !conditionsOpen)}>
            <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M4 5h12M4 10h12M4 15h12"/><circle cx="8" cy="5" r="1.6"/><circle cx="13" cy="10" r="1.6"/><circle cx="7" cy="15" r="1.6"/></svg>
            Conditions
          </button>

          <div class="result-sort-control">
            <label for="result-sort">Sort</label>
            <select id="result-sort" value={sort} onchange={changeSort}>
              <option value="relevance">Relevance</option>
              <option value="speed">Upload speed</option>
              {#if genericMode}
                <option value="size">Directory size</option>
                <option value="count">File count</option>
                <option value="name">Directory name</option>
              {:else}
                <option value="queue">Queue depth</option>
                <option value="size">Item size</option>
              {/if}
            </select>
            {#if sort === 'size' || (genericMode && (sort === 'count' || sort === 'name'))}
              <button type="button" class="size-direction-button" aria-label={`Reverse ${genericMode ? 'directory' : 'item'} sort`} onclick={() => (sizeDirection = sizeDirection === 'desc' ? 'asc' : 'desc')}>
                <svg class:ascending={sizeDirection === 'asc'} viewBox="0 0 20 20" aria-hidden="true"><path d="M10 4v12M6 8l4-4 4 4" /></svg>
              </button>
            {/if}
          </div>
        {/if}
      </div>

      {#if !aggregateMode && hasAppliedConditions(activeMode, activeRecord.conditions)}
        <div class="result-condition-pills">
          <SearchConditionPills mode={activeMode} bind:conditions={activeRecord.conditions} />
        </div>
      {/if}

      {#if !aggregateMode && conditionsOpen}
        <button type="button" class="results-config-backdrop" aria-label="Close search configuration" onclick={() => (conditionsOpen = false)}></button>
        <section class="search-config-popover results-config-popover" aria-label="Result search configuration">
          <SearchConfigPanel mode={activeMode} bind:conditions={activeRecord.conditions} title="Search configuration" initialTab="conditions" onclose={() => (conditionsOpen = false)} />
        </section>
      {/if}
    </div>

    {@const selectedSummary = selectionSummary()}
    <SelectionToolbar
      selectedCount={selectedSummary.requestedCount}
      floatingLabel={`Download ${selectedSummary.resolvablePublicCount}`}
      detail={selectedSummary.lockedCount ? `${selectedSummary.requestedCount} selected · ${selectedSummary.lockedCount} locked` : undefined}
      actionDisabled={selectedSummary.resolvablePublicCount === 0}
      onclear={() => { selected = new Set(); selectedAggregateGroups = new Set(); selectedAggregateFiles = new Set(); }}
      onaction={requestSelectedDownload}
    />

    {#if aggregateMode}
      {#if aggregateResults.length === 0 && resultState.phase === 'loading'}
        <!-- The resource-state notice above is the loading treatment until the first group arrives. -->
      {:else if aggregateResults.length === 0}
        <div class="search-results-empty">
          <strong>No matching groups</strong>
          <span>Adjust the text filter.</span>
        </div>
      {:else}
        <div class="aggregate-results-list">
          {#each aggregateResults as aggregateGroup (aggregateGroup.id)}
            {@render aggregateGroupCard(aggregateGroup)}
          {/each}
        </div>
      {/if}
    {:else if allVisibleResults.length === 0 && resultState.phase === 'loading'}
      <!-- The resource-state notice above is the loading treatment until the first result arrives. -->
    {:else if allVisibleResults.length === 0}
      <div class="search-results-empty">
        <strong>{genericMode ? 'No matching directories' : 'No matching results'}</strong>
        <span>Adjust the text filter or result conditions.</span>
      </div>
    {:else if sort === 'relevance' && !genericMode}
      {@const preferredGroups = tierGroups(groups, true)}
      {@const otherGroups = tierGroups(groups, false)}
      {#if preferredGroups.length}
        <div class="result-tier-heading preferred">
          <span>Preferred matches</span>
          <small>{tierItemCount(preferredGroups)}</small>
        </div>
        <div class="result-tier preferred-tier">
          {#each preferredGroups as group (group.key)}
            {@render peerGroup(group)}
          {/each}
        </div>
      {/if}
      {#if otherGroups.length}
        <div class="result-tier-heading other">
          <span>Other matches</span>
          <small>{tierItemCount(otherGroups)}</small>
        </div>
        <div class="result-tier">
          {#each otherGroups as group (group.key)}
            {@render peerGroup(group)}
          {/each}
        </div>
      {/if}
    {:else}
      <div class="result-tier">
        {#each groups as group (group.key)}
          {@render peerGroup(group)}
        {/each}
      </div>
    {/if}

    {#if projection?.nextCursor}
      <LoadMoreButton label="Load more results" loadingLabel="Loading results…" onclick={() => (resultPagesRequested += 1)} />
    {/if}
    {/if}

  {:else if activeAutomaticJob}
    <AutomaticJobDetail job={activeAutomaticJob} allJobs={automaticJobs} {userActions} {onopenjob} onjobaction={handleAutomaticJobAction} onback={onshowlist} />
  {/if}
</section>

{#if newJobOpen}
  <div class="new-job-modal">
    <button type="button" class="new-job-modal-backdrop" aria-label="Close new job" onclick={() => (newJobOpen = false)}></button>
    <div class="new-job-modal-dialog" role="dialog" aria-modal="true" aria-label="New job">
      <NewJobComposer onclose={() => (newJobOpen = false)} onstart={startPreviewJobs} />
    </div>
  </div>
{/if}

{#if aggregateOptionsGroupId && activeRecord && aggregateMode}
  {@const optionGroup = aggregateGroups(activeRecord).find((group) => group.id === aggregateOptionsGroupId)}
  {#if optionGroup}
    <div class="aggregate-options-modal">
      <button type="button" class="aggregate-options-backdrop" aria-label="Close options" onclick={() => (aggregateOptionsGroupId = null)}></button>
      <div class="aggregate-options-dialog" role="dialog" aria-modal="true" aria-label={`${optionGroup.itemName} options`}>
        <header class="aggregate-options-header">
          <div>
            <strong>{optionGroup.itemName}</strong>
            <small>{optionGroup.artist ? `${optionGroup.artist} · ` : ''}{optionGroup.shareCount} shares · {optionGroup.options.length} options</small>
          </div>
          <button type="button" class="aggregate-options-close" aria-label="Close options" onclick={() => (aggregateOptionsGroupId = null)}>×</button>
        </header>
        <div class="aggregate-options-list">
          {#each optionGroup.options as option (option.id)}
            {@const displayOption = option.kind === 'album' ? albumWithRetrieval(option) : option}
            <div class:current={aggregateRepresentative(optionGroup).id === displayOption.id} class="aggregate-option">
              <div class="aggregate-option-toolbar">
                {@render aggregatePeerSummary(displayOption.peer)}
                <button type="button" class="aggregate-use-option" onclick={() => chooseAggregateOption(optionGroup, displayOption)}>Use this option</button>
              </div>
              <div class="aggregate-option-card-wrap">
                <button type="button" class="aggregate-option-card-picker" aria-label={`Use ${displayOption.path}`} onclick={() => chooseAggregateOption(optionGroup, displayOption)}></button>
                {#if displayOption.kind === 'track'}
                  <FileItemCard path={displayOption.path} sizeBytes={displayOption.sizeBytes} audio={displayOption.audio} locked={displayOption.locked} />
                {:else}
                  <FolderItemCard
                    path={displayOption.path}
                    sizeBytes={displayOption.sizeBytes}
                    files={displayOption.files}
                    totalFileCount={displayOption.totalFileCount}
                    filesComplete
                    contentsState={albumContentsState(displayOption)}
                    locked={displayOption.locked}
                    onloadfullcontents={() => loadFullAlbumFolder(displayOption, optionGroup)}
                  />
                {/if}
              </div>
            </div>
          {/each}
        </div>
      </div>
    </div>
  {/if}
{/if}


{#snippet aggregatePeerSummary(peer: ProjectedSearchResult['peer'])}
  <div class="aggregate-peer-summary">
    <span class="aggregate-peer-username"><UsernameLink username={peer.username} actions={userActions} /></span>
    <span class="aggregate-peer-speed"><strong>{peer.uploadSpeedMbps.toFixed(1)} MB/s</strong></span>
    <span class:available={peer.freeUploadSlot} class="aggregate-peer-slot"><i></i>{peer.freeUploadSlot ? 'Free slot' : 'No free slot'}</span>
  </div>
{/snippet}

{#snippet aggregateGroupCard(group: AggregateSearchGroup)}
  {@const representative = aggregateRepresentative(group)}
  <section class="aggregate-result-group" class:selected={aggregateGroupSelected(group)} class:partial={aggregateGroupPartial(group)}>
    <header class="aggregate-result-header">
      <button
        type="button"
        class="aggregate-header-select-button"
        aria-label={`${aggregateGroupSelected(group) ? 'Deselect' : 'Select'} ${group.itemName}`}
        aria-pressed={aggregateGroupSelected(group)}
        onclick={() => toggleAggregateGroup(group, !aggregateGroupSelected(group))}
      ></button>
      <div class="aggregate-result-identity">
        <strong>{group.itemName}</strong>
        {#if group.artist}<small>{group.artist}</small>{/if}
      </div>
      <div class="aggregate-result-source">
        {@render aggregatePeerSummary(representative.peer)}
      </div>
      <div class="aggregate-result-stats">
        <button type="button" class="aggregate-options-button" onclick={() => (aggregateOptionsGroupId = group.id)}>{group.options.length} options</button>
      </div>
    </header>
    {#if representative.kind === 'track'}
      <FileItemCard
        path={representative.path}
        sizeBytes={representative.sizeBytes}
        audio={representative.audio}
        locked={representative.locked}
        selected={aggregateGroupSelected(group)}
        selectable
        onselect={(checked) => toggleAggregateGroup(group, checked)}
      />
    {:else}
      <FolderItemCard
        path={representative.path}
        sizeBytes={representative.sizeBytes}
        files={representative.files}
        totalFileCount={representative.totalFileCount}
        filesComplete
        contentsState={albumContentsState(representative)}
        locked={representative.locked}
        selected={aggregateGroupSelected(group)}
        partial={aggregateGroupPartial(group)}
        selectable
        selectedFileIds={aggregateSelectedFileIds(group)}
        onselectall={(checked) => toggleAggregateGroup(group, checked)}
        onselectfile={(file, checked) => { const original = representative.files.find((candidate) => candidate.id === file.id); if (original) toggleAggregateAlbumFile(group, original, checked); }}
        onloadfullcontents={() => loadFullAlbumFolder(representative, group)}
      />
    {/if}
  </section>
{/snippet}

{#snippet peerGroup(group: PeerGroup)}
  <PeerItemGroup peer={group.peer} itemCount={group.items.length} itemNoun={genericMode ? 'directory' : 'result'} itemNounPlural={genericMode ? 'directories' : 'results'} {userActions}>
    {#each group.items as result (result.id)}
      {#if result.kind === 'track'}
        <FileItemCard
          path={result.path}
          sizeBytes={result.sizeBytes}
          audio={result.audio}
          locked={result.locked}
          selected={selected.has(selectedKey(result))}
          preferred={group.preferred && sort === 'relevance'}
          selectable
          onselect={(checked) => toggleSelection(selectedKey(result), checked)}
        />
      {:else if result.kind === 'generic-directory'}
        <FolderItemCard
          path={result.path}
          sizeBytes={result.sizeBytes}
          files={result.files}
          totalFileCount={result.totalFileCount}
          filesComplete
          fileLayout="tree"
          contentsState={genericContentsState(result)}
          locked={result.locked}
          selected={isGenericDirectoryFullySelected(result)}
          partial={isGenericDirectoryPartiallySelected(result)}
          selectable
          selectedFileIds={selectedFileIdsForGenericDirectory(result)}
          onselectall={(checked) => toggleGenericDirectory(result, checked)}
          onselectfiles={(files, checked) => toggleGenericFiles(result, files, checked)}
          onselectfile={(file, checked) => { const original = result.files.find((candidate) => candidate.id === file.id); if (original) toggleSelection(selectedGenericFileKey(result, original), checked); }}
          onloadfullcontents={() => loadFullGenericDirectory(result)}
        />
      {:else}
        <FolderItemCard
          path={result.path}
          sizeBytes={result.sizeBytes}
          files={result.files}
          totalFileCount={result.totalFileCount}
          filesComplete
          contentsState={albumContentsState(result)}
          locked={result.locked}
          selected={isAlbumFullySelected(result)}
          partial={isAlbumPartiallySelected(result)}
          preferred={group.preferred && sort === 'relevance'}
          selectable
          selectedFileIds={selectedFileIdsForAlbum(result)}
          onselectall={(checked) => toggleAlbum(result, checked)}
          onselectfile={(file, checked) => { const original = result.files.find((candidate) => candidate.id === file.id); if (original) toggleSelection(selectedAlbumFileKey(result, original), checked); }}
          onloadfullcontents={() => loadFullAlbumFolder(result)}
        />
      {/if}
    {/each}
  </PeerItemGroup>
{/snippet}
