<script lang="ts">
  import Icon from '../components/Icon.svelte';
  import LinkifiedText from '../components/LinkifiedText.svelte';
  import UsernameLink from '../components/UsernameLink.svelte';
  import type { PrototypeScenario } from '../mock/types';
  import {
    chatInitials,
    chatPreview,
    createPrototypeChatConversations,
    createPrototypeChatRooms,
    type PrototypeChatConversation,
    type PrototypeChatMessage,
    type PrototypeChatRoom,
    type PrototypeChatTarget,
  } from '../prototype/chat';

  interface Props {
    scenario: PrototypeScenario;
    onopenuser: (username: string) => void;
    initialUsername?: string | null;
  }

  let { scenario, onopenuser, initialUsername = null }: Props = $props();

  let rooms = $state<PrototypeChatRoom[]>(createPrototypeChatRooms());
  let conversations = $state<PrototypeChatConversation[]>(createPrototypeChatConversations());
  let target = $state<PrototypeChatTarget>({ kind: 'user', id: 'dm-silvermachine' });
  let draft = $state('');
  let adding = $state<'room' | 'user' | null>(null);
  let addDraft = $state('');
  let menuOpen = $state(false);
  let blockedUsers = $state<Set<string>>(new Set());
  let initialUsernameHandled = '';
  let loadedScenarioId = $state<PrototypeScenario['id'] | null>(null);
  let composerTextarea = $state<HTMLTextAreaElement | null>(null);
  let messagesElement = $state<HTMLDivElement | null>(null);
  let chatOptionsElement = $state<HTMLDivElement | null>(null);

  let activeRoom = $derived(target.kind === 'room' ? rooms.find((room) => room.id === target.id) ?? null : null);
  let activeConversation = $derived(target.kind === 'user' ? conversations.find((conversation) => conversation.id === target.id) ?? null : null);
  let activeMessages = $derived(activeRoom?.messages ?? activeConversation?.messages ?? []);
  let activeUsername = $derived(activeConversation?.username ?? null);
  let activeUserBlocked = $derived(activeUsername ? blockedUsers.has(activeUsername) : false);
  let composerDisabled = $derived(scenario.connection === 'offline' || activeUserBlocked || (!activeRoom && !activeConversation));

  $effect(() => {
    const nextScenarioId = scenario.id;
    if (nextScenarioId === loadedScenarioId) return;
    loadedScenarioId = nextScenarioId;
    rooms = createPrototypeChatRooms(nextScenarioId);
    conversations = createPrototypeChatConversations(nextScenarioId);
    target = { kind: 'user', id: 'dm-silvermachine' };
    draft = '';
    adding = null;
    addDraft = '';
    menuOpen = false;
    blockedUsers = new Set();
    scheduleComposerResize();
    scheduleScrollToBottom();
  });

  $effect(() => {
    const username = initialUsername?.trim();
    if (!username || username === initialUsernameHandled) return;
    initialUsernameHandled = username;
    openOrCreateConversation(username);
  });

  function scheduleComposerResize(): void {
    requestAnimationFrame(() => resizeComposer(composerTextarea));
  }

  function scheduleScrollToBottom(): void {
    requestAnimationFrame(() => {
      if (messagesElement) messagesElement.scrollTop = messagesElement.scrollHeight;
    });
  }

  function selectTarget(next: PrototypeChatTarget): void {
    target = next;
    menuOpen = false;
    draft = '';
    scheduleComposerResize();
    scheduleScrollToBottom();
    if (next.kind === 'room') {
      rooms = rooms.map((room) => room.id === next.id ? { ...room, unread: 0 } : room);
    } else {
      conversations = conversations.map((conversation) => conversation.id === next.id ? { ...conversation, unread: 0 } : conversation);
    }
  }

  function openOrCreateConversation(username: string): void {
    const normalized = username.trim();
    if (!normalized) return;
    const existing = conversations.find((conversation) => conversation.username.toLowerCase() === normalized.toLowerCase());
    if (existing) {
      selectTarget({ kind: 'user', id: existing.id });
      return;
    }

    const id = `dm-${normalized.toLowerCase().replace(/[^a-z0-9]+/g, '-')}-${Date.now()}`;
    const next: PrototypeChatConversation = {
      id,
      username: normalized,
      presence: scenario.soulseek === 'ready' ? 'online' : 'offline',
      unread: 0,
      messages: [],
    };
    conversations = [next, ...conversations];
    selectTarget({ kind: 'user', id });
  }

  function joinRoom(name: string): void {
    const normalized = name.trim();
    if (!normalized) return;
    const existing = rooms.find((room) => room.name.toLowerCase() === normalized.toLowerCase());
    if (existing) {
      selectTarget({ kind: 'room', id: existing.id });
      return;
    }

    const id = `room-${normalized.toLowerCase().replace(/[^a-z0-9]+/g, '-')}-${Date.now()}`;
    const next: PrototypeChatRoom = {
      id,
      name: normalized,
      kind: 'public',
      memberCount: 0,
      unread: 0,
      messages: [],
    };
    rooms = [next, ...rooms];
    selectTarget({ kind: 'room', id });
  }

  function submitAdd(): void {
    if (adding === 'room') joinRoom(addDraft);
    else if (adding === 'user') openOrCreateConversation(addDraft);
    addDraft = '';
    adding = null;
  }

  function beginAdd(kind: 'room' | 'user'): void {
    adding = adding === kind ? null : kind;
    addDraft = '';
  }

  function createMessage(text: string): PrototypeChatMessage {
    return {
      id: `local-${Date.now()}-${Math.random().toString(36).slice(2)}`,
      sender: 'fi',
      text,
      mine: true,
      time: 'now',
    };
  }

  function sendMessage(): void {
    const text = draft.trim();
    if (!text || composerDisabled) return;
    const message = createMessage(text);

    if (activeRoom) {
      rooms = rooms.map((room) => room.id === activeRoom!.id ? { ...room, messages: [...room.messages, message] } : room);
    } else if (activeConversation) {
      conversations = conversations.map((conversation) => conversation.id === activeConversation!.id
        ? { ...conversation, messages: [...conversation.messages, message] }
        : conversation);
    }
    draft = '';
    scheduleComposerResize();
    scheduleScrollToBottom();
  }

  function resizeComposer(node: HTMLTextAreaElement | null): void {
    if (!node) return;
    node.style.height = 'auto';
    const minimum = 38;
    const maximum = 156;
    node.style.height = `${Math.max(minimum, Math.min(node.scrollHeight, maximum))}px`;
    node.style.overflowY = node.scrollHeight > maximum ? 'auto' : 'hidden';
  }

  function handleComposerInput(event: Event): void {
    resizeComposer(event.currentTarget as HTMLTextAreaElement);
  }

  function handleComposerKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      sendMessage();
    }
  }

  function handleAddKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter') {
      event.preventDefault();
      submitAdd();
    } else if (event.key === 'Escape') {
      adding = null;
      addDraft = '';
    }
  }

  function handleWindowKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Escape') return;
    if (menuOpen) menuOpen = false;
    if (adding) {
      adding = null;
      addDraft = '';
    }
  }

  function handleWindowPointerdown(event: PointerEvent): void {
    if (!menuOpen || !chatOptionsElement) return;
    const eventTarget = event.target;
    if (eventTarget instanceof Node && !chatOptionsElement.contains(eventTarget)) menuOpen = false;
  }

  function blockActiveUser(): void {
    if (!activeConversation) return;
    const next = new Set(blockedUsers);
    if (next.has(activeConversation.username)) next.delete(activeConversation.username);
    else next.add(activeConversation.username);
    blockedUsers = next;
    menuOpen = false;
  }

  function deleteActiveConversationHistory(): void {
    if (!activeConversation) return;
    conversations = conversations.map((conversation) => conversation.id === activeConversation.id
      ? { ...conversation, messages: [], unread: 0 }
      : conversation);
    menuOpen = false;
  }

  function leaveActiveRoom(): void {
    if (!activeRoom) return;
    const roomId = activeRoom.id;
    rooms = rooms.filter((room) => room.id !== roomId);
    menuOpen = false;
    const fallbackRoom = rooms[0];
    const fallbackUser = conversations[0];
    if (fallbackRoom) selectTarget({ kind: 'room', id: fallbackRoom.id });
    else if (fallbackUser) selectTarget({ kind: 'user', id: fallbackUser.id });
  }

