import { useState } from 'react';
import { useAppStore } from '../store';
import { ChunkModal } from './ChunkModal';

// First two non-empty lines of the chunk, without the [Section: …] prefix —
// enough to judge relevance before opening the full passage modal.
function previewLines(chunkText: string | undefined): string {
  return (chunkText ?? '')
    .replace(/^\[Section:[^\]]*\]\s*/, '')
    .split('\n')
    .map((l) => l.trim())
    .filter(Boolean)
    .slice(0, 2)
    .join('\n');
}

export function SourcesPane() {
  const { sources, citations, selectedSource, setSelectedSource } = useAppStore();
  const [open, setOpen] = useState(true);

  const citedIndexes = new Set(Object.keys(citations).map(Number));

  return (
    <>
      <aside
        className={`bg-white border-l border-gray-200 dark:bg-gray-900 dark:border-gray-800 flex flex-col overflow-hidden transition-[width] duration-200 max-[900px]:hidden ${
          open ? 'w-72' : 'w-11'
        }`}
      >
        <div className="flex items-center gap-2 px-3 pt-3 pb-2 border-b border-gray-200 dark:border-gray-800">
          <button
            onClick={() => setOpen(!open)}
            aria-expanded={open}
            aria-label={open ? 'Collapse sources panel' : 'Expand sources panel'}
            title={open ? 'Collapse' : 'Expand sources'}
            className="bg-transparent border border-gray-200 dark:border-gray-700 rounded-md w-6 h-6 text-xs leading-none cursor-pointer text-gray-400 hover:text-gray-700 dark:hover:text-gray-200 shrink-0"
          >
            {open ? '»' : '«'}
          </button>
          {open && (
            <h2 className="text-[0.8125rem] font-semibold uppercase tracking-widest text-gray-500 dark:text-gray-400">
              Sources{sources.length > 0 && ` (${sources.length})`}
            </h2>
          )}
        </div>

        {open && (
          <div className="flex-1 overflow-y-auto py-1">
            {sources.length === 0 ? (
              <p className="text-gray-400 dark:text-gray-500 text-sm px-4 py-4 text-center">
                Sources from the last answer will appear here.
              </p>
            ) : (
              sources.map((s) => {
                const cited = citedIndexes.has(s.sourceIndex);
                const preview = previewLines(s.chunkText);
                return (
                  <button
                    key={s.sourceIndex}
                    onClick={() => setSelectedSource(s)}
                    className={`block w-full text-left px-4 py-2.5 border-b border-gray-50 dark:border-gray-800/60 last:border-0 cursor-pointer transition-colors ${
                      cited
                        ? 'bg-blue-50 hover:bg-blue-100 dark:bg-blue-950/40 dark:hover:bg-blue-950/70'
                        : 'hover:bg-gray-50 dark:hover:bg-gray-800/60'
                    }`}
                  >
                    <span className="font-semibold text-sm text-blue-800 dark:text-blue-400 flex items-center gap-1.5 flex-wrap">
                      [{s.sourceIndex}] {s.title || s.sourceFile}
                      {cited && (
                        <span className="bg-blue-800 dark:bg-blue-600 text-white text-[0.6rem] font-bold px-1.5 py-px rounded tracking-wide">
                          CITED
                        </span>
                      )}
                    </span>
                    <span className="block text-xs text-gray-500 dark:text-gray-400 mt-px break-all">
                      {s.sourceFile}
                      {s.sectionHeading && ` · § ${s.sectionHeading}`}
                    </span>
                    {preview && (
                      <span className="block text-xs text-gray-600 dark:text-gray-300 mt-1 line-clamp-2 whitespace-pre-line">
                        {preview}
                      </span>
                    )}
                    <span className="block text-[0.7rem] text-gray-400 dark:text-gray-500 mt-1">
                      Score: {(s.score * 100).toFixed(1)}% · click for full passage
                    </span>
                  </button>
                );
              })
            )}
          </div>
        )}
      </aside>

      {selectedSource && <ChunkModal source={selectedSource} onClose={() => setSelectedSource(null)} />}
    </>
  );
}
