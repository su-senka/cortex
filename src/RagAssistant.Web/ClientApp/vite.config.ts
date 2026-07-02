import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// In development run `npm run dev` (port 5173) — API calls are proxied to the
// ASP.NET backend on 5141. In production `npm run build` outputs to ../wwwroot
// and ASP.NET serves everything as static files.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': 'http://localhost:5141',
      '/auth': 'http://localhost:5141',
      '/signin-oidc': { target: 'http://localhost:5141', changeOrigin: true },
      '/signout-callback-oidc': { target: 'http://localhost:5141', changeOrigin: true },
    },
  },
});