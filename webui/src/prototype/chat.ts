import type { components } from '../api/generated';
import type { ScenarioId } from '../mock/types';
import type { PrototypeDataLifetime } from './state';
import { prototypeUuid } from './ids';

export type ChatMessageDto = components['schemas']['ChatMessageDto'];
export type ConversationSummaryDto = components['schemas']['ConversationSummaryDto'];
export type ChatRoomSummaryDto = components['schemas']['ChatRoomSummaryDto'];
export type ChatRuntimeStateDto = components['schemas']['ChatRuntimeStateDto'];
export type ChatMessageState = components['schemas']['ChatMessageState'];
export type ChatRoomJoinPhase = components['schemas']['ChatRoomJoinPhase'];
export type ChatTargetKind = 'room' | 'user' | 'draft';

export interface PrototypeChatMessage {
  id: string;
  sender: string;
  text: string;
  mine: boolean;
  time: string;
  state: ChatMessageState;
  failureReason: string | null;
  dto: ChatMessageDto;
}

export interface PrototypeChatRoom {
  id: string;
  name: string;
  kind: 'public' | 'private';
  phase: ChatRoomJoinPhase;
  rosterComplete: boolean;
  failureReason: string | null;
  memberCount: number;
  unread: number;
  hasEarlierMessages: boolean;
  messages: PrototypeChatMessage[];
  lifetime: PrototypeDataLifetime;
  dto: ChatRoomSummaryDto;
}

export interface PrototypeChatConversation {
  id: string;
  username: string;
  presence: 'online' | 'away' | 'offline' | 'unknown';
  presenceObservedAt: string;
  unread: number;
  hasEarlierMessages: boolean;
  messages: PrototypeChatMessage[];
  lifetime: PrototypeDataLifetime;
  dto: ConversationSummaryDto;
}

export interface PrototypeChatDraftTarget {
  id: string;
  username: string;
  lifetime: 'frontend-draft';
}

export interface PrototypeChatTarget {
  kind: ChatTargetKind;
  id: string;
}

const roomParticipants = ['neonrain', 'tape_loop', 'silvermachine', 'circuitghost', 'wavefolder', 'fi'];
const roomMessageTemplates = [
  'anyone heard this pressing? I am trying to work out whether the two listings are actually the same master.',
  'I compared the checksums on my copy and they differ, but the audio length is identical.',
  'Discogs has a useful note here: https://www.discogs.com/ — the release comments are more useful than the tracklist.',
  'I can share the lossless version for a bit. Queue is not too bad right now.',
  'That search returned three folders for me. One is clearly a transcode, one looks complete, and the last has no artwork.',
  'Small correction to my previous message:\ntrack 7 is 5:14, not 5:41. I was looking at the remaster.',
  'Does anybody have the booklet scans as well? Not essential, just trying to keep the folder complete.',
  'The spectral view looks fine here. I would still prefer the original CD rip if somebody has it.',
  'Useful catalog cross-reference: https://www.last.fm/music/Boards+of+Canada — not authoritative, but handy for names.',
  'I queued it. If my slot opens before yours does, I can relay the files.',
  'The folder layout on that share is a little strange: Disc 1 / audio, Disc 1 / scans, Disc 2 / audio, then cue sheets at the root.',
  'For anyone following along, the important part is that the filenames are inconsistent but the tags are clean.\nI would search by album + track count rather than exact filenames.',
];

const directPeerTemplates = [
  'I found the folder. Give me a minute to check whether it is the original rip or the later remaster.',
  'Yep, it is lossless. The cue sheet and log are there too.',
  'My queue is moving, but slowly. You should get a slot without having to retry manually.',
  'The catalog entry I was comparing against is https://www.discogs.com/artist/3076-Boards-Of-Canada',
  'One odd thing: the filename says 2004, but the tags and booklet scans point to the earlier issue.',
  'I have two copies:\n- one FLAC folder with scans\n- one older MP3 folder\nI will leave both shared so you can pick the right one.',
  'If the transfer stalls, message me again. I am reorganizing a share and might briefly disappear.',
  'I checked track lengths against the cue sheet and they line up.',
];

const directMineTemplates = [
  'Perfect, thanks. I am mainly trying to avoid the remaster.',
  'No rush — I would rather wait for the clean copy.',
  'That sounds right. I will queue the FLAC folder.',
  'Thanks for checking the cue sheet too.',
  'The path is useful; I can filter the browse tree instead of searching again.',
  'If you move the folder, no problem. I can retry from the new result.',
];

function minuteStamp(index: number, hour = 1): string {
  const minute = (7 + index * 3) % 60;
  return `${String((hour + Math.floor((7 + index * 3) / 60)) % 24).padStart(2, '0')}:${String(minute).padStart(2, '0')}`;
}

