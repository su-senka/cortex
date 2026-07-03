import { useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../api';
import { useAppStore } from '../store';
import { relativeTime } from '../time';

export function Sidebar() {
  const qc = useQueryClient();
  const { data: convs = [] } = useQuery({ queryKey: ['conversations'], queryFn: api.conversations });
  const {
    activeConversationId, setActiveConversation, clearChat,
    sidebarOpen, setSidebarOpen,
  } = useAppStore();

  const [search, setSearch] = useState('');
  const searchRef = useRef<HTMLInputElement>(null);

  // ⌘K / Ctrl+K focuses the search input; ⌘N / Ctrl+N starts a new chat
  // (browsers may reserve ⌘N — it works where the page is allowed to handle it).
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (!(e.metaKey || e.ctrlKey) || e.altKey || e.shiftKey) return;
      if (e.key === 'k') {
        e.preventDefault();
        setSidebarOpen(true);
        searchRef.current?.focus();
      } else if (e.key === 'n') {
        e.preventDefault();
        clearChat();
      }
    }
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [clearChat, setSidebarOpen]);

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    return q ? convs.filter((c) => c.title.toLowerCase().includes(q)) : convs;
  }, [convs, search]);

  function select(id: string) {
    setActiveConversation(id);
    setSidebarOpen(false);
  }

  function newChat() {
    clearChat();
    setSidebarOpen(false);
  }

  async function handleDelete(e: React.MouseEvent, id: string) {
    e.stopPropagation();
    await api.deleteConversation(id);
    if (activeConversationId === id) clearChat();
    qc.invalidateQueries({ queryKey: ['conversations'] });
  }

  return (
    <>
      {sidebarOpen && (
        <div
          className="fixed inset-0 z-30 bg-black/40 sm:hidden"
          onClick={() => setSidebarOpen(false)}
          aria-hidden="true"
        />
      )}
      <aside
        className={`w-64 bg-slate-800 text-slate-200 flex flex-col shrink-0 overflow-hidden dark:bg-gray-900 dark:border-r dark:border-gray-800
          max-sm:fixed max-sm:inset-y-0 max-sm:left-0 max-sm:z-40 max-sm:w-72 max-sm:transition-transform max-sm:duration-200
          ${sidebarOpen ? 'max-sm:translate-x-0' : 'max-sm:-translate-x-full'}`}
      >
        <div className="px-3.5 py-3 border-b border-white/[0.08] flex flex-col gap-2">
          <button
            onClick={newChat}
            className="w-full bg-white/[0.08] border border-white/[0.12] text-slate-200 rounded-md px-3 py-2 text-sm text-left flex items-center gap-2 hover:bg-white/[0.14] cursor-pointer"
          >
            <span>＋</span> New chat
            <kbd className="ml-auto text-[0.65rem] text-slate-500 max-sm:hidden">⌘N</kbd>
          </button>
          <input
            ref={searchRef}
            type="search"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search…  ⌘K"
            aria-label="Search conversations"
            className="w-full bg-white/[0.06] border border-white/[0.1] text-slate-200 placeholder-slate-500 rounded-md px-3 py-1.5 text-[0.8125rem] outline-none focus:border-white/[0.3]"
          />
        </div>

        <div className="flex-1 overflow-y-auto py-1">
          {filtered.length === 0 ? (
            <p className="text-[0.775rem] text-slate-500 px-4 py-3">
              {convs.length === 0 ? 'No conversations yet.' : 'No matches.'}
            </p>
          ) : (
            filtered.map((c) => (
              <div
                key={c.id}
                role="button"
                tabIndex={0}
                onClick={() => select(c.id)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    select(c.id);
                  }
                }}
                className={`group flex items-center gap-1.5 mx-2 my-0.5 px-3 py-2 rounded-md cursor-pointer min-w-0 focus:outline-none focus-visible:ring-1 focus-visible:ring-white/40 ${
                  c.id === activeConversationId
                    ? 'bg-white/[0.12]'
                    : 'hover:bg-white/[0.07]'
                }`}
              >
                <span className="flex-1 min-w-0">
                  <span
                    title={c.title}
                    className={`block text-[0.8125rem] truncate ${
                      c.id === activeConversationId ? 'text-slate-100' : 'text-slate-400'
                    }`}
                  >
                    {c.title}
                  </span>
                  <span className="block text-[0.7rem] text-slate-500 mt-px">
                    {relativeTime(c.createdAt)}
                  </span>
                </span>
                <button
                  onClick={(e) => handleDelete(e, c.id)}
                  className="opacity-0 group-hover:opacity-100 focus-visible:opacity-100 bg-transparent border-none text-slate-400 hover:text-red-400 hover:bg-white/[0.08] text-xs px-1 py-0.5 rounded cursor-pointer shrink-0"
                  title="Delete conversation"
                  aria-label={`Delete conversation: ${c.title}`}
                >
                  ✕
                </button>
              </div>
            ))
          )}
        </div>
      </aside>
    </>
  );
}
