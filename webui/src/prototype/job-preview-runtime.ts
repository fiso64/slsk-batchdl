import { prototypeUuid } from './ids';
import type {
  AlbumAggregateJobRecord,
  AlbumJobRecord,
  AggregateJobRecord,
  AutomaticJobRecord,
  ExtractJobRecord,
  GenericJobRecord,
  JobListRecord,
  RemoteDirectoryJobRecord,
  RemoteFileJobRecord,
  RetrieveFolderJobRecord,
  SongJobRecord,
} from './jobs';
import type { JobPreviewNode, JobPreviewPlan } from './job-preview';

let createdJobSequence = 500;

function selectedPreviewTree(node: JobPreviewNode, selectedLeaves: Set<string>): JobPreviewNode | null {
  if (!node.children.length) return selectedLeaves.has(node.ref) ? { ...node } : null;
  const children = node.children
    .map((child) => selectedPreviewTree(child, selectedLeaves))
    .filter((child): child is JobPreviewNode => child !== null);
  if (!children.length) return null;
  return { ...node, children };
}

function runtimeJobFromPreview(
  node: JobPreviewNode,
  workflow: string,
  parentJobId: string | null,
  createdAtUtc: string,
): AutomaticJobRecord[] {
  const id = prototypeUuid(0x51000000, createdJobSequence++);
  const base = {
    id,
    workflowId: workflow,
    parentJobId,
    title: node.title,
    subtitle: node.detail,
    status: 'pending' as const,
    createdAtUtc,
    when: 'just now',
    lifetime: 'live' as const,
  };

  if (node.kind === 'extract') {
    const resultNode = node.children[0] ?? null;
    const resultRecords = resultNode ? runtimeJobFromPreview(resultNode, workflow, null, createdAtUtc) : [];
    const resultId = resultRecords[0]?.id ?? null;
    const record: ExtractJobRecord = {
      ...base,
      kind: 'extract',
      payload: { sourceType: node.sourceType ?? 'string', input: node.detail ?? node.title, resultJobId: resultId },
    };
    return [record, ...resultRecords];
  }

  if (node.kind === 'job-list') {
    const record: JobListRecord = {
      ...base,
      kind: 'job-list',
      progress: { completed: 0, total: node.children.length },
      payload: { name: node.title, childCount: node.children.length, succeeded: 0, failed: 0 },
    };
    const children = node.children.flatMap((child) => runtimeJobFromPreview(child, workflow, id, createdAtUtc));
    return [record, ...children];
  }

  if (node.kind === 'song') {
    const parts = node.title.split(' — ');
    const artist = parts.length > 1 ? parts.shift() ?? '' : '';
    const title = parts.length > 0 ? parts.join(' — ') : node.title;
    const record: SongJobRecord = { ...base, kind: 'song', payload: { artist, title, candidateCount: 0 } };
    return [record];
  }
  if (node.kind === 'album') {
    const parts = node.title.split(' — ');
    const artist = parts.length > 1 ? parts.shift() ?? '' : '';
    const album = parts.length > 0 ? parts.join(' — ') : node.title;
    const record: AlbumJobRecord = { ...base, kind: 'album', payload: { artist, album, resultCount: 0, files: [] } };
    return [record];
  }
  if (node.kind === 'aggregate') {
    const record: AggregateJobRecord = { ...base, kind: 'aggregate', payload: { artist: node.title, songCount: 0, succeeded: 0, failed: 0 } };
    return [record];
  }
  if (node.kind === 'album-aggregate') {
    const record: AlbumAggregateJobRecord = { ...base, kind: 'album-aggregate', payload: { artist: node.title, albumCount: 0, succeeded: 0, failed: 0 } };
    return [record];
  }
  if (node.kind === 'remote-directory') {
    const record: RemoteDirectoryJobRecord = {
      ...base,
      kind: 'remote-directory',
      payload: { username: 'nightshift', folderPath: node.title.split(' — ').at(-1) ?? node.title, files: [] },
    };
    return [record];
  }
  if (node.kind === 'remote-file') {
    const record: RemoteFileJobRecord = { ...base, kind: 'remote-file', payload: { username: 'nightshift', path: node.title, sizeBytes: 0 } };
    return [record];
  }
  if (node.kind === 'retrieve-folder') {
    const record: RetrieveFolderJobRecord = {
      ...base,
      kind: 'retrieve-folder',
      payload: { username: 'nightshift', folderPath: node.title, newFilesFoundCount: 0, outcome: 'retrieving' },
    };
    return [record];
  }
  const record: GenericJobRecord = { ...base, kind: 'generic', payload: { text: node.detail ?? node.title } };
  return [record];
}

/** Prototype-only adapter: commits a reviewed preview into local runtime fixtures. */
export function commitPreview(
  plan: JobPreviewPlan,
  selectedLeaves: Set<string>,
): { records: AutomaticJobRecord[]; rootId: string | null } {
  const selectedRoots = plan.roots
    .map((root) => selectedPreviewTree(root, selectedLeaves))
    .filter((root): root is JobPreviewNode => root !== null);
  if (!selectedRoots.length) return { records: [], rootId: null };

  const workflow = prototypeUuid(0x51010000, createdJobSequence++);
  const createdAtUtc = new Date().toISOString();
  const records = selectedRoots.flatMap((root) => runtimeJobFromPreview(root, workflow, null, createdAtUtc));
  return { records, rootId: records[0]?.id ?? null };
}
