import { isValidElement, useRef, useState } from 'react';
import type { ComponentProps, ReactNode } from 'react';

// Pulls "bash" out of the code child's "language-bash" class (set by the
// markdown fence info string and preserved by rehype-highlight).
function detectLanguage(children: ReactNode): string | null {
  if (!isValidElement(children)) return null;
  const className = (children.props as { className?: string }).className ?? '';
  const match = /language-([\w-]+)/.exec(className);
  return match ? match[1] : null;
}

// Fenced code block with a header bar (language label + copy button).
// The block is always dark — commands stay high-contrast in both themes,
// and the github-dark highlight palette works on it unconditionally.
export function CodeBlock({ children, ...rest }: ComponentProps<'pre'>) {
  const preRef = useRef<HTMLPreElement>(null);
  const [copied, setCopied] = useState(false);

  const language = detectLanguage(children);

  async function handleCopy() {
    // Read from the DOM so highlighted token spans are flattened back to plain text.
    const text = preRef.current?.textContent?.replace(/\n$/, '') ?? '';
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch { /* clipboard unavailable */ }
  }

  return (
    <div className="not-prose my-3 rounded-lg overflow-hidden border border-gray-300 dark:border-gray-700 bg-[#0d1117]">
      <div className="flex items-center justify-between gap-2 px-3 py-1.5 bg-[#161b22] border-b border-gray-700">
        <span className="text-[0.7rem] font-mono uppercase tracking-wider text-gray-300 select-none">
          {language ?? 'code'}
        </span>
        <button
          onClick={handleCopy}
          aria-label="Copy code to clipboard"
          className="border border-gray-600 rounded-md px-2 py-0.5 text-xs leading-relaxed cursor-pointer bg-transparent text-gray-200 hover:bg-white/10 hover:border-gray-400 transition-colors"
        >
          {copied ? '✓ Copied' : '⧉ Copy'}
        </button>
      </div>
      <pre
        ref={preRef}
        {...rest}
        className="m-0 overflow-x-auto px-3.5 py-3 text-[0.85rem] leading-relaxed text-[#e6edf3]"
      >
        {children}
      </pre>
    </div>
  );
}
