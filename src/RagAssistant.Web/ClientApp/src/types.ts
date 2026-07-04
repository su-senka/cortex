export interface User {
  name: string;
  sub: string;
  isAdmin: boolean;
  appName: string; // configurable via App:Name in appsettings.json
}

export interface Conversation {
  id: string;
  title: string;
  createdAt: string;
}

export interface ApiMessage {
  role: string;
  content: string;
  createdAt: string;
}

export interface Source {
  sourceIndex: number;
  title: string;
  sourceFile: string;
  sectionHeading?: string;
  score: number;
  chunkText?: string;
}

// Local chat message (includes UI state not present in the API type)
export interface ChatMessage {
  id?: string;           // set after backend persists the message
  role: 'user' | 'assistant';
  content: string;
  isStreaming?: boolean;
  feedback?: 1 | -1;
}