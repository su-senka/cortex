import { useRef } from 'react';

interface Props {
  onSend: (question: string) => void;
  busy: boolean;
}

export function InputBar({ onSend, busy }: Props) {
  const ref = useRef<HTMLTextAreaElement>(null);

  function handleSend() {
    const q = ref.current?.value.trim();
    if (!q || busy) return;
    ref.current!.value = '';
    ref.current!.style.height = 'auto';
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
    el.style.height = `${Math.min(el.scrollHeight, 130)}px`;
  }

  return (
    <div className="flex gap-2 px-6 py-3.5 bg-white border-t border-gray-200">
      <textarea
        ref={ref}
        rows={1}
        placeholder="Ask a question about the docs…"
        autoFocus
        onInput={handleInput}
        onKeyDown={handleKeyDown}
        className="flex-1 resize-none border border-gray-300 rounded-lg px-3.5 py-2 text-[0.9375rem] font-[inherit] leading-snug outline-none max-h-[130px] overflow-y-auto focus:border-blue-800 focus:ring-2 focus:ring-blue-200"
      />
      <button
        onClick={handleSend}
        disabled={busy}
        className="bg-blue-800 text-white border-none rounded-lg px-4 text-[0.9375rem] cursor-pointer whitespace-nowrap self-end h-[38px] hover:bg-blue-700 disabled:bg-blue-300 disabled:cursor-not-allowed"
      >
        Send
      </button>
    </div>
  );
}