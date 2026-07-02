import { create } from 'zustand';
import type { Source } from './types';

// Citations map: sourceIndex → sentences from the answer that cited that source.
// Used for keyword highlighting in the chunk modal.
export type Citations = Record<number, string[]>;

interface AppStore {
  activeConversationId: string | null;
  sources: Source[];
  citations: Citations;
  selectedSource: Source | null;

  setActiveConversation: (id: string | null) => void;
  setSources: (sources: Source[], citations: Citations) => void;
  setSelectedSource: (source: Source | null) => void;
  clearChat: () => void;
}

export const useAppStore = create<AppStore>((set) => ({
  activeConversationId: null,
  sources: [],
  citations: {},
  selectedSource: null,

  setActiveConversation: (id) =>
    set({ activeConversationId: id, sources: [], citations: {}, selectedSource: null }),

  setSources: (sources, citations) => set({ sources, citations }),

  setSelectedSource: (source) => set({ selectedSource: source }),

  clearChat: () =>
    set({ activeConversationId: null, sources: [], citations: {}, selectedSource: null }),
}));