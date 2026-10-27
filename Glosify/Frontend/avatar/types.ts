import type { VoiceMessage } from './connection.js';

export interface TranscriptStatus { conversationId: string; revision?: number; turnCount: number; savedTurns: number; transcriptId?: string | null; suppressed?: boolean; }
export interface AvatarConfig { available: boolean; language: string | null; languageName?: string | null; autoSaveConversations: boolean; quizzes: { id: string; name: string }[]; }
export interface SessionStarted { sessionId: string; memoryId: string; voiceName: string; nativeVoice: boolean; connectUrl: string; }
export interface Usage { total: number; recognition: number; replies: number; speech: number; available: number; pending?: boolean; }
export interface ReplyHelp { translation?: string | null; suggestion?: string | null; suggestionTranslation?: string | null; }
export type AvatarMessage = VoiceMessage & { turnId?: string } & (
    { type: 'transcript-status'; status: TranscriptStatus }
    | { type: 'transcript-save-error' | 'error'; detail: string }
    | { type: 'connected'; resumed?: boolean }
    | { type: 'usage' } & Usage
    | { type: 'connecting'; turnId: string }
    | { type: 'partial' | 'transcript' | 'reply'; text: string } & ReplyHelp
    | { type: 'audio'; pcm: string }
    | { type: 'audio-end' | 'idle' | 'listening' | 'thinking' }
);
