<script lang="ts">
  import Icon from '../components/Icon.svelte';
  import LinkifiedText from '../components/LinkifiedText.svelte';
  import UsernameLink from '../components/UsernameLink.svelte';
  import LoadMoreButton from '../components/LoadMoreButton.svelte';
  import MutationStatus from '../components/MutationStatus.svelte';
  import type { PrototypeScenario } from '../mock/types';
  import type { PrototypeMutationState } from '../prototype/backend-contracts';
  import { prototypeUuid } from '../prototype/ids';
  import {
    chatInitials,
    chatPreview,
    chatRuntimeForScenario,
    createJoiningRoom,
    createLocalOutgoingMessage,
    createPrototypeChatConversations,
    createPrototypeChatRooms,
    materializeConversation,
    updateOutgoingMessageState,
    type PrototypeChatConversation,
    type PrototypeChatDraftTarget,
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

  let rooms = $state<PrototypeChatRoom[]>(createPrototypeChatRooms('normal'));
  let conversations = $state<PrototypeChatConversation[]>(createPrototypeChatConversations('normal'));
  let target = $state<PrototypeChatTarget>({ kind: 'user', id: prototypeUuid(0x83000000, 1) });
  let draftTarget = $state<PrototypeChatDraftTarget | null>(null);
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
  let mutation = $state<PrototypeMutationState>({ phase: 'idle' });
  let localSequence = 1;

  let runtime = $derived(chatRuntimeForScenario(scenario.id));
  let activeRoom = $derived(target.kind === 'room' ? rooms.find((room) => room.id === target.id) ?? null : null);
  let activeConversation = $derived(target.kind === 'user' ? conversations.find((conversation) => conversation.id === target.id) ?? null : null);
  let activeDraft = $derived(target.kind === 'draft' && draftTarget?.id === target.id ? draftTarget : null);
  let activeMessages = $derived(activeRoom?.messages ?? activeConversation?.messages ?? []);
  let activeUsername = $derived(activeConversation?.username ?? activeDraft?.username ?? null);
  let activeUserBlocked = $derived(activeUsername ? blockedUsers.has(activeUsername) : false);
  let composerDisabled = $derived(runtime.state === 'Disabled' || activeUserBlocked || (!activeRoom && !activeConversation && !activeDraft));

  $effect(() => {
    const nextScenarioId = scenario.id;
    if (nextScenarioId === loadedScenarioId) return;
    loadedScenarioId = nextScenarioId;
    rooms = createPrototypeChatRooms(nextScenarioId);
    conversations = createPrototypeChatConversations(nextScenarioId);
    const firstConversation = conversations[0];
    const firstRoom = rooms[0];
    target = firstConversation ? { kind: 'user', id: firstConversation.id } : firstRoom ? { kind: 'room', id: firstRoom.id } : { kind: 'draft', id: '' };
    draftTarget = null;
    draft = '';
    adding = null;
    addDraft = '';
    menuOpen = false;
    blockedUsers = new Set();
    mutation = { phase: 'idle' };
    scheduleComposerResize();
    scheduleScrollToBottom();
  });

  $effect(() => {
    const username = initialUsername?.trim();
    if (!username || username === initialUsernameHandled) return;
    initialUsernameHandled = username;
    openOrCreateConversation(username);
  });

  function scheduleComposerResize(): void { requestAnimationFrame(() => resizeComposer(composerTextarea)); }
  function scheduleScrollToBottom(): void { requestAnimationFrame(() => { if (messagesElement) messagesElement.scrollTop = messagesElement.scrollHeight; }); }

  function selectTarget(next: PrototypeChatTarget): void {
    target = next;
    menuOpen = false;
    mutation = { phase: 'idle' };
    draft = '';
    scheduleComposerResize();
    scheduleScrollToBottom();
    if (next.kind === 'room') rooms = rooms.map((room) => room.id === next.id ? { ...room, unread: 0 } : room);
    else if (next.kind === 'user') conversations = conversations.map((conversation) => conversation.id === next.id ? { ...conversation, unread: 0 } : conversation);
  }

  function openOrCreateConversation(username: string): void {
    const normalized = username.trim();
    if (!normalized) return;
    const existing = conversations.find((conversation) => conversation.username.toLowerCase() === normalized.toLowerCase());
    if (existing) {
      draftTarget = null;
      selectTarget({ kind: 'user', id: existing.id });
      return;
    }
    const id = prototypeUuid(0x83ff0000, localSequence++);
    draftTarget = { id, username: normalized, lifetime: 'frontend-draft' };
    selectTarget({ kind: 'draft', id });
  }

  function joinRoom(name: string): void {
    const normalized = name.trim();
    if (!normalized) return;
    const existing = rooms.find((room) => room.name.toLowerCase() === normalized.toLowerCase());
    if (existing) { selectTarget({ kind: 'room', id: existing.id }); return; }
    const next = createJoiningRoom(normalized, localSequence++);
    rooms = [next, ...rooms];
    selectTarget({ kind: 'room', id: next.id });
    mutation = { phase: 'pending', label: 'Joining room…' };
    setTimeout(() => {
      rooms = rooms.map((room) => room.id === next.id ? { ...room, phase: 'Joined', rosterComplete: true, lifetime: 'live', dto: { ...room.dto, phase: 'Joined', rosterComplete: true } } : room);
      mutation = { phase: 'succeeded', label: 'Room joined' };
    }, 450);
  }

  function submitAdd(): void {
    if (adding === 'room') joinRoom(addDraft);
    else if (adding === 'user') openOrCreateConversation(addDraft);
    addDraft = ''; adding = null;
  }
  function beginAdd(kind: 'room' | 'user'): void { adding = adding === kind ? null : kind; addDraft = ''; }

  function settleMessage(targetKind: 'room' | 'user', targetId: string, messageId: string): void {
    const shouldFail = scenario.id === 'stress' && localSequence % 4 === 0;
    const state = shouldFail ? 'Failed' : 'Sent';
    const reason = shouldFail ? 'Chat persistence unavailable' : null;
    if (targetKind === 'room') {
      rooms = rooms.map((room) => room.id === targetId ? { ...room, messages: room.messages.map((m) => m.id === messageId ? updateOutgoingMessageState(m, state, reason) : m) } : room);
    } else {
      conversations = conversations.map((conversation) => conversation.id === targetId ? { ...conversation, messages: conversation.messages.map((m) => m.id === messageId ? updateOutgoingMessageState(m, state, reason) : m) } : conversation);
    }
    mutation = shouldFail ? { phase: 'failed', label: 'Message failed', detail: reason ?? undefined } : { phase: 'succeeded', label: 'Message sent' };
  }

  function sendMessage(): void {
    const text = draft.trim();
    if (!text || composerDisabled) return;
    mutation = { phase: 'pending', label: 'Sending…' };

    if (activeRoom) {
      const message = createLocalOutgoingMessage('Room', activeRoom.id, text, localSequence++);
      const roomId = activeRoom.id;
      rooms = rooms.map((room) => room.id === roomId ? { ...room, messages: [...room.messages, message] } : room);
      setTimeout(() => settleMessage('room', roomId, message.id), 400);
    } else if (activeConversation) {
      const message = createLocalOutgoingMessage('Direct', activeConversation.id, text, localSequence++);
      const conversationId = activeConversation.id;
      conversations = conversations.map((conversation) => conversation.id === conversationId ? { ...conversation, messages: [...conversation.messages, message] } : conversation);
      setTimeout(() => settleMessage('user', conversationId, message.id), 400);
    } else if (activeDraft) {
      const conversationId = prototypeUuid(0x8300f000, localSequence++);
      const message = createLocalOutgoingMessage('Direct', conversationId, text, localSequence++);
      const next = materializeConversation(activeDraft.username, message);
      conversations = [next, ...conversations];
      draftTarget = null;
      target = { kind: 'user', id: conversationId };
      setTimeout(() => settleMessage('user', conversationId, message.id), 400);
    }

    draft = '';
    scheduleComposerResize();
    scheduleScrollToBottom();
  }

  function resizeComposer(node: HTMLTextAreaElement | null): void {
    if (!node) return;
    node.style.height = 'auto';
    const minimum = 38, maximum = 156;
    node.style.height = `${Math.max(minimum, Math.min(node.scrollHeight, maximum))}px`;
    node.style.overflowY = node.scrollHeight > maximum ? 'auto' : 'hidden';
  }
  function handleComposerInput(event: Event): void { resizeComposer(event.currentTarget as HTMLTextAreaElement); }
  function handleComposerKeydown(event: KeyboardEvent): void { if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) { event.preventDefault(); sendMessage(); } }
  function handleAddKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter') { event.preventDefault(); submitAdd(); }
    else if (event.key === 'Escape') { adding = null; addDraft = ''; }
  }
  function handleWindowKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Escape') return;
    if (menuOpen) menuOpen = false;
    if (adding) { adding = null; addDraft = ''; }
  }
  function handleWindowPointerdown(event: PointerEvent): void {
    if (!menuOpen || !chatOptionsElement) return;
    if (event.target instanceof Node && !chatOptionsElement.contains(event.target)) menuOpen = false;
  }

  function blockActiveUser(): void {
    if (!activeUsername) return;
    mutation = { phase: 'pending', label: 'Updating peer access…' };
    const next = new Set(blockedUsers);
    if (next.has(activeUsername)) next.delete(activeUsername); else next.add(activeUsername);
    blockedUsers = next;
    menuOpen = false;
    mutation = { phase: 'succeeded', label: blockedUsers.has(activeUsername) ? 'User blocked' : 'User unblocked' };
  }

  function deleteActiveConversation(): void {
    if (!activeConversation) return;
    mutation = { phase: 'pending', label: 'Deleting chat…' };
    const id = activeConversation.id;
    const index = conversations.findIndex((conversation) => conversation.id === id);
    conversations = conversations.filter((conversation) => conversation.id !== id);
    menuOpen = false;

    const fallbackConversation = conversations[Math.min(Math.max(index, 0), Math.max(conversations.length - 1, 0))] ?? conversations[0];
    const fallbackRoom = rooms[0];
    if (fallbackConversation) selectTarget({ kind: 'user', id: fallbackConversation.id });
    else if (fallbackRoom) selectTarget({ kind: 'room', id: fallbackRoom.id });
    else {
      target = { kind: 'draft', id: '' };
      draftTarget = null;
      draft = '';
    }

    mutation = { phase: 'succeeded', label: 'Chat deleted' };
  }

  function leaveActiveRoom(): void {
    if (!activeRoom) return;
    const roomId = activeRoom.id;
    mutation = { phase: 'pending', label: 'Leaving room…' };
    rooms = rooms.map((room) => room.id === roomId ? { ...room, phase: 'Leaving', dto: { ...room.dto, phase: 'Leaving' } } : room);
    menuOpen = false;
    setTimeout(() => {
      rooms = rooms.filter((room) => room.id !== roomId);
      const fallbackRoom = rooms[0], fallbackUser = conversations[0];
      if (fallbackRoom) selectTarget({ kind: 'room', id: fallbackRoom.id });
      else if (fallbackUser) selectTarget({ kind: 'user', id: fallbackUser.id });
      mutation = { phase: 'succeeded', label: 'Room left' };
    }, 350);
  }

  function loadEarlierMessages(): void {
    const historical = Array.from({ length: 8 }, (_, index) => createLocalOutgoingMessage(activeRoom ? 'Room' : 'Direct', target.id, `Earlier message ${index + 1}`, 700_000 + index)).map((m) => updateOutgoingMessageState(m, 'Sent'));
    if (activeRoom) rooms = rooms.map((room) => room.id === activeRoom!.id ? { ...room, hasEarlierMessages: false, messages: [...historical, ...room.messages] } : room);
    else if (activeConversation) conversations = conversations.map((conversation) => conversation.id === activeConversation!.id ? { ...conversation, hasEarlierMessages: false, messages: [...historical, ...conversation.messages] } : conversation);
  }

  function presenceAgeLabel(): string {
    if (!activeConversation) return '';
    return activeConversation.presence === 'unknown' ? 'presence unknown' : `${activeConversation.presence} · observed 2 min ago`;
  }
