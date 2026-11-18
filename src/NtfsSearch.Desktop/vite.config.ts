import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  clearScreen: false,
  // Windows 从 WSL 映射盘构建时，保留盘符，避免解析成 Rollup 无法读取的 UNC 路径。
  resolve: { preserveSymlinks: true },
  build: { rollupOptions: { preserveSymlinks: true } },
})
