import { useState } from 'react';
import ReactMarkdown from 'react-markdown';
import rehypeHighlight from 'rehype-highlight';
import rehypeRaw from 'rehype-raw';
import { api } from '../api';
import { useAppStore } from '../store';
import type { ChatMessage } from '../types';

interface Props {
  message: ChatMessage;
}

// Replaces [^N] footnote refs with an HTML <cite> tag that rehype-raw passes through.
function preprocessCitations(text: string): string {
  return text.replace(/\[\^(\d+)\]/g, '<cite data-n="$1">[$1]</cite>');
}

export function MessageBubble({ message }: Props) {
  const { sources, setSelectedSource } = useAppStore();
  const [feedback, setFeedback] = useState<1 | -1 | null>(message.feedback ?? null);
  const [copied, setCopied] = useState(false);

  const isUser = message.role === 'user';

  if (isUser) {
    return (
      <div className="self-end max-w-[85%] sm:max-w-[70%]">
        <div className="bg-blue-800 text-white px-4 py-2.5 rounded-2xl rounded-br-md leading-relaxed break-words whitespace-pre-wrap dark:bg-blue-700">
          {message.content}
        </div>
      </div>
    );
  }

  async function handleCopy() {
    try {
      await navigator.clipboard.writeText(message.content);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch { /* clipboard unavailable */ }
  }

  // Assistant messages render full-width without a bubble — the visual asymmetry
  // separates the two roles better than mirrored bubbles.
  return (
    <div className="group w-full self-start">
      {message.isStreaming && message.content === '' ? (
        <div className="flex gap-1 items-center py-2" role="status" aria-label="Assistant is thinking">
          {[0, 1, 2].map((i) => (
            <span key={i} className="typing-dot w-2 h-2 rounded-full bg-gray-400 dark:bg-gray-500" />
          ))}
        </div>
      ) : message.isStreaming ? (
        <div className="leading-relaxed break-words whitespace-pre-wrap">
          {message.content}
          <span className="streaming-cursor" aria-hidden="true">▋</span>
        </div>
      ) : (
        <div className="prose prose-sm max-w-none dark:prose-invert prose-code:before:content-none prose-code:after:content-none prose-code:bg-gray-100 prose-code:px-1 prose-code:rounded dark:prose-code:bg-gray-800">
          <ReactMarkdown
            rehypePlugins={[rehypeHighlight, rehypeRaw]}
            components={{
              // Intercept <cite data-n="N"> inserted by preprocessCitations.
              cite: ({ node }) => {
                const n = Number(
                  (node?.properties as Record<string, unknown> | undefined)?.dataN ?? 0,
                );
                const source = sources.find((s) => s.sourceIndex === n);
                return (
                  <sup>
                    <button
                      onClick={() => source && setSelectedSource(source)}
                      className="text-blue-800 dark:text-blue-400 font-bold text-[0.72em] leading-none cursor-pointer hover:underline bg-transparent border-none p-0"
                    >
                      [{n}]
                    </button>
                  </sup>
                );
              },
            }}
          >
            {preprocessCitations(message.content)}
          </ReactMarkdown>
        </div>
      )}

      {!message.isStreaming && message.content !== '' && (
        <div
          className={`flex items-center gap-1.5 mt-1.5 transition-opacity ${
            feedback !== null ? 'opacity-100' : 'opacity-0 group-hover:opacity-100 focus-within:opacity-100'
          }`}
        >
          <button
            onClick={handleCopy}
            title="Copy to clipboard"
            aria-label="Copy message to clipboard"
            className="border border-gray-200 rounded-md px-2 py-0.5 text-sm leading-none cursor-pointer text-gray-400 hover:border-gray-400 hover:text-gray-600 transition-colors dark:border-gray-700 dark:text-gray-500 dark:hover:border-gray-500 dark:hover:text-gray-300"
          >
            {copied ? '✓ Copied' : '⧉'}
          </button>
          {message.id && ([1, -1] as const).map((rating) => (
            <button
              key={rating}
              disabled={feedback !== null}
              aria-label={rating === 1 ? 'Good answer' : 'Bad answer'}
              onClick={async () => {
                try {
                  await api.feedback(message.id!, rating);
                  setFeedback(rating);
                } catch { /* ignore */ }
              }}
              className={`border rounded-md px-2 py-0.5 text-sm leading-none cursor-pointer transition-colors disabled:cursor-default
                ${feedback === rating && rating === 1  ? 'bg-green-100 border-green-300 text-green-700 dark:bg-green-900/40 dark:border-green-700 dark:text-green-400' : ''}
                ${feedback === rating && rating === -1 ? 'bg-red-100 border-red-300 text-red-600 dark:bg-red-900/40 dark:border-red-700 dark:text-red-400'   : ''}
                ${feedback !== rating ? 'border-gray-200 text-gray-400 hover:border-gray-400 hover:text-gray-600 disabled:opacity-60 dark:border-gray-700 dark:text-gray-500 dark:hover:border-gray-500 dark:hover:text-gray-300' : ''}
              `}
            >
              {rating === 1 ? '👍' : '👎'}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