</script>

<svelte:window onkeydown={handleWindowKeydown} onpointerdown={handleWindowPointerdown} />

<section class="page chat-page">
  <div class="chat-layout">
    <aside class="chat-sidebar" aria-label="Chat destinations">
      <section class="chat-sidebar-section">
        <header class="chat-section-heading">
          <span>Rooms</span>
          <button type="button" class="chat-add-button" aria-label="Join room" title="Join room" onclick={() => beginAdd('room')}>+</button>
        </header>
        {#if adding === 'room'}
          <div class="chat-add-row">
            <input aria-label="Room name" placeholder="Room name…" bind:value={addDraft} onkeydown={handleAddKeydown} />
            <button type="button" disabled={!addDraft.trim()} onclick={submitAdd}>Join</button>
          </div>
        {/if}
        <div class="chat-destination-list">
          {#each rooms as room (room.id)}
            <button
              type="button"
              class:active={target.kind === 'room' && target.id === room.id}
              class="chat-destination room"
              aria-current={target.kind === 'room' && target.id === room.id ? 'page' : undefined}
              onclick={() => selectTarget({ kind: 'room', id: room.id })}
            >
              <span class="room-mark">#</span>
              <span class="chat-destination-copy">
                <strong>{room.name}</strong>
                <small>{chatPreview(room.messages)}</small>
              </span>
              {#if room.unread > 0}<span class="unread-count">{room.unread}</span>{/if}
            </button>
          {/each}
        </div>
      </section>

      <section class="chat-sidebar-section users-section">
        <header class="chat-section-heading">
          <span>Users</span>
          <button type="button" class="chat-add-button" aria-label="Message user" title="Message user" onclick={() => beginAdd('user')}>+</button>
        </header>
        {#if adding === 'user'}
          <div class="chat-add-row">
            <input aria-label="Username" placeholder="Username…" bind:value={addDraft} onkeydown={handleAddKeydown} />
            <button type="button" disabled={!addDraft.trim()} onclick={submitAdd}>Open</button>
          </div>
        {/if}
        <div class="chat-destination-list">
          {#each conversations as conversation (conversation.id)}
            <div
              class:active={target.kind === 'user' && target.id === conversation.id}
              class="chat-destination user"
            >
              <button
                type="button"
                class="chat-destination-open"
                aria-label={`Open conversation with ${conversation.username}`}
                aria-current={target.kind === 'user' && target.id === conversation.id ? 'page' : undefined}
                onclick={() => selectTarget({ kind: 'user', id: conversation.id })}
              ></button>
              <span class="avatar">{chatInitials(conversation.username)}</span>
              <span class="chat-destination-copy">
                <strong>{conversation.username}</strong>
                <small>{chatPreview(conversation.messages)}</small>
              </span>
              {#if conversation.unread > 0}<span class="unread-count">{conversation.unread}</span>{/if}
            </div>
          {/each}
        </div>
      </section>
    </aside>

    <div class="chat-thread">
      <header class="chat-thread-heading">
        {#if activeConversation}
          <div class="chat-thread-identity">
            <UsernameLink username={activeConversation.username} {onopenuser} />
            <small class:blocked={activeUserBlocked}>{activeUserBlocked ? 'blocked' : activeConversation.presence}</small>
          </div>
        {:else if activeRoom}
          <div class="chat-thread-identity">
            <strong>#{activeRoom.name}</strong>
            <small>{activeRoom.kind === 'private' ? 'Private room' : 'Public room'} · {activeRoom.memberCount.toLocaleString()} users</small>
          </div>
        {/if}

        <div class="chat-thread-options" bind:this={chatOptionsElement}>
          <button type="button" class="chat-options-button" aria-label="Chat options" aria-expanded={menuOpen} onclick={() => (menuOpen = !menuOpen)}><Icon name="more" /></button>
          {#if menuOpen}
            <div class="chat-options-menu">
              {#if activeConversation}
                <button type="button" onclick={blockActiveUser}>{activeUserBlocked ? 'Unblock user' : 'Block user'}</button>
                <button type="button" class="danger" onclick={deleteActiveConversationHistory}>Delete messages</button>
              {:else if activeRoom}
                <button type="button" class="danger" onclick={leaveActiveRoom}>Leave room</button>
              {/if}
            </div>
          {/if}
        </div>
      </header>

      <div class="chat-messages" aria-live="polite" bind:this={messagesElement}>
        {#if activeMessages.length === 0}
          <div class="chat-empty-thread">
            <strong>No messages yet</strong>
            <span>{activeRoom ? 'Messages sent while Sockseek is joined will appear here.' : 'Send a message to start the conversation.'}</span>
          </div>
        {:else if activeRoom}
          {#each activeMessages as message (message.id)}
            <div class:mine={message.mine} class="room-message">
              <div class="room-message-meta">
                {#if message.mine}
                  <span class="room-message-self">fi</span>
                {:else}
                  <UsernameLink username={message.sender} {onopenuser} />
                {/if}
                <time>{message.time}</time>
              </div>
              <div class="message-body"><LinkifiedText text={message.text} /></div>
            </div>
          {/each}
        {:else if activeConversation}
          {#each activeMessages as message (message.id)}
            <div class:mine={message.mine} class="direct-message-row">
              <div class="direct-message-bubble">
                <div class="message-body"><LinkifiedText text={message.text} /></div>
                <time>{message.time}</time>
              </div>
            </div>
          {/each}
        {/if}
      </div>

      <div class="chat-composer">
        {#if activeUserBlocked}
          <span class="composer-state">Unblock {activeUsername} to send messages.</span>
        {/if}
        <div class="chat-composer-row">
          <textarea
            aria-label="Message"
            rows="1"
            placeholder={activeRoom ? `Message #${activeRoom.name}…` : activeUsername ? `Message ${activeUsername}…` : 'Message…'}
            bind:value={draft}
            bind:this={composerTextarea}
            disabled={composerDisabled}
            oninput={handleComposerInput}
            onkeydown={handleComposerKeydown}
          ></textarea>
          <button type="button" title="Send (Ctrl+Enter)" disabled={composerDisabled || !draft.trim()} onclick={sendMessage}>Send</button>
        </div>
      </div>
    </div>
  </div>
</section>
