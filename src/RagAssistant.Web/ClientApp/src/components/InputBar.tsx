import { useEffect, useRef, useState } from 'react';
import { useAppStore } from '../store';

interface Props {
  onSend: (question: string) => void;
  busy: boolean;
}

export function InputBar({ onSend, busy }: Props) {
  const ref = useRef<HTMLTextAreaElement>(null);
  const [hasText, setHasText] = useState(false);
  const { tagFilter, setTagFilter } = useAppStore();

  // Re-focus after each response completes so the user can keep typing.
  useEffect(() => {
    if (!busy) ref.current?.focus();
  }, [busy]);

  function handleSend() {
    const q = ref.current?.value.trim();
    if (!q || busy) return;
    ref.current!.value = '';
    ref.current!.style.height = 'auto';
    setHasText(false);
    onSend(q);
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  }

  function handleInput() {
    const el = ref.current!;
    el.style.height = 'auto';
    // Grows to ~5 lines, then scrolls.
    el.style.height = `${Math.min(el.scrollHeight, 130)}px`;
    setHasText(el.value.trim().length > 0);
  }

  return (
    <div className="px-4 sm:px-6 py-3 bg-white border-t border-gray-200 dark:bg-gray-900 dark:border-gray-800">
      <div className="max-w-3xl mx-auto flex gap-2 items-end">
        <textarea
          ref={ref}
          rows={1}
          placeholder="Ask a question about the docs…"
          autoFocus
          aria-label="Ask a question"
          onInput={handleInput}
          onKeyDown={handleKeyDown}
          className="flex-1 resize-none border border-gray-300 rounded-xl px-3.5 py-2.5 text-[0.9375rem] font-[inherit] leading-snug outline-none max-h-[130px] overflow-y-auto focus:border-blue-700 focus:ring-2 focus:ring-blue-200 bg-white text-gray-900 dark:bg-gray-800 dark:border-gray-700 dark:text-gray-100 dark:placeholder-gray-500 dark:focus:border-blue-500 dark:focus:ring-blue-900"
        />
        <button
          onClick={handleSend}
          disabled={busy || !hasText}
          aria-label="Send message"
          className="bg-blue-800 text-white border-none rounded-xl w-[42px] h-[42px] text-lg leading-none cursor-pointer shrink-0 hover:bg-blue-700 disabled:bg-blue-300 disabled:cursor-not-allowed dark:disabled:bg-gray-700"
        >
          ↑
        </button>
      </div>
      <div className="max-w-3xl mx-auto flex items-center gap-2 mt-1.5 px-1">
        <p className="flex-1 text-[0.7rem] text-gray-400 dark:text-gray-500 max-sm:hidden">
          Enter to send · Shift+Enter for a new line
        </p>
        <label className="flex items-center gap-1.5 text-[0.7rem] text-gray-400 dark:text-gray-500">
          <span className="max-sm:hidden">Scope to tag</span>
          <input
            type="text"
            value={tagFilter}
            onChange={(e) => setTagFilter(e.target.value)}
            placeholder="any tag"
            aria-label="Only search documents with this tag"
            title="Only documents whose front-matter tags contain this value are searched"
            className={`w-28 border rounded-md px-2 py-0.5 text-[0.7rem] outline-none bg-white text-gray-700 placeholder-gray-400 focus:border-blue-600 dark:bg-gray-800 dark:text-gray-200 dark:placeholder-gray-500 dark:focus:border-blue-500 ${
              tagFilter.trim()
                ? 'border-blue-500 dark:border-blue-500'
                : 'border-gray-300 dark:border-gray-700'
            }`}
          />
        </label>
      </div>
    </div>
  );
}
