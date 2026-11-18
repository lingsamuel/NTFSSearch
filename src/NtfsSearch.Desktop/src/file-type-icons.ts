import { shallowReactive } from 'vue'
import { sendRequest, type SearchRow } from './desktop'

const batchSize = 16
const cacheSize = 512
const icons = shallowReactive(new Map<string, string | null>())
let visibleExtensions = new Set<string>()
let pending: string[] | undefined

export function fileExtension(row: SearchRow) {
  if (row.isDirectory) return
  const dot = row.name.lastIndexOf('.')
  if (dot >= 0 && dot < row.name.length - 1) return row.name.slice(dot).toLowerCase()
}

export function fileTypeIcon(extension: string | undefined) {
  return extension ? icons.get(extension) : undefined
}

export function setVisibleFileTypes(extensions: Set<string>) {
  visibleExtensions = extensions
  requestVisibleIcons()
}

export function resetFileTypeIconRequest() {
  pending = undefined
}

function requestVisibleIcons() {
  if (pending) return
  const batch: string[] = []
  for (const extension of visibleExtensions) {
    if (icons.has(extension)) continue
    batch.push(extension)
    if (batch.length === batchSize) break
  }
  if (!batch.length) return

  // 一次只发送一批。滚动时后续批次直接取最新视口，不积压已滚过的文件类型。
  pending = batch
  void sendRequest({ type: 'fileTypeIcons', extensions: batch }).catch(() => {
    if (pending === batch) pending = undefined
  })
}

export function applyFileTypeIcons(result: Record<string, string | null>) {
  if (!pending) return
  for (const extension of pending) icons.set(extension, result[extension] ?? null)
  pending = undefined
  for (const extension of icons.keys()) {
    if (icons.size <= cacheSize) break
    if (!visibleExtensions.has(extension)) icons.delete(extension)
  }
  requestVisibleIcons()
}
