<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import AdvancedSearch from './AdvancedSearch.vue'
import IndexStatusPanel from './IndexStatus.vue'
import SearchResults from './SearchResults.vue'
import Settings from './Settings.vue'
import SpaceAnalysis from './SpaceAnalysis.vue'
import { applyFileTypeIcons, resetFileTypeIconRequest } from './file-type-icons'
import { formatCount, formatMilliseconds, setLanguage, t } from './localization'
import {
  deleteFiles, dragFiles, listenToBackend, listenToOpenSettings, listenToSearchActivation, sendRequest, startBackend,
  type AdvancedSearchCriteria,
  type DesktopMessage, type DesktopRequest, type FileAction, type IndexStatus, type SearchResult, type SearchRow, type SortField,
  type SettingsRequest, type SettingsStatus, type SettingsTab, type SpaceAnalysisResult, type SpaceEntry, type SpaceExclusion,
  type SpaceVolumeCapacity,
} from './desktop'

const queryInput = ref<HTMLInputElement>()
const resultsTable = ref<InstanceType<typeof SearchResults>>()
const searchMenu = ref<HTMLElement>()
const toolsMenu = ref<HTMLElement>()
const typeMenu = ref<HTMLElement>()
const text = ref('')
const matchPath = ref(true)
const includeFiles = ref(true)
const includeDirectories = ref(true)
const advanced = shallowRef<AdvancedSearchCriteria>({})
const showingAdvanced = ref(false)
const extensions = shallowRef<string[]>([])
const extensionsPending = ref(false)
const extensionsError = ref('')
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
const spaceCapacities = shallowRef<Record<string, SpaceVolumeCapacity>>({})
const spaceRoot = ref('')
const spacePending = ref(false)
const spaceError = ref('')
const spacePathPending = ref(false)
const spacePathError = ref('')
const showingSettings = ref(false)
const keepSettingsDraft = ref(false)
const settingsPanel = ref<InstanceType<typeof Settings>>()
const settingsStatus = shallowRef<SettingsStatus>()
const settingsTab = ref<SettingsTab>('general')
const settingsPending = ref(false)
const settingsError = ref('')
let requestId = Date.now()
let spaceRequestId = requestId
let spaceCapacityRequestId = 0
let spaceDetailId = 0
let unlisten: (() => void) | undefined
let unlistenOpenSettings: (() => void) | undefined
let unlistenActivation: (() => void) | undefined
let focusBeforeSettings: HTMLElement | undefined
let focusBeforeAdvanced: HTMLElement | undefined
let noticeTimer: ReturnType<typeof setTimeout> | undefined
let deletePending = false

const hasAvailableIndex = computed(() => (result.value?.volumesReady ?? 0) > 0)
const indexCreationTime = computed(() => indexStatus.value?.indexCreationTime ?? false)
const indexFileSize = computed(() => indexStatus.value?.indexFileSize ?? true)
const indexModifiedTime = computed(() => indexStatus.value?.indexModifiedTime ?? true)
const canViewSpace = computed(() => connected.value && !!indexStatus.value && indexFileSize.value && !saving.value)
const showSpaceCapacities = computed(() => canViewSpace.value && showingSpace.value && !showingSettings.value
  && (settingsStatus.value?.showSpaceVolumeCapacity ?? true))
