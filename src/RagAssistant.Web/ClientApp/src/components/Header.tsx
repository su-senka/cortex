import { useState } from 'react';
import { api } from '../api';
import { useAppStore } from '../store';
import type { Theme } from '../theme';
import type { User } from '../types';

interface Props {
  user: User | undefined;
}

const NEXT_THEME: Record<Theme, Theme> = { system: 'dark', dark: 'light', light: 'system' };
const THEME_ICON: Record<Theme, string> = { system: '◐', dark: '☾', light: '☀' };

export function Header({ user }: Props) {
  const { setSidebarOpen, theme, setTheme } = useAppStore();
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
    <header className="bg-blue-800 text-white h-[52px] flex items-center gap-3 px-4 sm:px-5 shrink-0 dark:bg-gray-900 dark:border-b dark:border-gray-800">
      <button
        onClick={() => setSidebarOpen(true)}
        aria-label="Open conversation list"
        className="sm:hidden bg-transparent border-none text-white text-xl leading-none cursor-pointer px-1"
      >
        ☰
      </button>
      <h1 className="text-[1.0625rem] font-semibold">Cortex</h1>
      <div className="flex-1" />
      {user && <span className="text-sm opacity-85 whitespace-nowrap max-sm:hidden">{user.name}</span>}
      <button
        onClick={() => setTheme(NEXT_THEME[theme])}
        title={`Theme: ${theme} — click to change`}
        aria-label={`Theme: ${theme}. Click to change.`}
        className="bg-white/15 border border-white/30 text-white text-sm w-7 h-7 rounded-md cursor-pointer leading-none hover:bg-white/25"
      >
        {THEME_ICON[theme]}
      </button>
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
