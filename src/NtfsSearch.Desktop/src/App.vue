<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import IndexStatusPanel from './IndexStatus.vue'
import SearchResults from './SearchResults.vue'
import SpaceAnalysis from './SpaceAnalysis.vue'
import { applyFileTypeIcons, resetFileTypeIconRequest } from './file-type-icons'
import {
  formatCount, listenToBackend, sendRequest, startBackend,
  type DesktopMessage, type DesktopRequest, type FileAction, type IndexStatus, type SearchResult, type SearchRow, type SortField,
  type SpaceAnalysisResult,
} from './desktop'

const queryInput = ref<HTMLInputElement>()
const resultsTable = ref<InstanceType<typeof SearchResults>>()
const text = ref('')
const matchPath = ref('path')
const sort = ref<SortField>(0)
const descending = ref(false)
const selectedKey = ref<string | null>(null)
const result = shallowRef<SearchResult>()
const indexStatus = shallowRef<IndexStatus>()
const pending = ref(false)
const connected = ref(false)
const fatal = ref('')
const queryError = ref('')
const notice = ref('')
const actionError = ref('')
const saving = shallowRef<{ written: number; total: number }>()
const preferencePending = ref(false)
const showingSpace = ref(false)
const space = shallowRef<SpaceAnalysisResult>()
const spaceRoot = ref('')
const spacePending = ref(false)
const spaceError = ref('')
const spacePathPending = ref(false)
const spacePathError = ref('')
let requestId = Date.now()
let spaceRequestId = requestId
let spaceDetailId = 0
let unlisten: (() => void) | undefined
let noticeTimer: ReturnType<typeof setTimeout> | undefined

const hasAvailableIndex = computed(() => (result.value?.volumesReady ?? 0) > 0)
const page = computed(() => result.value ? Math.floor(result.value.offset / result.value.pageSize) + 1 : 1)
const pages = computed(() => result.value ? Math.max(1, Math.ceil(result.value.total / result.value.pageSize)) : 1)
const canPage = computed(() => connected.value && !pending.value && !queryError.value)
const emptyMessage = computed(() => {
  if (fatal.value) return ''
  if (queryError.value) return queryError.value
  if (pending.value || !result.value) return '正在搜索…'
  if (hasAvailableIndex.value) return '没有匹配的文件或文件夹'
  if (indexStatus.value?.error || indexStatus.value?.volumes.some(volume => volume.hasError))
    return '暂无可搜索的索引，请查看底栏的索引状态'
  if (indexStatus.value?.collectionCancelled) return '采集已取消，可从索引状态重新扫描'
  if (indexStatus.value?.discoveryComplete && !indexStatus.value.volumes.length) return '未发现可用的 NTFS 磁盘'
  return '正在加载磁盘索引，完成后会自动显示结果'
})
const resultLabel = computed(() => {
  if (pending.value) return '正在搜索…'
  if (hasAvailableIndex.value && result.value) return `${formatCount(result.value.total)} 项结果`
  return '等待索引'
})

function showFeedback(message: string, error = false) {
  clearTimeout(noticeTimer)
  actionError.value = error ? message : ''
  notice.value = error ? '' : message
  if (!error) noticeTimer = setTimeout(() => { notice.value = '' }, 1800)
}

function receive(message: DesktopMessage) {
  switch (message.type) {
    case 'result':
      if (message.requestId !== requestId) return
      result.value = message
      pending.value = false
      queryError.value = ''
      if (!message.rows.some(row => row.key === selectedKey.value)) selectedKey.value = null
      break
    case 'status':
      indexStatus.value = message
      break
    case 'spaceAnalysis':
      if (!showingSpace.value || message.requestId !== spaceRequestId) return
      if (message.detailId < spaceDetailId && space.value?.requestId === message.requestId
          && space.value.volumeKey === message.volumeKey && space.value.folder?.path === message.folder?.path)
        space.value = { ...message, details: space.value.details }
      else space.value = message
      spaceRoot.value = message.root ?? ''
      spaceError.value = message.error ?? ''
      spacePending.value = false
      spacePathPending.value = false
      break
    case 'spacePathError':
      if (!showingSpace.value || message.requestId !== spaceRequestId) return
      spacePathError.value = message.message
      spacePathPending.value = false
      break
    case 'error':
      if (message.scope === 'fatal') {
        resetFileTypeIconRequest()
        saving.value = undefined
        preferencePending.value = false
        connected.value = false
        pending.value = false
        fatal.value = message.message
      } else if (message.scope === 'query' && message.requestId === requestId) {
        pending.value = false
        queryError.value = message.message
        result.value = undefined
      } else if (message.scope !== 'query') {
        showFeedback(message.message, true)
      }
      break
    case 'notice':
      showFeedback(message.message)
      break
    case 'fileTypeIcons':
      applyFileTypeIcons(message.icons)
      break
    case 'saving':
      saving.value = message
      break
    case 'exitSavePreference':
      if (indexStatus.value) indexStatus.value = { ...indexStatus.value, skipExitIndexSave: message.skipExitIndexSave }
      preferencePending.value = false
      if (message.error) showFeedback(message.error, true)
      break
    case 'quit-complete':
      if (message.error) {
        saving.value = undefined
        connected.value = false
        showFeedback(message.error + '。可再次关闭窗口退出。', true)
      }
      break
  }
}

