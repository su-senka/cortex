import { useEffect } from 'react';
import { useAppStore } from '../store';
import type { Source } from '../types';

interface Props {
  source: Source;
  onClose: () => void;
}

const STOP_WORDS = new Set([
  'about','after','again','also','another','because','before','between','both',
  'could','does','done','each','every','from','have','here','into','just',
  'like','made','make','many','more','most','much','must','need','never',
  'only','other','over','same','should','some','such','than','that','their',
  'them','then','there','these','they','this','those','through','time',
  'under','very','want','well','were','what','when','where','which','while',
  'will','with','would','your',
]);

function escapeRegex(s: string) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function HighlightedText({ text, sentences }: { text: string; sentences: string[] }) {
  const keywords = [
    ...new Set(
      sentences
        .flatMap((s) => s.split(/\s+/))
        .map((w) => w.replace(/[^\w]/g, '').toLowerCase())
        .filter((w) => w.length >= 5 && !STOP_WORDS.has(w)),
    ),
  ].sort((a, b) => b.length - a.length);

  if (!keywords.length) return <>{text}</>;

  const re = new RegExp(`(${keywords.map(escapeRegex).join('|')})`, 'gi');
  const parts = text.split(re);

  return (
    <>
      {parts.map((part, i) =>
        i % 2 === 1 ? (
          <mark key={i} className="bg-yellow-200 rounded-sm px-px">
            {part}
          </mark>
        ) : (
          <span key={i}>{part}</span>
        ),
      )}
    </>
  );
}

export function ChunkModal({ source, onClose }: Props) {
  const { citations } = useAppStore();
  const sentences = citations[source.sourceIndex] ?? [];

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose();
    }
    document.addEventListener('keydown', onKey);
    document.body.style.overflow = 'hidden';
    return () => {
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = '';
    };
  }, [onClose]);

  const subtitle = [source.sourceFile, source.sectionHeading ? `§ ${source.sectionHeading}` : null]
    .filter(Boolean)
    .join('  ·  ');

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="absolute inset-0 bg-black/45" onClick={onClose} />
      <div className="relative bg-white rounded-xl w-[min(700px,92vw)] max-h-[82vh] flex flex-col shadow-2xl">
        <div className="flex items-start justify-between gap-3 px-5 pt-4 pb-3 border-b border-gray-200">
          <div className="flex-1 min-w-0">
            <div className="font-semibold text-[0.9375rem] truncate">{source.title || source.sourceFile}</div>
            <div className="text-[0.8rem] text-gray-500 mt-0.5">{subtitle}</div>
          </div>
          <button
            onClick={onClose}
            className="bg-transparent border-none cursor-pointer text-lg text-gray-400 hover:text-gray-900 leading-none shrink-0"
          >
            ✕
          </button>
        </div>

        <div className="flex-1 overflow-y-auto px-5 py-4">
          <pre className="whitespace-pre-wrap font-[inherit] text-sm leading-relaxed text-gray-700">
            <HighlightedText text={source.chunkText ?? ''} sentences={sentences} />
          </pre>
        </div>

        {sentences.length > 0 && (
          <div className="text-xs text-gray-400 px-5 py-2 border-t border-gray-100">
            Highlighted words were extracted from the answer that cited this source.
          </div>
        )}
      </div>
    </div>
  );
}