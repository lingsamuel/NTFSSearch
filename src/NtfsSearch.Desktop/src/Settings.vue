<script setup lang="ts">
import { computed, nextTick, reactive, ref, watch } from 'vue'
import { defaultSpaceVolumeSettings, type SettingsRequest, type SettingsStatus, type SettingsTab } from './desktop'

const props = defineProps<{
  status?: SettingsStatus
  pending: boolean
  connected: boolean
  error: string
}>()
const emit = defineEmits<{ close: []; request: [request: SettingsRequest] }>()
const tab = defineModel<SettingsTab>('tab', { required: true })
const settingsScroll = ref<HTMLDivElement>()
const settingsTabs = [
  { id: 'search', label: '搜索', title: 'search-settings-title' },
  { id: 'index', label: '索引', title: 'index-settings-title' },
  { id: 'space', label: '空间分析', title: 'space-settings-title' },
  { id: 'service', label: '搜索服务', title: 'service-settings-title' },
] as const
const tabs = computed(() => props.status?.searchServiceAvailable ? settingsTabs : settingsTabs.slice(0, -1))

const back = ref<HTMLButtonElement>()
const enabled = ref(false)
const port = ref<number | string>('')
const portInput = ref<HTMLInputElement>()
const searchDirectorySizes = ref(true)
const saveExitIndex = ref(true)
const indexMetadataOptions = [
  { key: 'indexCreationTime', name: 'index-creation-time', label: '索引创建时间' },
  { key: 'indexModifiedTime', name: 'index-modified-time', label: '索引修改时间' },
  { key: 'indexFileSize', name: 'index-file-size', label: '索引文件大小' },
] as const
const defaultIndexSettings = { precomputeSort: true, indexCreationTime: false, indexModifiedTime: true, indexFileSize: true }
const indexSettings = reactive({ ...defaultIndexSettings })
const spaceVolumeSettings = reactive({ ...defaultSpaceVolumeSettings })
const spaceVolumeOptions = ['showSpaceVolumeCapacity', 'spaceVolumeShowFree', 'spaceVolumeCapacityBar'] as const
const capacityDisplay = computed({
  get: () => spaceVolumeSettings.spaceVolumeShowFree ? 'free' : 'used',
  set: value => { spaceVolumeSettings.spaceVolumeShowFree = value === 'free' },
})
const portError = ref('')
const copyFeedback = ref('')
let saved: SettingsStatus | undefined
const busy = computed(() => props.pending || props.status?.busy)
const serviceState = computed(() => {
  if (!props.connected) return '无法获取'
  if (busy.value) return props.status ? '正在处理…' : '正在读取…'
  if (!props.status) return '无法获取'
  if (props.status.running) return '运行中'
  return props.status.enabled ? '已停止' : '已禁用'
})
const error = computed(() => portError.value || props.error || (!busy.value && props.status?.error) || '')
const curl = computed(() => props.status?.address
  ? String.raw`curl -sS --json '{"text":"C:\\Windows\\explorer.exe","limit":1}' ${props.status.address}/v1/search`
  : '')

// 只同步实际配置有变化的字段，重启服务或保存失败时保留表单草稿。
watch(() => props.status, status => {
  if (!status || status.busy) return
  if (!saved || status.enabled !== saved.enabled) enabled.value = status.enabled
  if (!saved || status.port !== saved.port) port.value = status.port
  if (!saved || status.searchDirectorySizes !== saved.searchDirectorySizes)
    searchDirectorySizes.value = status.searchDirectorySizes
  if (!saved || status.skipExitIndexSave !== saved.skipExitIndexSave)
    saveExitIndex.value = !status.skipExitIndexSave
  if (!saved || status.precomputeSort !== saved.precomputeSort)
    indexSettings.precomputeSort = status.precomputeSort ?? defaultIndexSettings.precomputeSort
  for (const { key } of indexMetadataOptions) {
    if (!saved || status[key] !== saved[key]) indexSettings[key] = status[key] ?? defaultIndexSettings[key]
  }
  for (const key of spaceVolumeOptions) {
    if (!saved || status[key] !== saved[key]) spaceVolumeSettings[key] = status[key] ?? defaultSpaceVolumeSettings[key]
  }
  saved = status
}, { immediate: true })
watch(curl, () => { copyFeedback.value = '' })
watch(tab, () => { if (settingsScroll.value) settingsScroll.value.scrollTop = 0 })
watch(tabs, available => {
  if (!available.some(item => item.id === tab.value)) tab.value = 'search'
})

function navigateTabs(event: KeyboardEvent) {
  const buttons = Array.from((event.currentTarget as HTMLElement).querySelectorAll('button'))
  const current = buttons.indexOf(event.target as HTMLButtonElement)
  let next: number
  switch (event.key) {
    case 'ArrowRight': next = (current + 1) % buttons.length; break
    case 'ArrowLeft': next = (current + buttons.length - 1) % buttons.length; break
    case 'Home': next = 0; break
    case 'End': next = buttons.length - 1; break
    default: return
  }
  event.preventDefault()
  tab.value = tabs.value[next].id
  buttons[next].focus()
}

