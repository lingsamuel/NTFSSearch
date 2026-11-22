import { invoke } from '@tauri-apps/api/core'
import { listen } from '@tauri-apps/api/event'

export type SortField = 0 | 1 | 2 | 3
export type FileAction = 'open' | 'openWith' | 'reveal' | 'copy'

export interface SearchRow {
  key: string
  name: string
  fullPath: string
  matchRanges: number[]
  isDirectory: boolean
  sizeText: string
  sizeBytesText: string | null
  sizeHint: string | null
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
  directorySortPending: boolean
  directorySortFailed: boolean
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
  skipExitIndexSave: boolean
  indexModifiedTime: boolean
  indexFileSize: boolean
  progress: string | null
  error: string | null
}

export interface SpaceExclusion {
  volumeKey: string
  path: string
  key: string
}

export interface SpaceEntry {
  directoryId: string | null
  fileReference: string | null
  name: string
  count: number
  key: string | null
  bytes: string
  sizeText: string
}

export interface SpaceAnalysisResult {
  type: 'spaceAnalysis'
  requestId: number
  roots: string[]
  root: string | null
  volumeKey: string | null
  error: string | null
  doubleClickMilliseconds: number
  doubleClickWidth: number
  doubleClickHeight: number
  detailId: number
  details: Record<string, SpaceEntry[] | null>
  flashes: string[]
  exclusionRevision: number
  exclusions: SpaceExclusion[]
  folder: {
    path: string
    breadcrumbs: { id: string; name: string }[]
    bytes: string
    sizeText: string
    unknownFiles: number
    isEmpty: boolean
    children: SpaceEntry[]
  } | null
}

interface IndexSettings {
  precomputeSort?: boolean
  indexCreationTime?: boolean
  indexModifiedTime?: boolean
  indexFileSize?: boolean
}

export interface SettingsStatus extends IndexSettings {
  type: 'settingsStatus'
  searchDirectorySizes: boolean
  skipExitIndexSave: boolean
  indexRestartRequired: boolean
  searchServiceAvailable: boolean
  enabled: boolean
  port: number
  address: string | null
  portOverridden: boolean
  running: boolean
  busy: boolean
  error: string | null
}

export type SettingsRequest =
  | { type: 'getSettings' | 'restartSearchService' }
  | ({ type: 'applySettings'; enabled?: boolean; port?: number; searchDirectorySizes: boolean; skipExitIndexSave: boolean } & IndexSettings)

export type DesktopMessage =
  | SearchResult
  | IndexStatus
  | SpaceAnalysisResult
  | SettingsStatus
  | { type: 'directorySizes'; requestId: number; rows: Pick<SearchRow, 'key' | 'sizeText' | 'sizeBytesText' | 'sizeHint'>[] }
  | { type: 'spacePathError'; requestId: number; message: string }
  | { type: 'fileTypeIcons'; icons: Record<string, string | null> }
  | { type: 'saving'; written: number; total: number; phase?: 'writing' | 'skipping' | 'committing' }
  | { type: 'exitSavePreference'; skipExitIndexSave: boolean; error: string | null }
  | { type: 'quit-complete'; error?: string }
  | { type: 'error'; scope: 'fatal' | 'query' | 'refresh' | 'action'; message: string; requestId?: number }
  | { type: 'notice'; message: string }

export type DesktopRequest =
  | SettingsRequest
  | { type: 'query'; requestId: number; text: string; sort: SortField; descending: boolean; matchPath: boolean; offset: number }
  | { type: 'spaceAnalysis'; requestId: number; root: string; directoryId: string }
  | { type: 'spacePath'; requestId: number; path: string; root: string; breadcrumbIds: string[]; volumeKey: string | null }
  | { type: 'spaceDetails'; requestId: number; detailId: number; directoryIds: string[]; manualDirectoryIds: string[]; branchIds: string[] }
  | { type: 'spaceExclude'; requestId: number; volumeKey: string; key: string; fileReference: string | null }
  | { type: 'spaceRestore'; requestId: number; volumeKey?: string; key?: string }
  | { type: 'closeSpaceAnalysis'; requestId: number }
  | { type: 'rescan' | 'cancel' | 'skipExitSave' }
  | { type: 'setSkipExitIndexSave'; skipExitIndexSave: boolean }
  | { type: 'fileTypeIcons'; extensions: string[] }
  | { type: FileAction; key: string }

export function listenToBackend(callback: (message: DesktopMessage) => void) {
  return listen<DesktopMessage>('backend-message', event => callback(event.payload))
}

export function listenToOpenSettings(callback: () => void) {
  return listen('open-settings', callback)
}

export function startBackend() {
  return invoke('start_backend')
}

export function sendRequest(request: DesktopRequest) {
  return invoke('send_request', { request })
}

export const formatCount = new Intl.NumberFormat('zh-CN').format