async function connect() {
  fatal.value = ''
  pending.value = true
  try {
    await startBackend()
    if (fatal.value) return
    connected.value = true
    query()
  } catch (error) {
    fatal.value ||= String(error)
    connected.value = false
    pending.value = false
  }
}

function query(offset = 0) {
  if (!connected.value || saving.value) return
  pending.value = true
  queryError.value = ''
  selectedKey.value = null
  resultsTable.value?.scrollToStart()
  const id = ++requestId
  void sendRequest({
    type: 'query', requestId: id, text: text.value, sort: sort.value,
    descending: descending.value, matchPath: matchPath.value === 'path', offset,
  }).catch(error => {
    if (requestId !== id) return
    connected.value = false
    pending.value = false
    fatal.value ||= String(error)
  })
}

watch([text, matchPath, sort, descending], () => query())

function sortBy(field: SortField) {
  if (sort.value === field) {
    descending.value = !descending.value
  } else {
    sort.value = field
    descending.value = false
  }
}

function turnPage(direction: number) {
  const current = result.value
  if (!canPage.value || !current) return
  const offset = current.offset + direction * current.pageSize
  if (offset >= 0 && offset < current.total) query(offset)
}

function focusQuery() {
  queryInput.value?.focus()
  queryInput.value?.select()
}

function clearQuery() {
  text.value = ''
  queryInput.value?.focus()
}

function handleShortcut(event: KeyboardEvent) {
  if (event.ctrlKey && event.key.toLowerCase() === 'f') {
    event.preventDefault()
    if (showingSpace.value) closeSpace()
    else focusQuery()
  }
}

function viewSpace(root = '', directoryId = '', path?: string) {
  if (!connected.value || saving.value) return
  const byPath = path !== undefined
  showingSpace.value = true
  spaceRoot.value = byPath ? space.value?.root ?? '' : root
  spacePending.value = !byPath
  spaceError.value = ''
  spacePathPending.value = byPath
  spacePathError.value = ''
  const id = ++spaceRequestId
  spaceDetailId = 0
  const displayed = space.value
  void sendRequest(byPath ? {
    type: 'spacePath', requestId: id, path, root: displayed?.root ?? '',
    breadcrumbIds: displayed?.folder?.breadcrumbs.map(crumb => crumb.id) ?? [], volumeKey: displayed?.volumeKey ?? null,
  }
    : { type: 'spaceAnalysis', requestId: id, root, directoryId }).catch(error => {
    if (id !== spaceRequestId) return
    spacePending.value = false
    spacePathPending.value = false
    if (byPath) spacePathError.value = String(error)
    else spaceError.value = String(error)
  })
}

function closeSpace() {
  showingSpace.value = false
  space.value = undefined
  if (connected.value) sendAction({ type: 'closeSpaceAnalysis', requestId: ++spaceRequestId })
  void nextTick(focusQuery)
}

function requestSpaceDetails(id: number, directoryIds: string[], manualDirectoryIds: string[], branchIds: string[]) {
  if (!showingSpace.value || !connected.value || id !== spaceRequestId) return
  void sendRequest({ type: 'spaceDetails', requestId: id, detailId: ++spaceDetailId, directoryIds, manualDirectoryIds, branchIds })
    .catch(error => { if (id === spaceRequestId) showFeedback(String(error), true) })
}

function sendAction(request: DesktopRequest) {
  if (!connected.value || saving.value) return
  showFeedback('')
  void sendRequest(request).catch(error => showFeedback(String(error), true))
}

function updateSavePreference(skipExitIndexSave: boolean) {
  if (!connected.value || saving.value || preferencePending.value) return
  showFeedback('')
  preferencePending.value = true
  void sendRequest({ type: 'setSkipExitIndexSave', skipExitIndexSave }).catch(error => {
    preferencePending.value = false
    showFeedback(String(error), true)
  })
}

function fileAction(type: FileAction, row: SearchRow) {
  if (!pending.value) sendAction({ type, key: row.key })
}

