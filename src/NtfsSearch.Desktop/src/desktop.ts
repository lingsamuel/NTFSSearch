import { invoke } from '@tauri-apps/api/core'
import { listen } from '@tauri-apps/api/event'

export type SortField = 0 | 1 | 2 | 3
export type FileAction = 'open' | 'reveal' | 'copy'

export interface SearchRow {
  key: string
  name: string
  fullPath: string
  isDirectory: boolean
  sizeText: string
  sizeBytesText: string | null
  modifiedText: string
}

export interface SearchResult {
  type: 'result'
  requestId: number
  userInitiated: boolean
  total: number
  offset: number
  pageSize: number
  userMilliseconds: number | null
  volumesReady: number
  rows: SearchRow[]
}

export interface IndexStatus {
  type: 'status'
  volumes: { root: string; message: string; hasError: boolean }[]
  indexed: number
  volumesReady: number
  active: number
  scanLimit: number | null
  refreshing: boolean
  discoveryComplete: boolean
  collectionCancelled: boolean
  progress: string | null
  error: string | null
}

export interface SpaceEntry {
  directoryId: string | null
  name: string
  count: number
  key: string | null
  bytes: string
  sizeText: string
  sizeBytesText: string
}

export interface SpaceAnalysisResult {
  type: 'spaceAnalysis'
  requestId: number
  roots: string[]
  root: string | null
  volumeKey: string | null
  error: string | null
  folder: {
    path: string
    breadcrumbs: { id: string; name: string }[]
    bytes: string
    sizeText: string
    sizeBytesText: string
    unknownFiles: number
    isEmpty: boolean
    children: SpaceEntry[]
  } | null
}

export type DesktopMessage =
  | SearchResult
  | IndexStatus
  | SpaceAnalysisResult
  | { type: 'spacePathError'; requestId: number; message: string }
  | { type: 'fileTypeIcons'; icons: Record<string, string | null> }
  | { type: 'saving'; written: number; total: number }
  | { type: 'quit-complete'; error?: string }
  | { type: 'error'; scope: 'fatal' | 'query' | 'refresh' | 'action'; message: string; requestId?: number }
  | { type: 'notice'; message: string }

export type DesktopRequest =
  | { type: 'query'; requestId: number; text: string; sort: SortField; descending: boolean; matchPath: boolean; offset: number }
  | { type: 'spaceAnalysis'; requestId: number; root: string; directoryId: string }
  | { type: 'spacePath'; requestId: number; path: string; root: string; breadcrumbIds: string[]; volumeKey: string | null }
  | { type: 'closeSpaceAnalysis'; requestId: number }
  | { type: 'rescan' | 'cancel' }
  | { type: 'fileTypeIcons'; extensions: string[] }
  | { type: FileAction; key: string }

export function listenToBackend(callback: (message: DesktopMessage) => void) {
  return listen<DesktopMessage>('backend-message', event => callback(event.payload))
}

export function startBackend() {
  return invoke('start_backend')
}

export function sendRequest(request: DesktopRequest) {
  return invoke('send_request', { request })
}

export const formatCount = new Intl.NumberFormat('zh-CN').format
