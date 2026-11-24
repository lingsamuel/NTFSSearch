import { invoke, isTauri } from '@tauri-apps/api/core'
import { createApp } from 'vue'
import App from './App.vue'
import { setLanguage } from './localization'
import './styles.css'

async function mount() {
  if (isTauri()) {
    // 首次渲染沿用桌面壳的语言。调用失败时继续使用浏览器检测结果。
    const language = await invoke<string>('desktop_language').catch(() => undefined)
    if (language) setLanguage(language)
  }
  createApp(App).mount('#app')
}

void mount()
