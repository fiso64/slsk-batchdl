<script lang="ts">
  import type { PrototypeScenario } from '../mock/types';

  interface Props {
    scenario: PrototypeScenario;
  }

  let { scenario }: Props = $props();

  const conversations = [
    { initials: 'sm', name: 'silvermachine', preview: 'yeah, I have the FLAC rip', unread: 2 },
    { initials: 'tl', name: 'tape_loop', preview: 'thanks!', unread: 0 },
    { initials: 'nr', name: 'neonrain', preview: 'try searching the catalog no.', unread: 0 },
  ];
</script>

<section class="page chat-page">
  <div class="chat-shell">
    <aside class="conversation-list">
      <div class="conversation-heading">Messages</div>
      {#each conversations as conversation, index}
        <button type="button" class:active={index === 0} class="conversation">
          <span class="avatar">{conversation.initials}</span>
          <span class="conversation-copy">
            <strong>{conversation.name}</strong>
            <small>{conversation.preview}</small>
          </span>
          {#if conversation.unread > 0}<span class="unread-count">{conversation.unread}</span>{/if}
        </button>
      {/each}
    </aside>

    <div class="thread">
      <header class="thread-heading">
        <strong>silvermachine</strong>
        <small>{scenario.soulseek === 'ready' ? 'online' : scenario.soulseek}</small>
      </header>
      <div class="messages">
        <div class="message theirs">I think I have the original FLAC rip.</div>
        <div class="message mine">Nice — looking for the Geogaddi version specifically.</div>
        <div class="message theirs">yeah, I have the FLAC rip</div>
      </div>
      <div class="composer">
        <input aria-label="Message" placeholder="Message silvermachine…" disabled={scenario.connection === 'offline'} />
        <button type="button" disabled={scenario.connection === 'offline'}>Send</button>
      </div>
    </div>
  </div>
</section>
