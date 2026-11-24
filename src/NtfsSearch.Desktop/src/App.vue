<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import IndexStatusPanel from './IndexStatus.vue'
import SearchResults from './SearchResults.vue'
import Settings from './Settings.vue'
import SpaceAnalysis from './SpaceAnalysis.vue'
import { applyFileTypeIcons, resetFileTypeIconRequest } from './file-type-icons'
import {
  dragFiles, formatCount, listenToBackend, listenToOpenSettings, listenToSearchActivation, sendRequest, startBackend,
  type DesktopMessage, type DesktopRequest, type FileAction, type IndexStatus, type SearchResult, type SearchRow, type SortField,
  type SettingsRequest, type SettingsStatus, type SpaceAnalysisResult, type SpaceEntry, type SpaceExclusion,
} from './desktop'

const queryInput = ref<HTMLInputElement>()
const resultsTable = ref<InstanceType<typeof SearchResults>>()
const text = ref('')
const matchPath = ref('path')
const sort = ref<SortField>(0)
const descending = ref(false)
const result = shallowRef<SearchResult>()
const indexStatus = shallowRef<IndexStatus>()
const pending = ref(false)
const connected = ref(false)
const fatal = ref('')
const queryError = ref('')
const notice = ref('')
const actionError = ref('')
const saving = shallowRef<Extract<DesktopMessage, { type: 'saving' }>>()
const skipSaveButton = ref<HTMLButtonElement>()
const skipSavePending = ref(false)
const preferencePending = ref(false)
const showingSpace = ref(false)
const spaceStarted = ref(false)
const space = shallowRef<SpaceAnalysisResult>()
const spaceRoot = ref('')
const spacePending = ref(false)
const spaceError = ref('')
const spacePathPending = ref(false)
const spacePathError = ref('')
const showingSettings = ref(false)
const keepSettingsDraft = ref(false)
const settingsPanel = ref<InstanceType<typeof Settings>>()
const settingsStatus = shallowRef<SettingsStatus>()
const settingsPending = ref(false)
const settingsError = ref('')
let requestId = Date.now()
let spaceRequestId = requestId
let spaceDetailId = 0
let unlisten: (() => void) | undefined
let unlistenOpenSettings: (() => void) | undefined
let unlistenActivation: (() => void) | undefined
let focusBeforeSettings: HTMLElement | undefined
let noticeTimer: ReturnType<typeof setTimeout> | undefined