watch([showSpaceCapacities, () => space.value?.roots.join('|')], ([enabled]) => {
  if (enabled) refreshSpaceCapacities()
  else ++spaceCapacityRequestId
})
const savePhase = computed(() => {
  const phase = saving.value?.phase ?? 'writing'
  return phase === 'writing' && skipSavePending.value ? 'skipping' : phase
})
const saveLabels = {
  writing: 'app.exitSaveWriting',
  skipping: 'app.exitSaveSkipping',
  committing: 'app.exitSaveCommitting',
}
const spaceUnavailable = computed(() => t('space.indexSizeRequired'))
const page = computed(() => result.value ? Math.floor(result.value.offset / result.value.pageSize) + 1 : 1)
const pages = computed(() => result.value ? Math.max(1, Math.ceil(result.value.total / result.value.pageSize)) : 1)
const canPage = computed(() => connected.value && !pending.value && !queryError.value)
const resultTypesLabel = computed(() => {
  if (includeDirectories.value) return t(includeFiles.value ? 'search.typeAll' : 'search.typeFolders')
  return t(includeFiles.value ? 'search.typeFiles' : 'search.typeNone')
})
const advancedCount = computed(() => {
  const criteria = advanced.value
  return [
    criteria.createdFrom || criteria.createdThrough,
    criteria.modifiedFrom || criteria.modifiedThrough,
    criteria.minSizeBytes || criteria.maxSizeBytes,
    criteria.extensions?.length,
  ].filter(Boolean).length
})
const emptyMessage = computed(() => {
  if (fatal.value) return ''
  if (queryError.value) return queryError.value
  if (pending.value || !result.value) return t('search.searching')
  if (hasAvailableIndex.value) return t('search.noResults')
  if (indexStatus.value?.error || indexStatus.value?.volumes.some(volume => volume.hasError))
    return t('search.indexUnavailable')
  if (indexStatus.value?.collectionCancelled) return t('search.collectionCancelled')
  if (indexStatus.value?.discoveryComplete && !indexStatus.value.volumes.length) return t('index.noVolumes')
  return t('search.loadingIndex')
})
const resultLabel = computed(() => {
  if (pending.value) return t('search.searching')
  if (hasAvailableIndex.value && result.value) return t('search.resultCount', { count: formatCount(result.value.total) })
  return t('index.waiting')
})
const directorySortStatus = computed(() => t(result.value?.directorySortPending
  ? 'search.directorySortPending' : 'search.directorySortFailed'))

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
    case 'status': {
      const firstReady = !indexStatus.value?.volumesReady && message.volumesReady > 0
      indexStatus.value = message
      if (firstReady && showingAdvanced.value) loadExtensions()
      if ((sort.value === 2 && !indexFileSize.value) || (sort.value === 3 && !indexModifiedTime.value)) {
        sort.value = 0
        descending.value = false
      }
      if (spaceStarted.value && !indexFileSize.value) resetSpace()
      break
    }
    case 'settingsStatus':
      settingsStatus.value = message
      setLanguage(message.effectiveLanguage)
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
    case 'spaceCapacities':
      if (showSpaceCapacities.value && message.requestId === spaceCapacityRequestId)
        spaceCapacities.value = message.capacities
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
        extensionsPending.value = false
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
    case 'searchExtensions':
      extensionsPending.value = false
      extensionsError.value = message.error ?? ''
      if (message.extensions) extensions.value = message.extensions
      break
    case 'saving':
      if (showingAdvanced.value) closeAdvancedSearch()
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
        showFeedback(t('app.exitFailed', { error: message.error }), true)
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
    if (showingAdvanced.value) loadExtensions()
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
    descending: descending.value, matchPath: matchPath.value, offset,
    includeFiles: includeFiles.value, includeDirectories: includeDirectories.value, ...advanced.value,
  }).catch(error => {
    if (requestId !== id) return
    connected.value = false
    pending.value = false
    fatal.value ||= String(error)
  })
}

watch([text, matchPath, sort, descending, includeFiles, includeDirectories, advanced], () => query())
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
  if (showingSettings.value || showingAdvanced.value || saving.value) return
  if (event.ctrlKey && event.key.toLowerCase() === 'f') {
    event.preventDefault()
    if (showingSpace.value) closeSpace()
    else focusQuery()
  }
}

function positionMenu(event: MouseEvent) {
  if (!(event.currentTarget instanceof HTMLButtonElement)) return
  const menu = event.currentTarget.popoverTargetElement
  if (!(menu instanceof HTMLElement)) return
  const button = event.currentTarget.getBoundingClientRect()
  // 旧 WebView 按触发按钮的位置显示，新版本由 CSS 锚定以便随窗口调整。
  menu.style.setProperty('--menu-top', `${button.bottom}px`)
  menu.style.setProperty('--menu-left', `${button.left}px`)
  menu.style.setProperty('--menu-right', `${window.innerWidth - button.right}px`)
}

function setMatchPath(value: boolean) {
  searchMenu.value?.hidePopover()
  matchPath.value = value
  if (!showingSpace.value) focusQuery(false)
}

function loadExtensions() {
  if (!connected.value || !indexStatus.value?.volumesReady || extensionsPending.value) return
  extensionsPending.value = true
  extensionsError.value = ''
  void sendRequest({ type: 'searchExtensions' }).catch(error => {
    extensionsPending.value = false
    extensionsError.value = String(error)
  })
}