function message(
  sequence: number,
  targetKind: 'Direct' | 'Room',
  targetId: string,
  sender: string,
  text: string,
  mine: boolean,
  time: string,
  state: ChatMessageState = mine ? 'Sent' : 'Received',
  failureReason: string | null = null,
): PrototypeChatMessage {
  const id = prototypeUuid(0x81000000, sequence);
  const dto: ChatMessageDto = {
    messageId: id,
    sequence,
    targetKind,
    targetId,
    sender,
    direction: mine ? 'Outgoing' : 'Incoming',
    text,
    occurredAtUtc: `2026-08-07T${time.includes(':') ? time : '00:00'}:00.000Z`,
    recordedAtUtc: '2026-08-07T08:15:00.000Z',
    state,
    failureReason,
  };
  return { id, sender, text, mine, time, state, failureReason, dto };
}

function makeRoomMessages(targetId: string, count: number, seed = 0): PrototypeChatMessage[] {
  return Array.from({ length: count }, (_, index) => {
    const sender = roomParticipants[(index + seed) % roomParticipants.length]!;
    const template = roomMessageTemplates[(index * 5 + seed) % roomMessageTemplates.length]!;
    const text = index > 0 && index % 17 === 0
      ? `${template}\n\nLonger follow-up: I also checked the alternate folder and found a couple of extra non-audio files. Nothing critical, but it is a good stress case for a message that wraps across several lines and contains a URL such as https://www.discogs.com/search/?q=Boards+of+Canada&type=all.`
      : template;
    const mine = sender === 'fi';
    const state: ChatMessageState = mine && index % 29 === 0 ? 'Unknown' : mine ? 'Sent' : 'Received';
    return message(10_000 + seed * 1_000 + index, 'Room', targetId, sender, text, mine, minuteStamp(index, seed % 3), state);
  });
}

function makeDirectMessages(targetId: string, username: string, count: number, seed = 0): PrototypeChatMessage[] {
  return Array.from({ length: count }, (_, index) => {
    const mine = (index + seed) % 3 === 1;
    const templates = mine ? directMineTemplates : directPeerTemplates;
    let text = templates[(index * 3 + seed) % templates.length]!;
    if (index > 0 && index % 19 === 0) {
      text = `${text}\n\nI am including a deliberately long follow-up so we can see wrapping and scrolling behavior in the prototype. The folder has several nested directories, inconsistent capitalization, artwork, logs, cue sheets, and enough files that the browse result itself is much easier to inspect than to describe in one line.`;
    }
    let state: ChatMessageState = mine ? 'Sent' : 'Received';
    let failureReason: string | null = null;
    if (mine && index % 31 === 0) state = 'Pending';
    if (mine && index > 0 && index % 37 === 0) { state = 'Failed'; failureReason = 'Chat persistence unavailable'; }
    return message(20_000 + seed * 1_000 + index, 'Direct', targetId, mine ? 'fi' : username, text, mine, minuteStamp(index, (seed + 1) % 4), state, failureReason);
  });
}

function roomSummary(id: string, name: string, kind: 'public' | 'private', phase: ChatRoomJoinPhase, memberCount: number, unread: number, rosterComplete: boolean, messages: PrototypeChatMessage[], failureReason: string | null = null): ChatRoomSummaryDto {
  return {
    roomId: id, name, configured: true, remembered: true, desired: phase !== 'Leaving', kind: kind === 'private' ? 'Private' : 'Public', owned: false, moderated: false,
    phase, failureReason, memberCount, memberRevision: 1, rosterComplete, unreadCount: unread, lastReadSequence: 0, revision: 1, lastMessage: messages.at(-1)?.dto ?? null,
  };
}

function conversationSummary(id: string, username: string, unread: number, messages: PrototypeChatMessage[]): ConversationSummaryDto {
  return { conversationId: id, username, privateMessagesBlocked: false, archived: false, unreadCount: unread, lastReadSequence: 0, revision: 1, lastMessage: messages.at(-1)?.dto ?? null };
}