function save() {
  const value = Number(port.value)
  if (props.status?.searchServiceAvailable && (!Number.isInteger(value) || value < 1 || value > 65535)) {
    portError.value = '端口必须是 1 到 65535 之间的整数。'
    tab.value = 'service'
    void nextTick(() => portInput.value?.focus())
    return
  }
  portError.value = ''
  const service = props.status?.searchServiceAvailable ? { enabled: enabled.value, port: value } : {}
  emit('request', { type: 'applySettings', ...service,
    searchDirectorySizes: searchDirectorySizes.value, skipExitIndexSave: !saveExitIndex.value, ...indexSettings, ...spaceVolumeSettings })
}

function restart() {
  portError.value = ''
  emit('request', { type: 'restartSearchService' })
}

async function copy() {
  try {
    await navigator.clipboard.writeText(curl.value)
    copyFeedback.value = '已复制示例'
  } catch {
    copyFeedback.value = '复制失败，可选中命令手动复制。'
  }
}

function focusBack() {
  back.value?.focus()
}

defineExpose({ focusBack })
</script>

<template>
  <section class="settings-workspace" aria-labelledby="settings-title" @keydown.esc.stop="emit('close')">
    <header class="settings-toolbar">
      <button ref="back" type="button" class="outlined-button" @click="emit('close')">返回</button>
      <h1 id="settings-title">设置</h1>
    </header>
    <div class="settings-tabs" role="tablist" aria-label="设置分类" @keydown="navigateTabs">
      <button v-for="item in tabs" :id="item.title" :key="item.id" type="button" role="tab"
        :aria-controls="`settings-panel-${item.id}`" :aria-selected="tab === item.id"
        :tabindex="tab === item.id ? 0 : -1" @click="tab = item.id">{{ item.label }}</button>
    </div>
    <div ref="settingsScroll" class="settings-scroll">
      <form id="desktop-settings" class="settings-content" novalidate @submit.prevent="save">
        <fieldset :disabled="!connected || !status || busy">
          <section v-show="tab === 'search'" id="settings-panel-search" role="tabpanel" aria-labelledby="search-settings-title">
            <label class="settings-toggle">
              <input v-model="searchDirectorySizes" name="search-directory-sizes" type="checkbox"
                :disabled="!indexSettings.indexFileSize"
                :aria-describedby="!indexSettings.indexFileSize ? 'folder-size-note' : undefined">计算搜索结果中的文件夹大小
            </label>
            <p v-if="!indexSettings.indexFileSize" id="folder-size-note" class="settings-note">
              文件夹大小统计依赖「索引文件大小」。
            </p>
          </section>
          <section v-show="tab === 'index'" id="settings-panel-index" role="tabpanel" aria-labelledby="index-settings-title">
            <label class="settings-toggle">
              <input v-model="saveExitIndex" name="save-exit-index" type="checkbox">退出时保存索引
            </label>
            <label class="settings-toggle">
              <input v-model="indexSettings.precomputeSort" name="precompute-sort" type="checkbox"
                aria-describedby="precompute-sort-note">预计算排序
            </label>
            <p id="precompute-sort-note" class="settings-note">保存后立即生效。</p>
            <p class="settings-note index-settings-note">保存后，以下索引设置将在下次重启时生效。请从托盘菜单退出程序，再重新打开。</p>
            <label v-for="option in indexMetadataOptions" :key="option.key" class="settings-toggle">
              <input v-model="indexSettings[option.key]" :name="option.name" type="checkbox"
                :aria-describedby="option.key === 'indexFileSize' ? 'index-file-size-note' : undefined">{{ option.label }}
            </label>
            <p id="index-file-size-note" class="settings-note">
              关闭文件大小索引会停用大小列、按大小排序和筛选、文件夹大小统计及空间分析。
            </p>
          </section>
          <section v-show="tab === 'space'" id="settings-panel-space" role="tabpanel" aria-labelledby="space-settings-title">
            <label class="settings-toggle">
              <input v-model="spaceVolumeSettings.showSpaceVolumeCapacity" name="show-space-volume-capacity" type="checkbox">显示磁盘容量
            </label>
            <label class="settings-capacity-display">
              容量显示方式
              <select v-model="capacityDisplay" name="space-volume-display" :disabled="!spaceVolumeSettings.showSpaceVolumeCapacity">
                <option value="used">已用空间</option>
                <option value="free">剩余空间</option>
              </select>
            </label>
            <label class="settings-toggle">
              <input v-model="spaceVolumeSettings.spaceVolumeCapacityBar" name="space-volume-capacity-bar" type="checkbox"
                :disabled="!spaceVolumeSettings.showSpaceVolumeCapacity">显示背景用量条
            </label>
          </section>
          <section v-if="status?.searchServiceAvailable" v-show="tab === 'service'" id="settings-panel-service"
            class="settings-service" role="tabpanel" aria-labelledby="service-settings-title">
            <label class="settings-toggle"><input v-model="enabled" name="search-service-enabled" type="checkbox">启用 HTTP 搜索服务</label>
            <label class="settings-port">
              端口
              <input ref="portInput" v-model="port" type="number" min="1" max="65535" step="1" required
                :aria-invalid="portError ? true : undefined" :aria-describedby="portError ? 'settings-error' : undefined"
                @input="portError = ''">
            </label>
            <button type="button" class="outlined-button" :disabled="!status?.enabled" @click="restart">重启服务</button>
            <dl class="settings-status">
              <dt>服务状态</dt><dd role="status">{{ serviceState }}</dd>
              <dt>访问地址</dt><dd>{{ status?.address ?? '—' }}</dd>
            </dl>
            <p v-if="status?.portOverridden" class="settings-note">环境变量 NTFS_SEARCH_PORT 覆盖已保存的端口。</p>
            <section v-if="curl" class="settings-example" aria-labelledby="curl-example-title">
              <h3 id="curl-example-title">Linux / WSL 查询示例</h3>
              <div class="settings-command">
                <pre><code>{{ curl }}</code></pre>
                <button type="button" class="outlined-button" @click="copy">复制示例</button>
              </div>
              <p class="settings-note">Windows 系统卷索引就绪后，响应会返回匹配文件的完整路径。</p>
              <p v-if="copyFeedback" class="settings-note" role="status">{{ copyFeedback }}</p>
            </section>
          </section>
        </fieldset>
      </form>
    </div>
    <footer class="settings-footer">
      <div class="settings-feedback">
        <p v-if="status?.indexRestartRequired" class="settings-note" role="status">索引字段设置已保存，下次重启生效。</p>
        <p v-if="error" id="settings-error" class="settings-error" role="alert">{{ error }}</p>
      </div>
      <button type="submit" form="desktop-settings" class="outlined-button" :disabled="!connected || !status || busy">
        保存并应用
      </button>
    </footer>
  </section>
