<script lang="ts">
  interface Props {
    username: string;
    onopenuser: (username: string) => void;
    title?: string;
  }

  let { username, onopenuser, title }: Props = $props();

  function open(event: MouseEvent): void {
    event.stopPropagation();
    onopenuser(username);
  }
</script>

<button
  type="button"
  class="username-link"
  title={title ?? `View ${username}`}
  aria-label={`View ${username}'s profile`}
  onclick={open}
>{username}</button>