function baseRooms(): PrototypeChatRoom[] {
  const indieId = prototypeUuid(0x82000000, 1);
  const electronicId = prototypeUuid(0x82000000, 2);
  const indieMessages = [
    message(1, 'Room', indieId, 'neonrain', 'anyone heard the new Broadcast archival release?', false, '01:43'),
    message(2, 'Room', indieId, 'tape_loop', 'yeah — details are up at https://www.discogs.com/', false, '01:45'),
    message(3, 'Room', indieId, 'fi', 'Nice, adding it to the queue.', true, '01:46'),
  ];
  const electronicMessages = [
    message(4, 'Room', electronicId, 'silvermachine', 'Boards of Canada discussion moved over here.', false, '00:18'),
    message(5, 'Room', electronicId, 'circuitghost', 'good call. https://www.last.fm/music/Boards+of+Canada has the listening stats too.', false, '00:21'),
  ];
  return [
    { id: indieId, name: 'indie', kind: 'public', phase: 'Joined', rosterComplete: true, failureReason: null, memberCount: 418, unread: 3, hasEarlierMessages: true, messages: indieMessages, lifetime: 'retained', dto: roomSummary(indieId, 'indie', 'public', 'Joined', 418, 3, true, indieMessages) },
    { id: electronicId, name: 'electronic', kind: 'public', phase: 'Joined', rosterComplete: true, failureReason: null, memberCount: 1267, unread: 0, hasEarlierMessages: false, messages: electronicMessages, lifetime: 'retained', dto: roomSummary(electronicId, 'electronic', 'public', 'Joined', 1267, 0, true, electronicMessages) },
  ];
}

function baseConversations(): PrototypeChatConversation[] {
  const defs = [
    [1, 'silvermachine', 'online', 2],
    [2, 'tape_loop', 'online', 0],
    [3, 'neonrain', 'away', 0],
  ] as const;
  return defs.map(([n, username, presence, unread]) => {
    const id = prototypeUuid(0x83000000, n);
    const messages = n === 1
      ? [
          message(100 + n, 'Direct', id, username, 'I think I have the original FLAC rip.', false, '01:31'),
          message(110 + n, 'Direct', id, 'fi', 'Nice — looking for the Geogaddi version specifically.', true, '01:32'),
          message(120 + n, 'Direct', id, username, 'yeah, I have the FLAC rip — catalog info is at https://www.discogs.com/artist/3076-Boards-Of-Canada', false, '01:34'),
        ]
      : n === 2
        ? [message(130 + n, 'Direct', id, username, 'thanks!', false, '00:52')]
        : [message(140 + n, 'Direct', id, username, 'try searching the catalog again when the peer comes back online.', false, '00:12')];
    return { id, username, presence, presenceObservedAt: '2026-08-07T08:13:00.000Z', unread, hasEarlierMessages: n === 1, messages, lifetime: 'retained' as const, dto: conversationSummary(id, username, unread, messages) };
  });
}

const extraRooms = ['ambient', 'jazz', 'metal', 'soundtracks', 'vinyl', 'lossless', 'experimental', 'downtempo'] as const;
const extraUsers = ['circuitghost', 'wavefolder', 'needle_drop', 'bitrot', 'cathedralradio', 'slowqueue', 'archive_diver', 'late_night', 'riplog'] as const;

export function createPrototypeChatRooms(scenarioId: ScenarioId = 'normal'): PrototypeChatRoom[] {
  const rooms = baseRooms();
  if (scenarioId !== 'busy' && scenarioId !== 'stress') return rooms;
  const stress = scenarioId === 'stress';
  const baseCount = stress ? 84 : 32;
  const expandedBase = rooms.map((room, index) => {
    const messages = makeRoomMessages(room.id, baseCount - index * (stress ? 7 : 5), index + 1);
    const phase: ChatRoomJoinPhase = stress && index === 1 ? 'Failed' : 'Joined';
    const rosterComplete = !(stress && index === 0);
    return { ...room, phase, rosterComplete, failureReason: phase === 'Failed' ? 'Join timed out' : null, unread: index === 0 ? (stress ? 27 : 8) : (stress ? 11 : 2), hasEarlierMessages: true, messages, dto: roomSummary(room.id, room.name, room.kind, phase, room.memberCount, room.unread, rosterComplete, messages, phase === 'Failed' ? 'Join timed out' : null) };
  });
  const extraCount = stress ? extraRooms.length : 2;
  const extras = extraRooms.slice(0, extraCount).map((name, index) => {
    const id = prototypeUuid(0x82000000, 10 + index);
    const messages = makeRoomMessages(id, stress ? 58 + index * 3 : 22 + index * 2, index + 4);
    const phase: ChatRoomJoinPhase = index === 0 && stress ? 'Joining' : 'Joined';
    const memberCount = 300 + index * 211;
    return { id, name, kind: 'public' as const, phase, rosterComplete: !stress || index % 3 !== 0, failureReason: null, memberCount, unread: (index * 3 + 1) % (stress ? 14 : 6), hasEarlierMessages: true, messages, lifetime: 'retained' as const, dto: roomSummary(id, name, 'public', phase, memberCount, 0, !stress || index % 3 !== 0, messages) };
  });
  return [...expandedBase, ...extras];
}

