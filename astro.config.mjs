import { defineConfig } from 'astro/config';

// Static output: Azure Static Web Apps serves the built `dist/` directory.
export default defineConfig({
  output: 'static',
  build: {
    format: 'directory'
  },
  trailingSlash: 'ignore'
});
