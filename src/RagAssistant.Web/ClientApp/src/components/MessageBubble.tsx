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

  const isUser = message.role === 'user';

  if (isUser) {
    return (
      <div className="max-w-[780px] w-full self-end">
        <div className="bg-blue-800 text-white px-3.5 py-2.5 rounded-[10px] rounded-br-[3px] leading-relaxed break-words">
          {message.content}
        </div>
      </div>
    );
  }

  return (
    <div className="max-w-[780px] w-full self-start">
      <div className="bg-white border border-gray-200 px-3.5 py-2.5 rounded-[10px] rounded-bl-[3px] leading-relaxed break-words">
        {message.isStreaming && message.content === '' ? (
          <span className="text-gray-400 text-sm animate-pulse">Thinking…</span>
        ) : message.isStreaming ? (
          <span className="whitespace-pre-wrap">{message.content}</span>
        ) : (
          <div className="prose prose-sm max-w-none prose-code:before:content-none prose-code:after:content-none prose-code:bg-gray-100 prose-code:px-1 prose-code:rounded">
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
                        className="text-blue-800 font-bold text-[0.72em] leading-none cursor-pointer hover:underline bg-transparent border-none p-0"
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
      </div>

      {message.id && (
        <div className="flex gap-1.5 mt-1.5 pl-0.5">
          {([1, -1] as const).map((rating) => (
            <button
              key={rating}
              disabled={feedback !== null}
              onClick={async () => {
                try {
                  await api.feedback(message.id!, rating);
                  setFeedback(rating);
                } catch { /* ignore */ }
              }}
              className={`border rounded-md px-2 py-0.5 text-sm leading-none cursor-pointer transition-colors disabled:cursor-default
                ${feedback === rating && rating === 1  ? 'bg-green-100 border-green-300 text-green-700' : ''}
                ${feedback === rating && rating === -1 ? 'bg-red-100 border-red-300 text-red-600'   : ''}
                ${feedback !== rating ? 'border-gray-200 text-gray-400 hover:border-gray-400 hover:text-gray-600 disabled:opacity-60' : ''}
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