export function createPrototypeChatConversations(scenarioId: ScenarioId = 'normal'): PrototypeChatConversation[] {
  const conversations = baseConversations();
  if (scenarioId !== 'busy' && scenarioId !== 'stress') return conversations;
  const stress = scenarioId === 'stress';
  const baseCount = stress ? 76 : 30;
  const expandedBase = conversations.map((conversation, index) => {
    const messages = makeDirectMessages(conversation.id, conversation.username, baseCount - index * (stress ? 9 : 6), index + 1);
    const unread = index === 0 ? (stress ? 18 : 6) : (stress ? index * 3 : index);
    return { ...conversation, unread, hasEarlierMessages: true, messages, dto: conversationSummary(conversation.id, conversation.username, unread, messages) };
  });
  const extraCount = stress ? extraUsers.length : 3;
  const extras = extraUsers.slice(0, extraCount).map((username, index) => {
    const id = prototypeUuid(0x83000000, 10 + index);
    const presence = username === 'bitrot' || username === 'riplog' ? 'offline' as const : index % 3 === 1 ? 'away' as const : 'online' as const;
    const messages = makeDirectMessages(id, username, stress ? 44 + index * 4 : 20 + index * 2, index + 5);
    const unread = (index * 2 + 1) % (stress ? 11 : 5);
    return { id, username, presence, presenceObservedAt: '2026-08-07T08:09:00.000Z', unread, hasEarlierMessages: true, messages, lifetime: 'retained' as const, dto: conversationSummary(id, username, unread, messages) };
  });
  return [...expandedBase, ...extras];
}

export function chatRuntimeForScenario(scenarioId: ScenarioId): ChatRuntimeStateDto {
  if (scenarioId === 'offline') return { state: 'Disabled', reason: 'Daemon unavailable', desiredRoomCount: 0, joinedRoomCount: 0, unreadPrivateMessageCount: 0, unreadRoomMessageCount: 0, revision: 1 };
  if (scenarioId === 'stress') return { state: 'Degraded', reason: 'Persistence is degraded; sends may fail', desiredRoomCount: 10, joinedRoomCount: 8, unreadPrivateMessageCount: 18, unreadRoomMessageCount: 38, revision: 5 };
  return { state: 'Ready', reason: null, desiredRoomCount: scenarioId === 'busy' ? 4 : 2, joinedRoomCount: scenarioId === 'busy' ? 4 : 2, unreadPrivateMessageCount: scenarioId === 'busy' ? 9 : 2, unreadRoomMessageCount: scenarioId === 'busy' ? 10 : 3, revision: 3 };
}

export function createLocalOutgoingMessage(targetKind: 'Direct' | 'Room', targetId: string, text: string, sequence = Date.now() % 900_000): PrototypeChatMessage {
  return message(90_000 + sequence, targetKind, targetId, 'fi', text, true, 'now', 'Pending');
}

export function materializeConversation(username: string, firstMessage: PrototypeChatMessage): PrototypeChatConversation {
  const id = firstMessage.dto.targetId;
  const messages = [{ ...firstMessage, dto: { ...firstMessage.dto, targetId: id } }];
  return { id, username, presence: 'unknown', presenceObservedAt: new Date().toISOString(), unread: 0, hasEarlierMessages: false, messages, lifetime: 'retained', dto: conversationSummary(id, username, 0, messages) };
}


export function createJoiningRoom(name: string, sequence = Date.now() % 900_000): PrototypeChatRoom {
  const id = prototypeUuid(0x8200f000, sequence);
  const messages: PrototypeChatMessage[] = [];
  const phase: ChatRoomJoinPhase = 'Joining';
  return {
    id, name, kind: 'public', phase, rosterComplete: false, failureReason: null, memberCount: 0, unread: 0,
    hasEarlierMessages: false, messages, lifetime: 'live-only',
    dto: roomSummary(id, name, 'public', phase, 0, 0, false, messages),
  };
}

export function updateOutgoingMessageState(message: PrototypeChatMessage, state: ChatMessageState, failureReason: string | null = null): PrototypeChatMessage {
  return { ...message, state, failureReason, dto: { ...message.dto, state, failureReason } };
}

export function chatPreview(messages: readonly PrototypeChatMessage[]): string {
  return messages.at(-1)?.text.replace(/\s+/g, ' ') ?? 'No messages yet';
}

export function chatInitials(username: string): string {
  const pieces = username.split(/[^a-z0-9]+/i).filter(Boolean);
  return pieces.length > 1 ? `${pieces[0]![0]}${pieces[1]![0]}`.toLowerCase() : username.slice(0, 2).toLowerCase();
}