const hasAvailableIndex = computed(() => (result.value?.volumesReady ?? 0) > 0)
const indexFileSize = computed(() => indexStatus.value?.indexFileSize ?? true)
const indexModifiedTime = computed(() => indexStatus.value?.indexModifiedTime ?? true)
const canViewSpace = computed(() => connected.value && !!indexStatus.value && indexFileSize.value && !saving.value)
const savePhase = computed(() => {
  const phase = saving.value?.phase ?? 'writing'
  return phase === 'writing' && skipSavePending.value ? 'skipping' : phase
})
const saveLabels = {
  writing: '正在保存退出快照',
  skipping: '正在跳过本次保存…',
  committing: '正在完成保存…',
}
const spaceUnavailable = '空间分析需要启用「索引文件大小」。'
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
      break
    case 'directorySizes': {
      const current = result.value
      if (message.requestId !== requestId || current?.requestId !== message.requestId) return
      const changed = new Map(message.rows.map(row => [row.key, row]))
      result.value = { ...current, rows: current.rows.map(row => {
        const size = changed.get(row.key)
        return size ? { ...row, ...size } : row
      }) }
      break
    }
    case 'status':
      indexStatus.value = message
      if ((sort.value === 2 && !indexFileSize.value) || (sort.value === 3 && !indexModifiedTime.value)) {
        sort.value = 0
        descending.value = false
      }
      if (spaceStarted.value && !indexFileSize.value) resetSpace()
      break
    case 'settingsStatus':
      settingsStatus.value = message
      settingsPending.value = message.busy
      if (indexStatus.value) indexStatus.value = { ...indexStatus.value, skipExitIndexSave: message.skipExitIndexSave }
      break
    case 'spaceAnalysis':
      if (!spaceStarted.value || message.requestId !== spaceRequestId) return
      if (message.detailId < spaceDetailId && space.value?.requestId === message.requestId
          && space.value.volumeKey === message.volumeKey && space.value.folder?.path === message.folder?.path
          && space.value.exclusionRevision === message.exclusionRevision)
        space.value = { ...message, details: space.value.details }
      else space.value = message
      spaceRoot.value = message.root ?? ''
      spaceError.value = message.error ?? ''
      spacePending.value = false
      spacePathPending.value = false
      break
    case 'spacePathError':
      if (!spaceStarted.value || message.requestId !== spaceRequestId) return
      spacePathError.value = message.message
      spacePathPending.value = false
      break
    case 'error':
      if (message.scope === 'fatal') {
        resetFileTypeIconRequest()
        saving.value = undefined
        preferencePending.value = false
        settingsStatus.value = undefined
        settingsPending.value = false
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
      if (!saving.value) skipSavePending.value = false
      saving.value = message
      break
    case 'exitSavePreference':
      if (indexStatus.value) indexStatus.value = { ...indexStatus.value, skipExitIndexSave: message.skipExitIndexSave }
      if (settingsStatus.value) settingsStatus.value = { ...settingsStatus.value, skipExitIndexSave: message.skipExitIndexSave }
      preferencePending.value = false
      if (message.error) showFeedback(message.error, true)
      break
    case 'quit-complete':
      if (message.error) {
        saving.value = undefined
        connected.value = false
        showFeedback(message.error + '。可从托盘菜单再次选择「退出」。', true)
      }
      break
  }
}