</script>

<svelte:window onkeydown={handleWindowKeydown} onpointerdown={handleWindowPointerdown} />

<section class="page chat-page">
  <div class="chat-layout">
    <aside class="chat-sidebar" aria-label="Chat destinations">
      <section class="chat-sidebar-section">
        <header class="chat-section-heading"><span>Rooms</span><button type="button" class="chat-add-button" aria-label="Join room" title="Join room" onclick={() => beginAdd('room')}>+</button></header>
        {#if adding === 'room'}<div class="chat-add-row"><input aria-label="Room name" placeholder="Room name…" bind:value={addDraft} onkeydown={handleAddKeydown} /><button type="button" disabled={!addDraft.trim()} onclick={submitAdd}>Join</button></div>{/if}
        <div class="chat-destination-list">
          {#each rooms as room (room.id)}
            <button type="button" class:active={target.kind === 'room' && target.id === room.id} class="chat-destination room" aria-current={target.kind === 'room' && target.id === room.id ? 'page' : undefined} onclick={() => selectTarget({ kind: 'room', id: room.id })}>
              <span class="room-mark">#</span><span class="chat-destination-copy"><strong>{room.name}</strong><small>{room.phase !== 'Joined' ? room.phase : chatPreview(room.messages)}</small></span>{#if room.unread > 0}<span class="unread-count">{room.unread}</span>{/if}
            </button>
          {/each}
        </div>
      </section>

      <section class="chat-sidebar-section users-section">
        <header class="chat-section-heading"><span>Users</span><button type="button" class="chat-add-button" aria-label="Message user" title="Message user" onclick={() => beginAdd('user')}>+</button></header>
        {#if adding === 'user'}<div class="chat-add-row"><input aria-label="Username" placeholder="Username…" bind:value={addDraft} onkeydown={handleAddKeydown} /><button type="button" disabled={!addDraft.trim()} onclick={submitAdd}>Open</button></div>{/if}
        <div class="chat-destination-list">
          {#if activeDraft}
            <div class:active={target.kind === 'draft'} class="chat-destination user draft-target">
              <button type="button" class="chat-destination-open" aria-label={`Open draft conversation with ${activeDraft.username}`} onclick={() => selectTarget({ kind: 'draft', id: activeDraft.id })}></button>
              <span class="avatar">{chatInitials(activeDraft.username)}</span><span class="chat-destination-copy"><strong>{activeDraft.username}</strong><small>New chat</small></span>
            </div>
          {/if}
          {#each conversations as conversation (conversation.id)}
            <div class:active={target.kind === 'user' && target.id === conversation.id} class="chat-destination user">
              <button type="button" class="chat-destination-open" aria-label={`Open conversation with ${conversation.username}`} aria-current={target.kind === 'user' && target.id === conversation.id ? 'page' : undefined} onclick={() => selectTarget({ kind: 'user', id: conversation.id })}></button>
              <span class="avatar">{chatInitials(conversation.username)}</span><span class="chat-destination-copy"><strong>{conversation.username}</strong><small>{chatPreview(conversation.messages)}</small></span>{#if conversation.unread > 0}<span class="unread-count">{conversation.unread}</span>{/if}
            </div>
          {/each}
        </div>
      </section>
    </aside>

    <div class="chat-thread">
      <header class="chat-thread-heading">
        {#if activeConversation || activeDraft}
          <div class="chat-thread-identity"><UsernameLink username={activeUsername ?? ''} {onopenuser} /><small class:blocked={activeUserBlocked}>{activeUserBlocked ? 'blocked' : activeConversation ? presenceAgeLabel() : 'new chat'}</small></div>
        {:else if activeRoom}
          <div class="chat-thread-identity"><strong>#{activeRoom.name}</strong><small>{activeRoom.kind === 'private' ? 'Private room' : 'Public room'} · {activeRoom.memberCount.toLocaleString()} users · {activeRoom.rosterComplete ? 'roster complete' : 'roster partial'}{activeRoom.phase !== 'Joined' ? ` · ${activeRoom.phase}` : ''}</small></div>
        {/if}
        <div class="chat-thread-options" bind:this={chatOptionsElement}>
          <button type="button" class="chat-options-button" aria-label="Chat options" aria-expanded={menuOpen} onclick={() => (menuOpen = !menuOpen)}><Icon name="more" /></button>
          {#if menuOpen}<div class="chat-options-menu">{#if activeConversation || activeDraft}<button type="button" onclick={blockActiveUser}>{activeUserBlocked ? 'Unblock user' : 'Block user'}</button>{#if activeConversation}<button type="button" class="danger" onclick={deleteActiveConversation}>Delete chat</button>{/if}{:else if activeRoom}<button type="button" class="danger" onclick={leaveActiveRoom}>Leave room</button>{/if}</div>{/if}
        </div>
      </header>

      <div class="chat-resource-state"><MutationStatus state={mutation} /></div>

      <div class="chat-messages" aria-live="polite" bind:this={messagesElement}>
        {#if (activeRoom?.hasEarlierMessages || activeConversation?.hasEarlierMessages)}<LoadMoreButton label="Load earlier messages" onclick={loadEarlierMessages} />{/if}
        {#if activeMessages.length === 0}
          <div class="chat-empty-thread"><strong>{activeDraft ? 'New chat' : 'No messages yet'}</strong></div>
        {:else if activeRoom}
          {#each activeMessages as message (message.id)}
            <div class:mine={message.mine} class:send-failed={message.state === 'Failed'} class="room-message"><div class="room-message-meta">{#if message.mine}<span class="room-message-self">fi</span>{:else}<UsernameLink username={message.sender} {onopenuser} />{/if}<time>{message.time}</time>{#if message.mine}<span class={`message-send-state ${message.state.toLowerCase()}`}>{message.state}{message.failureReason ? ` · ${message.failureReason}` : ''}</span>{/if}</div><div class="message-body"><LinkifiedText text={message.text} /></div></div>
          {/each}
        {:else if activeConversation}
          {#each activeMessages as message (message.id)}
            <div class:mine={message.mine} class:send-failed={message.state === 'Failed'} class="direct-message-row"><div class="direct-message-bubble"><div class="message-body"><LinkifiedText text={message.text} /></div><div class="direct-message-meta"><time>{message.time}</time>{#if message.mine}<span class={`message-send-state ${message.state.toLowerCase()}`}>{message.state}{message.failureReason ? ` · ${message.failureReason}` : ''}</span>{/if}</div></div></div>
          {/each}
        {/if}
      </div>

      <div class="chat-composer">
        {#if activeUserBlocked}<span class="composer-state">Unblock {activeUsername} to send messages.</span>{:else if runtime.state !== 'Ready'}<span class="composer-state">Chat {runtime.state.toLowerCase()}{runtime.reason ? ` · ${runtime.reason}` : ''}</span>{/if}
        <div class="chat-composer-row"><textarea aria-label="Message" rows="1" placeholder={activeRoom ? `Message #${activeRoom.name}…` : activeUsername ? `Message ${activeUsername}…` : 'Message…'} bind:value={draft} bind:this={composerTextarea} disabled={composerDisabled} oninput={handleComposerInput} onkeydown={handleComposerKeydown}></textarea><button type="button" title="Send (Ctrl+Enter)" disabled={composerDisabled || !draft.trim()} onclick={sendMessage}>Send</button></div>
      </div>
    </div>
  </div>
</section>