</template>

<style scoped>
.settings-workspace {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  background: var(--surface);
}
.settings-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 13px 16px;
  border-bottom: 1px solid var(--border);
  background: var(--chrome);
}
.settings-toolbar button { height: 39px; }
.settings-toolbar h1 { margin: 0; font-size: 15px; }
.settings-tabs {
  display: flex;
  gap: 4px;
  padding: 0 16px;
  border-bottom: 1px solid var(--border);
}
.settings-tabs button {
  padding: 10px 12px 8px;
  border: 0;
  border-bottom: 2px solid transparent;
  background: transparent;
  white-space: nowrap;
}
.settings-tabs button:hover { background: var(--accent-quiet); }
.settings-tabs button[aria-selected="true"] { border-bottom-color: var(--accent); color: var(--accent); font-weight: 600; }
.settings-tabs button:focus-visible { outline-offset: -2px; }
.settings-scroll { flex: 1; overflow: auto; }
.settings-content { max-width: 820px; padding: 20px 16px; }
.settings-content fieldset { min-width: 0; margin: 0; padding: 0; border: 0; }
.settings-toggle, .settings-port, .settings-capacity-display { display: flex; align-items: center; gap: 8px; }
.settings-toggle + .settings-toggle { margin-top: 7px; }
.settings-note.index-settings-note { margin: 12px 0 7px; }
.settings-port, .settings-capacity-display { margin: 12px 0; }
.settings-toggle input { accent-color: var(--accent); }
.settings-port input, .settings-capacity-display select {
  padding: 4px 8px;
  border: 1px solid #c2ccdc;
  border-radius: 5px;
  background: var(--surface);
  font-variant-numeric: tabular-nums;
}
.settings-port input { width: 104px; }
.settings-capacity-display select:disabled { color: var(--muted); }
.settings-port input:focus { outline: 1px solid var(--accent); }
.settings-status { display: grid; grid-template-columns: auto 1fr; gap: 4px 12px; margin: 16px 0 0; }
.settings-status dt, .settings-note { color: var(--muted); }
.settings-status dd { margin: 0; font-variant-numeric: tabular-nums; }
.settings-note, .settings-error { margin: 5px 0 0; }
.settings-error { color: var(--warning); }
.settings-footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 12px;
  padding: 10px 16px;
  border-top: 1px solid var(--border);
  background: var(--chrome);
}
.settings-footer button { flex: none; }
.settings-feedback p { margin: 0; }
.settings-feedback p + p { margin-top: 4px; }
.settings-example { margin-top: 18px; padding-top: 14px; border-top: 1px solid var(--border); }
.settings-example h3 { margin: 0 0 7px; font-size: 13px; }
.settings-command { display: flex; align-items: center; gap: 8px; }
.settings-command pre {
  flex: 1;
  overflow-x: auto;
  margin: 0;
  padding: 6px 8px;
  border: 1px solid var(--border);
  border-radius: 5px;
  background: var(--chrome);
  font: 12px/1.5 Consolas, monospace;
}
.settings-command button { flex: none; }
</style>
