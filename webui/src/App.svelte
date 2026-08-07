<script lang="ts">
  import type { components } from './api/generated';

  type ApiSchemaName = keyof components['schemas'];
  type StateSnapshotDto = components['schemas']['StateSnapshotDto'];

  const generatedSchemaProbe: ApiSchemaName = 'StateSnapshotDto';
  const snapshotProbe: Pick<StateSnapshotDto, 'capturedAtUtc' | 'transfers'> = {
    capturedAtUtc: new Date(0).toISOString(),
    transfers: [],
  };
</script>

<main>
  <h1>Sockseek WebUI prototype</h1>
  <p>Svelte is mounted and the generated Sockseek OpenAPI types are accessible.</p>
  <dl>
    <div>
      <dt>Generated schema probe</dt>
      <dd><code>{generatedSchemaProbe}</code></dd>
    </div>
    <div>
      <dt>Snapshot transfer count</dt>
      <dd>{snapshotProbe.transfers.length}</dd>
    </div>
  </dl>
</main>
