/// <reference types="vite/client" />
import { computed, ref } from 'vue'

interface LanguageCatalog {
  name: string
  messages: Record<string, string>
}

const catalogs = import.meta.glob<LanguageCatalog>('../../../locales/*.json', { eager: true, import: 'default' })
const languagesByCode = new Map(Object.entries(catalogs).map(([path, catalog]) => [
  path.slice(path.lastIndexOf('/') + 1, -'.json'.length), catalog,
]))
const fallbackLanguage = 'en'

export const availableLanguages = Array.from(languagesByCode, ([code, catalog]) => ({ code, name: catalog.name }))

function detectLanguage(preferences: readonly string[]) {
  for (const preference of preferences) {
    const segments = preference.toLowerCase().split('-')
    const base = segments[0]
    while (segments.length) {
      const tag = segments.join('-')
      const exact = availableLanguages.find(({ code }) => code.toLowerCase() === tag)
      if (exact) return exact.code
      segments.pop()
    }
    const match = (base === 'zh' ? availableLanguages.find(({ code }) => code === 'zh-CN') : undefined)
      ?? availableLanguages.find(({ code }) => code.toLowerCase().startsWith(base + '-'))
    if (match) return match.code
  }
  return fallbackLanguage
}

const language = ref(fallbackLanguage)

export function setLanguage(code: string) {
  language.value = languagesByCode.has(code) ? code : fallbackLanguage
  document.documentElement.lang = language.value
}

// 启动时先按浏览器语言显示，收到设置状态后使用后端解析的语言。
setLanguage(detectLanguage(navigator.languages))

export function t(key: string, parameters: Record<string, string | number> = {}) {
  const message = languagesByCode.get(language.value)?.messages[key]
    ?? languagesByCode.get(fallbackLanguage)?.messages[key]
    ?? key
  return message.replace(/\{([a-zA-Z][a-zA-Z0-9_]*)\}/g, (placeholder, name: string) =>
    Object.hasOwn(parameters, name) ? String(parameters[name]) : placeholder)
}

const count = computed(() => new Intl.NumberFormat(language.value))
const percent = computed(() => new Intl.NumberFormat(language.value, { style: 'percent', maximumFractionDigits: 2 }))
const milliseconds = computed(() => new Intl.NumberFormat(language.value, { minimumFractionDigits: 2, maximumFractionDigits: 2 }))

export function formatCount(value: number) { return count.value.format(value) }
export function formatPercent(value: number) { return percent.value.format(value) }
export function formatMilliseconds(value: number) { return milliseconds.value.format(value) }
