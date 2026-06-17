import { defineConfig } from 'vite'

// On GitHub Pages a project site is served from /<repo>/, so Vite needs that
// base path. Locally (dev / PAGES_BASE_PATH unset) it falls back to '/'.
function normalizeBasePath(basePath?: string) {
  if (!basePath || basePath === '/') {
    return '/'
  }
  const withLeadingSlash = basePath.startsWith('/') ? basePath : `/${basePath}`
  return withLeadingSlash.endsWith('/') ? withLeadingSlash : `${withLeadingSlash}/`
}

const repositoryName = process.env.GITHUB_REPOSITORY?.split('/')[1]
const base = normalizeBasePath(process.env.PAGES_BASE_PATH ?? repositoryName)

export default defineConfig({
  base,
  clearScreen: false,
  server: {
    watch: {
      ignored: ['**/*.fs'] // Don't watch F# sources; Fable handles them
    }
  }
})
