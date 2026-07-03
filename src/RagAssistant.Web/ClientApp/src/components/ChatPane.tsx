import { useEffect, useRef, useState, useCallback } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { api, streamChat } from '../api';
import { useAppStore, type Citations } from '../store';
import type { ChatMessage, Source } from '../types';
import { MessageBubble } from './MessageBubble';
import { InputBar } from './InputBar';

const SUGGESTIONS = [
  'How do I connect to the VPN from macOS?',
  'What are the steps to generate a TLS certificate?',
  'What is a P1 incident?',
  "What's on the onboarding checklist for new engineers?",
];

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

  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [busy, setBusy] = useState(false);
  const [showJump, setShowJump] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);
  // Whether the user is at (or near) the bottom — auto-scroll only then, so
  // scrolling up to re-read mid-stream isn't fought by the incoming tokens.
  const atBottomRef = useRef(true);
  const abortRef = useRef<AbortController | null>(null);
  // Conversations we just finished streaming — skip the API fetch for those,
  // we already have the complete messages in state.
  const justStreamedRef = useRef(new Set<string>());

  // Load messages when active conversation changes.
  useEffect(() => {
    if (!activeConversationId) {
      setMessages([]);
      return;
    }
    if (justStreamedRef.current.has(activeConversationId)) {
      justStreamedRef.current.delete(activeConversationId);
      return;
    }
    let cancelled = false;
    api.messages(activeConversationId).then((msgs) => {
      if (cancelled) return;
      atBottomRef.current = true;
      setMessages(
        msgs.map((m) => ({ role: m.role as 'user' | 'assistant', content: m.content })),
      );
    }).catch(() => {});
    return () => { cancelled = true; };
  }, [activeConversationId]);

  useEffect(() => {
    if (atBottomRef.current) {
      scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight });
      setShowJump(false);
    }
  }, [messages]);

  function handleScroll() {
    const el = scrollRef.current;
    if (!el) return;
    const nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
    atBottomRef.current = nearBottom;
    setShowJump(!nearBottom);
  }

  function jumpToBottom() {
    atBottomRef.current = true;
    setShowJump(false);
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: 'smooth' });
  }

  const send = useCallback(async (question: string) => {
    if (busy) return;

    abortRef.current?.abort();
    const ac = new AbortController();
    abortRef.current = ac;

    setBusy(true);
    atBottomRef.current = true;

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
    <main className="relative flex flex-col flex-1 min-w-0">
      {messages.length === 0 ? (
        <div className="flex-1 flex flex-col items-center justify-center gap-6 px-6">
          <div className="text-center">
            <h2 className="text-xl font-semibold text-gray-700 dark:text-gray-200">
              What do you want to know?
            </h2>
            <p className="text-sm text-gray-500 dark:text-gray-400 mt-1.5">
              Answers come straight from the internal documentation, with citations.
            </p>
          </div>
          <div className="grid gap-2 sm:grid-cols-2 w-full max-w-xl">
            {SUGGESTIONS.map((s) => (
              <button
                key={s}
                onClick={() => send(s)}
                className="text-left text-sm text-gray-700 bg-white border border-gray-200 rounded-xl px-4 py-3 cursor-pointer hover:border-blue-400 hover:bg-blue-50 transition-colors dark:bg-gray-900 dark:border-gray-700 dark:text-gray-300 dark:hover:border-blue-500 dark:hover:bg-gray-800"
              >
                {s}
              </button>
            ))}
          </div>
        </div>
      ) : (
        <div
          ref={scrollRef}
          onScroll={handleScroll}
          aria-live="polite"
          className="flex-1 overflow-y-auto px-4 sm:px-6 py-5"
        >
          <div className="max-w-3xl mx-auto flex flex-col gap-5">
            {messages.map((msg, i) => (
              <MessageBubble key={i} message={msg} />
            ))}
          </div>
        </div>
      )}

      {showJump && (
        <button
          onClick={jumpToBottom}
          aria-label="Scroll to bottom"
          className="absolute bottom-24 left-1/2 -translate-x-1/2 bg-white border border-gray-300 text-gray-600 rounded-full w-9 h-9 shadow-md cursor-pointer text-lg leading-none hover:bg-gray-50 dark:bg-gray-800 dark:border-gray-600 dark:text-gray-300 dark:hover:bg-gray-700"
        >
          ↓
        </button>
      )}

      <InputBar onSend={send} busy={busy} />
    </main>
  );
}
