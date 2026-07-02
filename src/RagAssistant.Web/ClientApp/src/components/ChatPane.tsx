import { useEffect, useRef, useState, useCallback } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { api, streamChat } from '../api';
import { useAppStore, type Citations } from '../store';
import type { ChatMessage, Source } from '../types';
import { MessageBubble } from './MessageBubble';
import { InputBar } from './InputBar';

const WELCOME: ChatMessage = {
  role: 'assistant',
  content: 'Hello! Ask me anything about the internal documentation.',
};

function extractCitations(text: string): Citations {
  const cits: Citations = {};
  const re = /([^.!?\n]*[.!?]?)\s*\[\^(\d+)\]/g;
  let m;
  while ((m = re.exec(text)) !== null) {
    const sentence = m[1].trim();
    const idx = parseInt(m[2], 10);
    if (!cits[idx]) cits[idx] = [];
    if (sentence) cits[idx].push(sentence);
  }
  return cits;
}

export function ChatPane() {
  const qc = useQueryClient();
  const { activeConversationId, setActiveConversation, setSources } = useAppStore();

  const [messages, setMessages] = useState<ChatMessage[]>([WELCOME]);
  const [busy, setBusy] = useState(false);
  const bottomRef = useRef<HTMLDivElement>(null);
  const abortRef = useRef<AbortController | null>(null);
  // Conversations we just finished streaming — skip the API fetch for those,
  // we already have the complete messages in state.
  const justStreamedRef = useRef(new Set<string>());

  // Load messages when active conversation changes.
  useEffect(() => {
    if (!activeConversationId) {
      setMessages([WELCOME]);
      return;
    }
    if (justStreamedRef.current.has(activeConversationId)) {
      justStreamedRef.current.delete(activeConversationId);
      return;
    }
    let cancelled = false;
    api.messages(activeConversationId).then((msgs) => {
      if (cancelled) return;
      setMessages(
        msgs.map((m) => ({ role: m.role as 'user' | 'assistant', content: m.content })),
      );
    }).catch(() => {});
    return () => { cancelled = true; };
  }, [activeConversationId]);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  const send = useCallback(async (question: string) => {
    if (busy) return;

    abortRef.current?.abort();
    const ac = new AbortController();
    abortRef.current = ac;

    setBusy(true);

    // Append user message + placeholder assistant message.
    setMessages((prev) => [
      ...prev,
      { role: 'user', content: question },
      { role: 'assistant', content: '', isStreaming: true },
    ]);

    let sources: Source[] = [];
    let citations: Citations = {};
    let fullText = '';
    // Track a new conversation ID from the server without updating the store yet.
    // Updating the store during streaming triggers the useEffect above, which
    // re-fetches messages from the API and overwrites the in-progress stream.
    let newConversationId: string | null = null;

    try {
      for await (const event of streamChat(question, activeConversationId, ac.signal)) {
        if (event.t === 'sources') {
          sources = event.v;
          setSources(sources, {});
          if (event.conversationId !== activeConversationId) {
            newConversationId = event.conversationId;
            qc.invalidateQueries({ queryKey: ['conversations'] });
          }
        } else if (event.t === 'text') {
          fullText += event.v;
          setMessages((prev) => {
            const next = [...prev];
            next[next.length - 1] = { role: 'assistant', content: fullText, isStreaming: true };
            return next;
          });
        } else if (event.t === 'saved') {
          citations = extractCitations(fullText);
          setSources(sources, citations);
          setMessages((prev) => {
            const next = [...prev];
            next[next.length - 1] = {
              role: 'assistant',
              content: fullText,
              id: event.messageId,
            };
            return next;
          });
        } else if (event.t === 'error') {
          setMessages((prev) => {
            const next = [...prev];
            next[next.length - 1] = { role: 'assistant', content: `Error: ${event.v}` };
            return next;
          });
        }
      }
    } catch (err) {
      if ((err as Error).name !== 'AbortError') {
        setMessages((prev) => {
          const next = [...prev];
          next[next.length - 1] = {
            role: 'assistant',
            content: `Error: ${(err as Error).message}`,
          };
          return next;
        });
      }
    } finally {
      // Ensure streaming flag is cleared even if saved event didn't arrive.
      setMessages((prev) => {
        const last = prev[prev.length - 1];
        if (last?.isStreaming) {
          return [...prev.slice(0, -1), { ...last, isStreaming: false }];
        }
        return prev;
      });
      // Commit the conversation ID now that streaming is done.  Marking it in
      // justStreamedRef prevents the useEffect from re-fetching and overwriting
      // the messages we already have.  Re-apply sources because setActiveConversation
      // resets them in the store.
      if (newConversationId) {
        justStreamedRef.current.add(newConversationId);
        setActiveConversation(newConversationId);
        if (sources.length > 0) setSources(sources, citations);
      }
      setBusy(false);
    }
  }, [busy, activeConversationId, setActiveConversation, setSources, qc]);

  return (
    <main className="flex flex-col flex-1 min-w-0">
      <div className="flex-1 overflow-y-auto px-6 py-5 flex flex-col gap-4">
        {messages.map((msg, i) => (
          <MessageBubble key={i} message={msg} />
        ))}
        <div ref={bottomRef} />
      </div>
      <InputBar onSend={send} busy={busy} />
    </main>
  );
}