async function connect() {
  if (spaceStarted.value) resetSpace()
  fatal.value = ''
  pending.value = true
  try {
    await startBackend()
    if (fatal.value) return
    connected.value = true
    query()
    if (showingSettings.value) requestSettings({ type: 'getSettings' })
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
  resultsTable.value?.clearSelection()
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
watch(skipSaveButton, button => {
  if (button && !button.disabled) button.focus()
}, { flush: 'post' })

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

function focusQuery(selectText = true) {
  queryInput.value?.focus()
  if (selectText) queryInput.value?.select()
}

function clearQuery() {
  text.value = ''
  queryInput.value?.focus()
}

function handleShortcut(event: KeyboardEvent) {
  if (showingSettings.value || saving.value) return
  if (event.ctrlKey && event.key.toLowerCase() === 'f') {
    event.preventDefault()
    if (showingSpace.value) closeSpace()
    else focusQuery()
  }
}

function openSettings() {
  if (showingSettings.value) return
  if (document.activeElement instanceof HTMLElement) focusBeforeSettings = document.activeElement
  showingSettings.value = true
  requestSettings({ type: 'getSettings' })
  void nextTick(() => settingsPanel.value?.focusBack())
}

function closeSettings() {
  showingSettings.value = false
  keepSettingsDraft.value = false
  const previous = focusBeforeSettings
  focusBeforeSettings = undefined
  void nextTick(() => {
    previous?.focus({ preventScroll: true })
    if (!showingSpace.value && document.activeElement === document.body) focusQuery()
  })
}

async function activateSearch() {
  if (saving.value || showingSpace.value) return
  if (showingSettings.value) {
    // 重新进入前台时保留设置组件，未应用的表单值仍可继续编辑。
    keepSettingsDraft.value = true
    showingSettings.value = false
    focusBeforeSettings = undefined
  }
  await nextTick()
  if (!saving.value && !showingSettings.value && !showingSpace.value) focusQuery(false)
}

function requestSettings(request: SettingsRequest) {
  if (!connected.value || saving.value || settingsPending.value
      || (preferencePending.value && request.type !== 'getSettings')) return
  settingsError.value = ''
  settingsPending.value = true
  void sendRequest(request).catch(error => {
    settingsPending.value = false
    settingsError.value = String(error)
  })
}

function openSpace() {
  if (!canViewSpace.value) return
  showingSpace.value = true
  if (spaceStarted.value) {
    sendAction({ type: 'spaceVisibility', enabled: true })
  } else {
    spaceStarted.value = true
    viewSpace()
  }
}

function viewSpace(root = '', directoryId = '', path?: string) {
  if (!canViewSpace.value) return
  const byPath = path !== undefined
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
  sendAction({ type: 'spaceVisibility', enabled: false })
  void nextTick(() => focusQuery())
}

function resetSpace() {
  if (showingSpace.value) void nextTick(() => focusQuery())
  showingSpace.value = spaceStarted.value = false
  space.value = undefined
  sendAction({ type: 'closeSpaceAnalysis', requestId: ++spaceRequestId })
}

function requestSpaceDetails(id: number, directoryIds: string[], manualDirectoryIds: string[], branchIds: string[]) {
  if (!spaceStarted.value || !connected.value || id !== spaceRequestId) return
  void sendRequest({ type: 'spaceDetails', requestId: id, detailId: ++spaceDetailId, directoryIds, manualDirectoryIds, branchIds })
    .catch(error => { if (id === spaceRequestId) showFeedback(String(error), true) })
}

function excludeSpaceItem(item: SpaceEntry) {
  const current = space.value
  if (!current?.volumeKey || current.requestId !== spaceRequestId || !item.key) return
  sendAction({ type: 'spaceExclude', requestId: current.requestId, volumeKey: current.volumeKey,
    key: item.key, fileReference: item.fileReference })
}

function restoreSpaceItems(item?: SpaceExclusion) {
  if (space.value?.requestId !== spaceRequestId) return
  sendAction({ type: 'spaceRestore', requestId: spaceRequestId, volumeKey: item?.volumeKey, key: item?.key })
}

function sendAction(request: DesktopRequest) {
  if (!connected.value || saving.value) return
  showFeedback('')
  void sendRequest(request).catch(error => showFeedback(String(error), true))
}

function updateSavePreference(skipExitIndexSave: boolean) {
  if (!connected.value || saving.value || preferencePending.value || settingsPending.value) return
  showFeedback('')
  preferencePending.value = true
  void sendRequest({ type: 'setSkipExitIndexSave', skipExitIndexSave }).catch(error => {
    preferencePending.value = false
    showFeedback(String(error), true)
  })
}

function skipExitSave() {
  if (!saving.value || savePhase.value !== 'writing') return
  showFeedback('')
  skipSavePending.value = true
  void sendRequest({ type: 'skipExitSave' }).catch(error => {
    skipSavePending.value = false
    showFeedback(String(error), true)
  })
}

function fileAction(type: FileAction, row: SearchRow) {
  if (!pending.value) sendAction({ type, key: row.key })
}

function startFileDrag(keys: string[]) {
  if (!connected.value || pending.value || saving.value || !keys.length) return
  showFeedback('')
  void dragFiles(keys).catch(error => showFeedback(String(error), true))
}

onMounted(async () => {
  await nextTick()
  queryInput.value?.focus()
  try {
    unlistenOpenSettings = await listenToOpenSettings(openSettings)
    unlistenActivation = await listenToSearchActivation(activateSearch)
    unlisten = await listenToBackend(receive)
    await connect()
  } catch (error) {
    fatal.value = String(error)
  }
})

onBeforeUnmount(() => {
  unlisten?.()
  unlistenOpenSettings?.()
  unlistenActivation?.()
  clearTimeout(noticeTimer)
})
</script>

<template>
  <main class="workspace" @keydown="handleShortcut" @contextmenu.prevent>
    <div class="workspace-content" :class="{ 'settings-open': showingSettings }" :inert="showingSettings || !!saving">
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
        <button type="button" class="outlined-button toolbar-button" aria-label="空间分析"
          :disabled="!canViewSpace" :title="indexFileSize ? undefined : spaceUnavailable"
          :aria-description="indexFileSize ? undefined : spaceUnavailable" @click="openSpace">空间分析</button>
        <button type="button" class="outlined-button toolbar-button" @click="openSettings">设置</button>
      </header>

      <SearchResults v-show="!showingSpace" ref="resultsTable" :rows="fatal ? [] : result?.rows ?? []"
        :sort="sort" :descending="descending" :pending="pending" :empty-message="emptyMessage"
        :index-file-size="indexFileSize" :index-modified-time="indexModifiedTime"
        @sort="sortBy" @action="fileAction" @drag="startFileDrag">
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
          <span v-if="indexFileSize && !pending && (result?.directorySortPending || result?.directorySortFailed)" class="directory-sort-status"
            :title="result.directorySortPending ? '正在计算目录大小，排序待更新' : '部分目录大小统计失败'">
            {{ result.directorySortPending ? '正在计算目录大小，排序待更新' : '部分目录大小统计失败' }}
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
        <IndexStatusPanel :status="indexStatus" :connected="connected" :preference-pending="preferencePending || settingsPending"
          @save-preference="updateSavePreference" @action="sendAction({ type: $event })" />
      </footer>
      <SpaceAnalysis v-if="spaceStarted" v-show="showingSpace" :visible="showingSpace" :result="space" :root="spaceRoot"
        :pending="spacePending && !fatal"
        :error="fatal || spaceError" :path-pending="spacePathPending && !fatal" :path-error="spacePathError"
        :index-status="indexStatus" @view="viewSpace" @path="viewSpace(spaceRoot, '', $event)" @close="closeSpace"
        @settings="openSettings"
        @details="requestSpaceDetails"
        @action="(type, key) => sendAction({ type, key })" @exclude="excludeSpaceItem" @restore="restoreSpaceItems" />
    </div>
    <Settings v-if="showingSettings || keepSettingsDraft" v-show="showingSettings" ref="settingsPanel"
      :inert="!!saving" :status="settingsStatus" :pending="settingsPending || preferencePending"
      :connected="connected" :error="fatal || settingsError" @close="closeSettings" @request="requestSettings" />
    <div v-if="!saving && (notice || actionError)" class="operation-feedback" :class="{ error: actionError }" :role="actionError ? 'alert' : 'status'">
      <span>{{ actionError || notice }}</span>
      <button v-if="actionError" type="button" aria-label="关闭提示" @click="actionError = ''">×</button>
    </div>
    <div v-if="saving" class="save-overlay">
      <div>
        <strong role="status">{{ saveLabels[savePhase] }}</strong>
        <progress v-if="saving.total" :value="saving.written" :max="saving.total"
          :aria-label="'已保存 ' + saving.written + ' 个卷，共 ' + saving.total + ' 个卷'"></progress>
        <span v-if="actionError" class="save-error" role="alert">{{ actionError }}</span>
        <button ref="skipSaveButton" type="button" class="outlined-button" :disabled="savePhase !== 'writing'"
          @click="skipExitSave">此次不保存</button>
      </div>
    </div>
  </main>
</template>

<style scoped>
.workspace { position: relative; }
.workspace-content { display: contents; }
.workspace-content.settings-open { visibility: hidden; }
.toolbar-button { height: 39px; white-space: nowrap; }

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
  max-width: min(430px, calc(100% - 24px));
  padding: 18px 22px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--surface);
}
.save-overlay progress { width: 100%; accent-color: var(--accent); }
.save-overlay button { justify-self: end; }
.save-error { color: var(--warning); overflow-wrap: anywhere; }
</style>
