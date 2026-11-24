import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const directory = process.argv[2] ?? fileURLToPath(new URL('../locales/', import.meta.url))
const baseLanguage = 'en'

function validateUniqueKeys(source) {
  // JSON.parse 会保留同名字段的最后一个值，因此在已确认语法有效后检查每个对象的字段。
  const objects = []
  for (const match of source.matchAll(/"(?:[^"\\]|\\[\s\S])*"|[{}]/g)) {
    const token = match[0]
    if (token === '{') objects.push(new Set())
    else if (token === '}') objects.pop()
    else if (/^\s*:/.test(source.slice(match.index + token.length))) {
      const key = JSON.parse(token)
      const keys = objects.at(-1)
      if (keys.has(key)) throw new Error(`重复字段 ${key}`)
      keys.add(key)
    }
  }
}

function placeholders(message) {
  return [...new Set(Array.from(message.matchAll(/\{([a-zA-Z][a-zA-Z0-9_]*)\}/g), match => match[1]))].sort().join(', ')
}

try {
  const languages = new Map()
  for (const file of readdirSync(directory).filter(name => name.endsWith('.json')).sort()) {
    try {
      const tag = Intl.getCanonicalLocales(file.slice(0, -5))[0].toLowerCase()
      if (languages.has(tag)) throw new Error(`重复语言 ${tag}`)
      const source = readFileSync(join(directory, file), 'utf8')
      const data = JSON.parse(source)
      validateUniqueKeys(source)
      if (typeof data?.name !== 'string' || !data.name.trim()) throw new Error('缺少语言名称 name')
      const messages = data.messages
      if (!messages || typeof messages !== 'object' || Array.isArray(messages)) throw new Error('缺少译文对象 messages')
      for (const [key, value] of Object.entries(messages)) {
        if (typeof value !== 'string' || !value.trim()) throw new Error(`${key} 的译文须为非空文本`)
      }
      languages.set(tag, { file, messages })
    } catch (error) {
      throw new Error(`${file}：${error.message}`)
    }
  }
  const english = languages.get(baseLanguage)?.messages
  const chinese = languages.get('zh-cn')?.messages
  if (!english || !chinese) throw new Error('缺少内置 en.json 或 zh-CN.json')
  for (const key of Object.keys(english)) {
    if (!Object.hasOwn(chinese, key)) throw new Error(`zh-CN.json 缺少内置译文 ${key}`)
  }
  for (const [tag, { file, messages }] of languages) {
    if (tag === baseLanguage) continue
    for (const [key, message] of Object.entries(messages)) {
      if (!Object.hasOwn(english, key)) throw new Error(`${file}：${key} 在 en.json 中找不到`)
      if (placeholders(message) !== placeholders(english[key]))
        throw new Error(`${file}：${key} 需要占位符 ${placeholders(english[key]) || '（无）'}`)
    }
  }
  console.log(`已检查 ${languages.size} 种语言和 ${Object.keys(english).length} 条内置译文。`)
} catch (error) {
  console.error(error.message)
  process.exitCode = 1
}
