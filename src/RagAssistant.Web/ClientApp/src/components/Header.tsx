import { useState } from 'react';
import { api } from '../api';
import type { User } from '../types';

interface Props {
  user: User | undefined;
}

export function Header({ user }: Props) {
  const [ingestState, setIngestState] = useState<'idle' | 'busy' | 'done' | 'error'>('idle');

  async function handleReindex() {
    setIngestState('busy');
    try {
      const res = await api.ingest();
      setIngestState(res.ok ? 'done' : 'error');
    } catch {
      setIngestState('error');
    }
    setTimeout(() => setIngestState('idle'), 2000);
  }

  const ingestLabel =
    ingestState === 'busy'  ? 'Indexing…' :
    ingestState === 'done'  ? '✓ Done'    :
    ingestState === 'error' ? '✗ Error'   : '↺ Re-index';

  return (
    <header className="bg-blue-800 text-white h-[52px] flex items-center gap-3 px-5 shrink-0">
      <h1 className="text-[1.0625rem] font-semibold">Cortex</h1>
      <div className="flex-1" />
      {user && <span className="text-sm opacity-85 whitespace-nowrap">{user.name}</span>}
      {user?.isAdmin && (
        <button
          onClick={handleReindex}
          disabled={ingestState === 'busy'}
          className="bg-white/15 border border-white/30 text-white text-xs px-3 py-1 rounded-md cursor-pointer whitespace-nowrap hover:bg-white/25 disabled:opacity-60 disabled:cursor-not-allowed"
        >
          {ingestLabel}
        </button>
      )}
      <a
        href="/auth/logout"
        className="bg-white/15 border border-white/30 text-white text-xs px-3 py-1 rounded-md whitespace-nowrap hover:bg-white/25"
      >
        Sign out
      </a>
    </header>
  );
}