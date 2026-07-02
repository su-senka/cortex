import { useAppStore } from '../store';
import { ChunkModal } from './ChunkModal';

export function SourcesPane() {
  const { sources, citations, selectedSource, setSelectedSource } = useAppStore();

  const citedIndexes = new Set(Object.keys(citations).map(Number));

  return (
    <>
      <aside className="w-70 bg-white border-l border-gray-200 flex flex-col overflow-hidden max-[900px]:hidden">
        <h2 className="text-[0.8125rem] font-semibold uppercase tracking-widest text-gray-500 px-4 pt-3.5 pb-2 border-b border-gray-200">
          Sources
        </h2>
        <div className="flex-1 overflow-y-auto py-1">
          {sources.length === 0 ? (
            <p className="text-gray-400 text-sm px-4 py-4 text-center">
              Sources from the last answer will appear here.
            </p>
          ) : (
            sources.map((s) => {
              const cited = citedIndexes.has(s.sourceIndex);
              return (
                <div
                  key={s.sourceIndex}
                  onClick={() => setSelectedSource(s)}
                  className={`px-4 py-2 border-b border-gray-50 last:border-0 cursor-pointer transition-colors ${
                    cited ? 'bg-blue-50 hover:bg-blue-100' : 'hover:bg-gray-50'
                  }`}
                >
                  <div className="font-semibold text-sm text-blue-800 flex items-center gap-1.5 flex-wrap">
                    [{s.sourceIndex}] {s.title || s.sourceFile}
                    {cited && (
                      <span className="bg-blue-800 text-white text-[0.6rem] font-bold px-1.5 py-px rounded tracking-wide">
                        CITED
                      </span>
                    )}
                  </div>
                  <div className="text-xs text-gray-500 mt-px break-all">{s.sourceFile}</div>
                  {s.sectionHeading && (
                    <div className="text-xs text-gray-700 mt-0.5">§ {s.sectionHeading}</div>
                  )}
                  <div className="text-[0.7rem] text-gray-400 mt-0.5">
                    Score: {(s.score * 100).toFixed(1)}%
                  </div>
                  <div className="text-[0.68rem] text-gray-400 mt-0.5">Click to view passage</div>
                </div>
              );
            })
          )}
        </div>
      </aside>

      {selectedSource && <ChunkModal source={selectedSource} onClose={() => setSelectedSource(null)} />}
    </>
  );
}