function openAdvancedSearch() {
  if (showingAdvanced.value || showingSettings.value || saving.value) return
  searchMenu.value?.hidePopover()
  if (showingSpace.value) closeSpace()
  else if (document.activeElement instanceof HTMLElement) focusBeforeAdvanced = document.activeElement
  showingAdvanced.value = true
  loadExtensions()
}

function closeAdvancedSearch() {
  showingAdvanced.value = false
  const previous = focusBeforeAdvanced
  focusBeforeAdvanced = undefined
  void nextTick(() => {
    previous?.focus({ preventScroll: true })
    if (document.activeElement === document.body && !saving.value) focusQuery(false)
  })
}

function applyAdvancedSearch(criteria: AdvancedSearchCriteria) {
  advanced.value = criteria
  closeAdvancedSearch()
}

function openSettings(tab: SettingsTab = 'general') {
  if (showingSettings.value || showingAdvanced.value || saving.value) return
  searchMenu.value?.hidePopover()
  toolsMenu.value?.hidePopover()
  typeMenu.value?.hidePopover()
  if (document.activeElement instanceof HTMLElement) focusBeforeSettings = document.activeElement
  settingsTab.value = tab
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
  if (deletePending || saving.value || showingSpace.value || showingAdvanced.value) return
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
  typeMenu.value?.hidePopover()
  showingSpace.value = true
  if (spaceStarted.value) {
    sendAction({ type: 'spaceVisibility', enabled: true })
  } else {
    spaceStarted.value = true
    viewSpace()
  }
}

