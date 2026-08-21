import type { ScenarioId } from '../mock/types';

export type ChatTargetKind = 'room' | 'user';

export interface PrototypeChatMessage {
  id: string;
  sender: string;
  text: string;
  mine: boolean;
  time: string;
}

export interface PrototypeChatRoom {
  id: string;
  name: string;
  kind: 'public' | 'private';
  memberCount: number;
  unread: number;
  messages: PrototypeChatMessage[];
}

export interface PrototypeChatConversation {
  id: string;
  username: string;
  presence: 'online' | 'away' | 'offline';
  unread: number;
  messages: PrototypeChatMessage[];
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

function makeRoomMessages(prefix: string, count: number, seed = 0): PrototypeChatMessage[] {
  return Array.from({ length: count }, (_, index) => {
    const sender = roomParticipants[(index + seed) % roomParticipants.length]!;
    const template = roomMessageTemplates[(index * 5 + seed) % roomMessageTemplates.length]!;
    return {
      id: `${prefix}-${index + 1}`,
      sender,
      text: index > 0 && index % 17 === 0
        ? `${template}\n\nLonger follow-up: I also checked the alternate folder and found a couple of extra non-audio files. Nothing critical, but it is a good stress case for a message that wraps across several lines and contains a URL such as https://www.discogs.com/search/?q=Boards+of+Canada&type=all.`
        : template,
      mine: sender === 'fi',
      time: minuteStamp(index, seed % 3),
    };
  });
}

function makeDirectMessages(prefix: string, username: string, count: number, seed = 0): PrototypeChatMessage[] {
  return Array.from({ length: count }, (_, index) => {
    const mine = (index + seed) % 3 === 1;
    const templates = mine ? directMineTemplates : directPeerTemplates;
    let text = templates[(index * 3 + seed) % templates.length]!;
    if (index > 0 && index % 19 === 0) {
      text = `${text}\n\nI am including a deliberately long follow-up so we can see wrapping and scrolling behavior in the prototype. The folder has several nested directories, inconsistent capitalization, artwork, logs, cue sheets, and enough files that the browse result itself is much easier to inspect than to describe in one line.`;
    }
    return {
      id: `${prefix}-${index + 1}`,
      sender: mine ? 'fi' : username,
      text,
      mine,
      time: minuteStamp(index, (seed + 1) % 4),
    };
  });
}

function baseRooms(): PrototypeChatRoom[] {
  return [
    {
      id: 'room-indie',
      name: 'indie',
      kind: 'public',
      memberCount: 418,
      unread: 3,
      messages: [
        { id: 'r1', sender: 'neonrain', text: 'anyone heard the new Broadcast archival release?', mine: false, time: '01:43' },
        { id: 'r2', sender: 'tape_loop', text: 'yeah — details are up at https://www.discogs.com/', mine: false, time: '01:45' },
        { id: 'r3', sender: 'fi', text: 'Nice, adding it to the queue.', mine: true, time: '01:46' },
      ],
    },
    {
      id: 'room-electronic',
      name: 'electronic',
      kind: 'public',
      memberCount: 1267,
      unread: 0,
      messages: [
        { id: 'r4', sender: 'silvermachine', text: 'Boards of Canada discussion moved over here.', mine: false, time: '00:18' },
        { id: 'r5', sender: 'circuitghost', text: 'good call. https://www.last.fm/music/Boards+of+Canada has the listening stats too.', mine: false, time: '00:21' },
      ],
    },
  ];
}

function baseConversations(): PrototypeChatConversation[] {
  return [
    {
      id: 'dm-silvermachine',
      username: 'silvermachine',
      presence: 'online',
      unread: 2,
      messages: [
        { id: 'd1', sender: 'silvermachine', text: 'I think I have the original FLAC rip.', mine: false, time: '01:31' },
        { id: 'd2', sender: 'fi', text: 'Nice — looking for the Geogaddi version specifically.', mine: true, time: '01:32' },
        { id: 'd3', sender: 'silvermachine', text: 'yeah, I have the FLAC rip — catalog info is at https://www.discogs.com/artist/3076-Boards-Of-Canada', mine: false, time: '01:34' },
      ],
    },
    {
      id: 'dm-tape-loop',
      username: 'tape_loop',
      presence: 'online',
      unread: 0,
      messages: [
        { id: 'd4', sender: 'tape_loop', text: 'thanks!', mine: false, time: '00:52' },
      ],
    },
    {
      id: 'dm-neonrain',
      username: 'neonrain',
      presence: 'away',
      unread: 0,
      messages: [
        { id: 'd5', sender: 'neonrain', text: 'try searching the catalog again when the peer comes back online.', mine: false, time: 'Yesterday' },
      ],
    },
  ];
}

const extraRooms = [
  ['room-ambient', 'ambient', 732],
  ['room-jazz', 'jazz', 994],
  ['room-metal', 'metal', 1834],
  ['room-soundtracks', 'soundtracks', 361],
  ['room-vinyl', 'vinyl', 628],
  ['room-lossless', 'lossless', 2156],
  ['room-experimental', 'experimental', 547],
  ['room-downtempo', 'downtempo', 411],
] as const;

const extraUsers = [
  ['dm-circuitghost', 'circuitghost', 'online'],
  ['dm-wavefolder', 'wavefolder', 'away'],
  ['dm-needle_drop', 'needle_drop', 'online'],
  ['dm-bitrot', 'bitrot', 'offline'],
  ['dm-cathedralradio', 'cathedralradio', 'online'],
  ['dm-slowqueue', 'slowqueue', 'away'],
  ['dm-archive_diver', 'archive_diver', 'online'],
  ['dm-late_night', 'late_night', 'online'],
  ['dm-riplog', 'riplog', 'offline'],
] as const satisfies readonly (readonly [string, string, PrototypeChatConversation['presence']])[];

export function createPrototypeChatRooms(scenarioId: ScenarioId = 'normal'): PrototypeChatRoom[] {
  const rooms = baseRooms();
  if (scenarioId !== 'busy' && scenarioId !== 'stress') return rooms;

  const stress = scenarioId === 'stress';
  const baseCount = stress ? 84 : 32;
  const extraCount = stress ? extraRooms.length : 2;

  const expandedBase = rooms.map((room, index) => ({
    ...room,
    unread: index === 0 ? (stress ? 27 : 8) : (stress ? 11 : 2),
    messages: makeRoomMessages(`${scenarioId}-${room.id}`, baseCount - index * (stress ? 7 : 5), index + 1),
  }));

  const expandedExtra = extraRooms.slice(0, extraCount).map(([id, name, memberCount], index) => ({
    id,
    name,
    kind: 'public' as const,
    memberCount,
    unread: (index * 3 + 1) % (stress ? 14 : 6),
    messages: makeRoomMessages(`${scenarioId}-${id}`, stress ? 58 + index * 3 : 22 + index * 2, index + 4),
  }));

  return [...expandedBase, ...expandedExtra];
}

export function createPrototypeChatConversations(scenarioId: ScenarioId = 'normal'): PrototypeChatConversation[] {
  const conversations = baseConversations();
  if (scenarioId !== 'busy' && scenarioId !== 'stress') return conversations;

  const stress = scenarioId === 'stress';
  const baseCount = stress ? 76 : 30;
  const expandedBase = conversations.map((conversation, index) => ({
    ...conversation,
    unread: index === 0 ? (stress ? 18 : 6) : (stress ? index * 3 : index),
    messages: makeDirectMessages(`${scenarioId}-${conversation.id}`, conversation.username, baseCount - index * (stress ? 9 : 6), index + 1),
  }));

  const extraCount = stress ? extraUsers.length : 3;
  const expandedExtra = extraUsers.slice(0, extraCount).map(([id, username, presence], index) => ({
    id,
    username,
    presence,
    unread: stress ? (index * 5) % 13 : index,
    messages: makeDirectMessages(`${scenarioId}-${id}`, username, stress ? 44 + index * 4 : 18 + index * 2, index + 5),
  }));

  return [...expandedBase, ...expandedExtra];
}

export function chatPreview(messages: readonly PrototypeChatMessage[]): string {
  const text = messages.at(-1)?.text ?? 'No messages yet';
  return text.replace(/\s+/g, ' ').trim();
}

export function chatInitials(username: string): string {
  const pieces = username.split(/[^a-z0-9]+/i).filter(Boolean);
  if (!pieces.length) return '?';
  return pieces.slice(0, 2).map((piece) => piece[0]!.toLowerCase()).join('');
}
