import type { ApiMessage, Conversation, Source, User } from './types';

async function json<T>(url: string, opts?: RequestInit): Promise<T> {
  const res = await fetch(url, opts);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json() as Promise<T>;
}

export const api = {
  me: () => json<User>('/api/me'),
  conversations: () => json<Conversation[]>('/api/conversations'),
  messages: (id: string) => json<ApiMessage[]>(`/api/conversations/${id}/messages`),
  deleteConversation: (id: string) => fetch(`/api/conversations/${id}`, { method: 'DELETE' }),
  ingest: () => fetch('/api/ingest', { method: 'POST' }),
  feedback: (messageId: string, rating: 1 | -1) =>
    fetch('/api/feedback', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ messageId, rating }),
    }),
};

// SSE event shapes from /api/chat
export type SseEvent =
  | { t: 'sources'; v: Source[]; conversationId: string }
  | { t: 'text'; v: string }
  | { t: 'saved'; messageId: string }
  | { t: 'error'; v: string };

export async function* streamChat(
  question: string,
  conversationId: string | null,
  signal: AbortSignal,
): AsyncGenerator<SseEvent> {
  const res = await fetch('/api/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ question, conversationId }),
    signal,
  });

  if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`);

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;

    buffer += decoder.decode(value, { stream: true });
    const parts = buffer.split('\n\n');
    buffer = parts.pop() ?? '';

    for (const part of parts) {
      if (!part.startsWith('data: ')) continue;
      const raw = part.slice(6).trim();
      if (raw === '[DONE]') return;
      try {
        yield JSON.parse(raw) as SseEvent;
      } catch {
        // malformed SSE line — skip
      }
    }
  }
}