function refreshSpaceCapacities() {
  if (!showSpaceCapacities.value || !space.value?.roots.length) return
  const id = ++spaceCapacityRequestId
  void sendRequest({ type: 'spaceCapacities', requestId: id }).catch(() => {
    if (id === spaceCapacityRequestId) spaceCapacities.value = {}
  })
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

async function deleteSearchResults(keys: string[], permanent: boolean) {
  if (deletePending || !connected.value || pending.value || saving.value || showingSpace.value) return
  deletePending = true
  showFeedback('')
  try {
    await deleteFiles(keys, permanent)
  } catch (error) {
    showFeedback(String(error), true)
  } finally {
    deletePending = false
  }
}

onMounted(async () => {
  await nextTick()
  queryInput.value?.focus()
  try {
    unlistenOpenSettings = await listenToOpenSettings(() => openSettings())
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
    <nav v-show="!showingSettings" class="top-menu" :aria-label="t('menu.app')" :inert="showingAdvanced || !!saving">
      <button type="button" popovertarget="search-menu" @click="positionMenu">{{ t('common.search') }}</button>
      <div id="search-menu" ref="searchMenu" class="file-menu anchored-menu" popover role="group" :aria-label="t('menu.searchCommands')">
        <button type="button" autofocus @click="openAdvancedSearch">{{ t('menu.advancedSearch') }}</button>
        <div class="menu-separator" role="separator"></div>
        <button type="button" :aria-label="t('search.matchPath')" :aria-pressed="matchPath" @click="setMatchPath(true)">
          {{ t('search.matchPath') }} <span v-if="matchPath" class="menu-check" aria-hidden="true">✓</span>
        </button>
        <button type="button" :aria-label="t('search.matchName')" :aria-pressed="!matchPath" @click="setMatchPath(false)">
          {{ t('search.matchName') }} <span v-if="!matchPath" class="menu-check" aria-hidden="true">✓</span>
        </button>
      </div>
      <button type="button" popovertarget="tools-menu" @click="positionMenu">{{ t('menu.tools') }}</button>
      <div id="tools-menu" ref="toolsMenu" class="file-menu anchored-menu" popover role="group" :aria-label="t('menu.toolCommands')">
        <button type="button" autofocus @click="openSettings()">{{ t('menu.settings') }}</button>
      </div>
    </nav>
    <div class="workspace-content" :class="{ 'settings-open': showingSettings }" :inert="showingSettings || !!saving">
      <header v-show="!showingSpace" class="search-toolbar">
        <div class="search-field">
          <span class="search-glyph" aria-hidden="true"></span>
          <input ref="queryInput" v-model="text" type="text" :aria-label="t('search.input')" :placeholder="t('search.input')"
            autocomplete="off" spellcheck="false" :title="t('search.inputHint')"
            @keydown.down.prevent="resultsTable?.focusSelection()">
          <button v-if="text" type="button" class="clear-search" :title="t('search.clear')" :aria-label="t('search.clear')" @click="clearQuery">×</button>
        </div>
        <button id="result-type-toggle" type="button" class="outlined-button type-toggle"
          :class="{ 'type-selected': !includeFiles || !includeDirectories }" :aria-label="t('search.resultTypeSelection', { selection: resultTypesLabel })"
          popovertarget="result-type-menu" @click="positionMenu">
          <span>{{ resultTypesLabel }}</span><i class="menu-caret" aria-hidden="true"></i>
        </button>
        <div id="result-type-menu" ref="typeMenu" class="file-menu anchored-menu type-menu" popover role="group" :aria-label="t('search.resultType')">
          <label><input v-model="includeDirectories" name="include-directories" type="checkbox" autofocus>{{ t('search.typeFolders') }}</label>
          <label><input v-model="includeFiles" name="include-files" type="checkbox">{{ t('search.typeFiles') }}</label>
        </div>
        <button type="button" class="outlined-button toolbar-button" :aria-label="t('common.spaceAnalysis')"
          :disabled="!canViewSpace" :title="indexFileSize ? undefined : spaceUnavailable"
          :aria-description="indexFileSize ? undefined : spaceUnavailable" @click="openSpace">{{ t('common.spaceAnalysis') }}</button>
      </header>

      <SearchResults v-show="!showingSpace" ref="resultsTable" :rows="fatal ? [] : result?.rows ?? []"
        :sort="sort" :descending="descending" :pending="pending" :empty-message="emptyMessage"
        :index-file-size="indexFileSize" :index-modified-time="indexModifiedTime"
        @sort="sortBy" @action="fileAction" @drag="startFileDrag" @delete="deleteSearchResults">
        <div v-if="fatal" class="startup-error" role="alert">
          <strong>{{ t('search.serviceError') }}</strong>
          <span>{{ fatal }}</span>
          <button type="button" class="outlined-button" @click="connect">{{ t('common.retry') }}</button>
        </div>
      </SearchResults>

      <footer v-show="!showingSpace" class="status-bar">
        <div class="result-summary" aria-live="polite" aria-atomic="true">
          <span class="result-count">{{ resultLabel }}</span>
          <span v-if="advancedCount" class="query-time">{{ t('search.filterCount', { count: formatCount(advancedCount) }) }}</span>
          <span v-if="hasAvailableIndex && result?.userMilliseconds != null" class="query-time" :title="t('search.queryTime')">
            {{ t('search.queryMilliseconds', { time: formatMilliseconds(result.userMilliseconds) }) }}
          </span>
          <span v-if="indexFileSize && !pending && (result?.directorySortPending || result?.directorySortFailed)" class="directory-sort-status"
            :title="directorySortStatus">
            {{ directorySortStatus }}
          </span>
        </div>
        <div class="page-controls" :aria-label="t('search.pagination')">
          <button type="button" :title="t('search.previousPage')" :aria-label="t('search.previousPage')" :disabled="!canPage || page <= 1" @click="turnPage(-1)">
            <span class="chevron left" aria-hidden="true"></span>
          </button>
          <span class="page-number" :aria-label="t('search.pageNumber')">{{ t('search.pageFraction', { page: formatCount(page), total: formatCount(pages) }) }}</span>
          <button type="button" :title="t('search.nextPage')" :aria-label="t('search.nextPage')" :disabled="!canPage || page >= pages" @click="turnPage(1)">
            <span class="chevron right" aria-hidden="true"></span>
          </button>
        </div>
        <IndexStatusPanel :status="indexStatus" :connected="connected" :preference-pending="preferencePending || settingsPending"
          @save-preference="updateSavePreference" @action="sendAction({ type: $event })" />
      </footer>
      <SpaceAnalysis v-if="spaceStarted" v-show="showingSpace" class="space-page"
        :visible="showingSpace" :result="space" :root="spaceRoot"
        :pending="spacePending && !fatal"
        :error="fatal || spaceError" :path-pending="spacePathPending && !fatal" :path-error="spacePathError"
        :capacities="spaceCapacities" :volume-settings="settingsStatus" @refresh-capacities="refreshSpaceCapacities"
        :index-status="indexStatus" @view="viewSpace" @path="viewSpace(spaceRoot, '', $event)" @close="closeSpace"
        @settings="openSettings('space')"
        @details="requestSpaceDetails"
        @action="(type, key) => sendAction({ type, key })" @exclude="excludeSpaceItem" @restore="restoreSpaceItems" />
    </div>
    <AdvancedSearch v-if="showingAdvanced" :criteria="advanced" :extensions="extensions"
      :extensions-pending="extensionsPending" :extensions-error="extensionsError"
      :index-ready="!!indexStatus?.volumesReady"
      :index-creation-time="indexCreationTime" :index-modified-time="indexModifiedTime" :index-file-size="indexFileSize"
      @apply="applyAdvancedSearch" @close="closeAdvancedSearch" @retry="loadExtensions" />
    <Settings v-if="showingSettings || keepSettingsDraft" v-show="showingSettings" ref="settingsPanel"
      v-model:tab="settingsTab" :inert="!!saving" :status="settingsStatus" :pending="settingsPending || preferencePending"
      :connected="connected" :error="fatal || settingsError" @close="closeSettings" @request="requestSettings" />
    <div v-if="!saving && (notice || actionError)" class="operation-feedback" :class="{ error: actionError }" :role="actionError ? 'alert' : 'status'">
      <span>{{ actionError || notice }}</span>
      <button v-if="actionError" type="button" :aria-label="t('app.dismissNotice')" @click="actionError = ''">×</button>
    </div>
    <div v-if="saving" class="save-overlay">
      <div>
        <strong role="status">{{ t(saveLabels[savePhase]) }}</strong>
        <progress v-if="saving.total" :value="saving.written" :max="saving.total"
          :aria-label="t('app.exitSaveProgress', { written: formatCount(saving.written), total: formatCount(saving.total) })"></progress>
        <span v-if="actionError" class="save-error" role="alert">{{ actionError }}</span>
        <button ref="skipSaveButton" type="button" class="outlined-button" :disabled="savePhase !== 'writing'"
          @click="skipExitSave">{{ t('app.skipExitSave') }}</button>
      </div>
    </div>
  </main>
</template>

<style scoped>
.workspace { position: relative; grid-template-rows: auto auto minmax(0, 1fr) auto; }
.workspace-content { display: contents; }
.workspace-content.settings-open { visibility: hidden; }
.workspace .space-page { grid-row: 2 / -1; }
.toolbar-button { height: 39px; white-space: nowrap; }
.top-menu {
  grid-row: 1;
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 3px 13px;
  border-bottom: 1px solid var(--border);
  background: var(--chrome);
}
.top-menu > button {
  min-height: 27px;
  padding: 3px 12px;
  border: 0;
  border-radius: 5px;
  background: transparent;
}
.top-menu > button:hover,
.top-menu:has(#search-menu:popover-open) > [popovertarget="search-menu"],
.top-menu:has(#tools-menu:popover-open) > [popovertarget="tools-menu"] { background: var(--accent-quiet); }
.anchored-menu {
  inset: auto;
  top: var(--menu-top);
  left: var(--menu-left);
  top: anchor(bottom, var(--menu-top));
  left: anchor(left, var(--menu-left));
  width: 174px;
  margin: 4px 0 0;
  color: inherit;
}
.anchored-menu:not(:popover-open) { display: none; }
.menu-separator { margin: 3px 5px; border-top: 1px solid var(--border); }
.menu-check { color: var(--accent); font-weight: 600; }
.type-toggle {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  min-width: 88px;
  height: 39px;
  border-radius: 7px;
  background: var(--surface);
  white-space: nowrap;
}
.type-toggle.type-selected { border-color: #b4c7f3; background: var(--accent-quiet); color: var(--accent); }
.menu-caret {
  width: 6px;
  height: 6px;
  margin: -3px 1px 0 0;
  border-right: 1.5px solid currentColor;
  border-bottom: 1.5px solid currentColor;
  transform: rotate(45deg);
}
.type-menu {
  right: var(--menu-right);
  right: anchor(right, var(--menu-right));
  left: auto;
  width: 126px;
}
.type-menu label { display: flex; align-items: center; gap: 8px; padding: 5px 8px; border-radius: 4px; cursor: pointer; }
.type-menu label:hover, .type-menu label:focus-within { background: var(--accent-quiet); }
.type-menu input { margin: 0; accent-color: var(--accent); }

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
