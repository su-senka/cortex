import { create } from 'zustand';
import type { Source } from './types';
import { applyTheme, getStoredTheme, type Theme } from './theme';

// Citations map: sourceIndex → sentences from the answer that cited that source.
// Used for keyword highlighting in the chunk modal.
export type Citations = Record<number, string[]>;

interface AppStore {
  activeConversationId: string | null;
  sources: Source[];
  citations: Citations;
  selectedSource: Source | null;
  sidebarOpen: boolean; // mobile drawer state; ignored on desktop widths
  theme: Theme;
  // Optional retrieval scope: only chunks whose front-matter tags contain this
  // value are searched. Empty string = no filter.
  tagFilter: string;

  setActiveConversation: (id: string | null) => void;
  setSources: (sources: Source[], citations: Citations) => void;
  setSelectedSource: (source: Source | null) => void;
  setSidebarOpen: (open: boolean) => void;
  setTheme: (theme: Theme) => void;
  setTagFilter: (tagFilter: string) => void;
  clearChat: () => void;
}

export const useAppStore = create<AppStore>((set) => ({
  activeConversationId: null,
  sources: [],
  citations: {},
  selectedSource: null,
  sidebarOpen: false,
  theme: getStoredTheme(),
  tagFilter: '',

  setActiveConversation: (id) =>
    set({ activeConversationId: id, sources: [], citations: {}, selectedSource: null }),

  setSources: (sources, citations) => set({ sources, citations }),

  setSelectedSource: (source) => set({ selectedSource: source }),

  setSidebarOpen: (sidebarOpen) => set({ sidebarOpen }),

  setTagFilter: (tagFilter) => set({ tagFilter }),

  setTheme: (theme) => {
    applyTheme(theme);
    set({ theme });
  },

  clearChat: () =>
    set({ activeConversationId: null, sources: [], citations: {}, selectedSource: null }),
}));
