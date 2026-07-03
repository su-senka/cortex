import { useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from './api';
import { useAppStore } from './store';
import { watchSystemTheme } from './theme';
import { Header } from './components/Header';
import { Sidebar } from './components/Sidebar';
import { ChatPane } from './components/ChatPane';
import { SourcesPane } from './components/SourcesPane';

export default function App() {
  const { data: user } = useQuery({ queryKey: ['me'], queryFn: api.me });

  useEffect(() => watchSystemTheme(() => useAppStore.getState().theme), []);

  return (
    <div className="flex flex-col h-dvh bg-gray-100 text-gray-900 dark:bg-gray-950 dark:text-gray-100">
      <Header user={user} />
      <div className="flex flex-1 overflow-hidden">
        <Sidebar />
        <ChatPane />
        <SourcesPane />
      </div>
    </div>
  );
}
