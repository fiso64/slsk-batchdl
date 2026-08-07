<script lang="ts">
  interface Props {
    title: string;
  }

  let { title }: Props = $props();
</script>

<section class="placeholder-page">
  <h2>{title}</h2>
  <p>This page is intentionally empty for now. It exists only to establish the prototype navigation.</p>
</section>
