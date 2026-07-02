import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../api';
import { useAppStore } from '../store';

export function Sidebar() {
  const qc = useQueryClient();
  const { data: convs = [] } = useQuery({ queryKey: ['conversations'], queryFn: api.conversations });
  const { activeConversationId, setActiveConversation, clearChat } = useAppStore();

  async function handleDelete(e: React.MouseEvent, id: string) {
    e.stopPropagation();
    await api.deleteConversation(id);
    if (activeConversationId === id) clearChat();
    qc.invalidateQueries({ queryKey: ['conversations'] });
  }

  return (
    <aside className="w-60 bg-slate-800 text-slate-200 flex flex-col shrink-0 overflow-hidden max-sm:hidden">
      <div className="px-3.5 py-3 border-b border-white/[0.08]">
        <button
          onClick={clearChat}
          className="w-full bg-white/[0.08] border border-white/[0.12] text-slate-200 rounded-md px-3 py-2 text-sm text-left flex items-center gap-2 hover:bg-white/[0.14] cursor-pointer"
        >
          <span>＋</span> New chat
        </button>
      </div>

      <div className="flex-1 overflow-y-auto py-1">
        {convs.length === 0 ? (
          <p className="text-[0.775rem] text-slate-500 px-4 py-3">No conversations yet.</p>
        ) : (
          convs.map((c) => (
            <div
              key={c.id}
              onClick={() => setActiveConversation(c.id)}
              className={`group flex items-center gap-1.5 mx-2 my-0.5 px-3 py-2 rounded-md cursor-pointer min-w-0 ${
                c.id === activeConversationId
                  ? 'bg-white/[0.12]'
                  : 'hover:bg-white/[0.07]'
              }`}
            >
              <span
                title={c.title}
                className={`flex-1 text-[0.8125rem] truncate ${
                  c.id === activeConversationId ? 'text-slate-100' : 'text-slate-400'
                }`}
              >
                {c.title}
              </span>
              <button
                onClick={(e) => handleDelete(e, c.id)}
                className="opacity-0 group-hover:opacity-100 bg-transparent border-none text-slate-400 hover:text-red-400 hover:bg-white/[0.08] text-xs px-1 py-0.5 rounded cursor-pointer shrink-0"
                title="Delete conversation"
              >
                ✕
              </button>
            </div>
          ))
        )}
      </div>
    </aside>
  );
}