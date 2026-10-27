import type { LearningChange } from '../shared/learning-changes.js';

export interface RunInput {
    message: string;
    contextQuizId?: string | null;
    focusedWordId?: string | null;
    documentContext?: { documentId: string; pageNumber: number } | null;
    transcriptId?: string | null;
    bookDocumentId?: string | null;
    transcriptContext?: { page: number; stream: string | null } | null;
    contextAnkiCollectionId?: string | null;
    avatarConversationId?: string | null;
    expectedLanguageCode?: string;
}
export interface MessagePart { type: string; text?: string | null; title?: string | null; tool?: string | null; state?: string | null; }
export interface PendingChange { kind: string; summary: string; payloadJson: string; }
export interface RunPlanItem { text: string; status: string; }
export interface AssistantRun extends LearningChange {
    threadId: string; status: string; reason?: string | null; revision: number; turnId: string;
    userMessageId: string; messageId?: string | null; savedChanges: number; permanentChanges: number;
    canUndo: boolean; undone: boolean; plan: RunPlanItem[]; parts: MessagePart[];
    question?: { question: string; options: string[]; multiple: boolean } | null;
    approval?: { changes: PendingChange[]; kinds: string[] } | null;
}
export interface UndoResult { undone: number; kept: number; run?: AssistantRun; }
export interface RunCommand { message?: string | null; always?: boolean; answers?: string[]; }