onMounted(async () => {
  await nextTick()
  queryInput.value?.focus()
  try {
    unlisten = await listenToBackend(receive)
    await connect()
  } catch (error) {
    fatal.value = String(error)
  }
})

onBeforeUnmount(() => {
  unlisten?.()
  clearTimeout(noticeTimer)
})
</script>

<template>
  <main class="workspace" @keydown="handleShortcut" @contextmenu.prevent>
    <header v-show="!showingSpace" class="search-toolbar">
      <div class="search-field">
        <span class="search-glyph" aria-hidden="true"></span>
        <input ref="queryInput" v-model="text" type="text" aria-label="搜索文件和文件夹" placeholder="搜索文件和文件夹"
          autocomplete="off" spellcheck="false" title="空格分隔搜索条件，双引号匹配含空格的连续文字"
          @keydown.down.prevent="resultsTable?.focusSelection()">
        <button v-if="text" type="button" class="clear-search" title="清空搜索" aria-label="清空搜索" @click="clearQuery">×</button>
      </div>
      <select v-model="matchPath" class="scope-select" aria-label="匹配范围" title="匹配范围">
        <option value="path">完整路径</option>
        <option value="name">仅名称</option>
      </select>
      <button type="button" class="outlined-button space-open" aria-label="空间分析"
        :disabled="!connected || !!saving" @click="viewSpace()">空间分析</button>
    </header>

    <SearchResults v-show="!showingSpace" ref="resultsTable" v-model:selected-key="selectedKey" :rows="fatal ? [] : result?.rows ?? []"
      :sort="sort" :descending="descending" :pending="pending" :empty-message="emptyMessage"
      @sort="sortBy" @action="fileAction">
      <div v-if="fatal" class="startup-error" role="alert">
        <strong>搜索服务异常</strong>
        <span>{{ fatal }}</span>
        <button type="button" class="outlined-button" @click="connect">重试</button>
      </div>
    </SearchResults>

    <footer v-show="!showingSpace" class="status-bar">
      <div class="result-summary" aria-live="polite" aria-atomic="true">
        <span class="result-count">{{ resultLabel }}</span>
        <span v-if="hasAvailableIndex && result?.userMilliseconds != null" class="query-time" title="用户查询耗时">
          {{ result.userMilliseconds.toFixed(2) }} ms
        </span>
      </div>
      <div class="page-controls" aria-label="分页">
        <button type="button" title="上一页" aria-label="上一页" :disabled="!canPage || page <= 1" @click="turnPage(-1)">
          <span class="chevron left" aria-hidden="true"></span>
        </button>
        <span class="page-number" aria-label="当前页和总页数">{{ formatCount(page) }} / {{ formatCount(pages) }}</span>
        <button type="button" title="下一页" aria-label="下一页" :disabled="!canPage || page >= pages" @click="turnPage(1)">
          <span class="chevron right" aria-hidden="true"></span>
        </button>
      </div>
      <IndexStatusPanel :status="indexStatus" :connected="connected" :preference-pending="preferencePending"
        @save-preference="updateSavePreference" @action="sendAction({ type: $event })" />
    </footer>
    <SpaceAnalysis v-if="showingSpace" :result="space" :root="spaceRoot" :pending="spacePending && !fatal"
      :error="fatal || spaceError" :path-pending="spacePathPending && !fatal" :path-error="spacePathError"
      :index-status="indexStatus" @view="viewSpace" @path="viewSpace(spaceRoot, '', $event)" @close="closeSpace"
      @details="requestSpaceDetails"
      @reveal="sendAction({ type: 'reveal', key: $event })" />
    <div v-if="notice || actionError" class="operation-feedback" :class="{ error: actionError }" :role="actionError ? 'alert' : 'status'">
      <span>{{ actionError || notice }}</span>
      <button v-if="actionError" type="button" aria-label="关闭提示" @click="actionError = ''">×</button>
    </div>
    <div v-if="saving" class="save-overlay" role="status" aria-live="polite">
      <div>
        <strong>正在保存退出快照</strong>
        <progress v-if="saving.total" :value="saving.written" :max="saving.total"
          :aria-label="'已保存 ' + saving.written + ' 个卷，共 ' + saving.total + ' 个卷'"></progress>
      </div>
    </div>
  </main>
</template>

<style scoped>
.space-open { height: 39px; white-space: nowrap; }

.save-overlay {
  position: fixed;
  inset: 0;
  z-index: 8;
  display: grid;
  place-items: center;
  background: #19253830;
}
.save-overlay > div {
  display: grid;
  gap: 12px;
  min-width: 250px;
  padding: 18px 22px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--surface);
}
.save-overlay progress { width: 100%; accent-color: var(--accent); }
</style>
