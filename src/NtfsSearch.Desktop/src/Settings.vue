<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import type { SearchServiceRequest, SearchServiceStatus } from './desktop'

const props = defineProps<{
  status?: SearchServiceStatus
  pending: boolean
  connected: boolean
  error: string
}>()
const emit = defineEmits<{ close: []; request: [request: SearchServiceRequest] }>()

const back = ref<HTMLButtonElement>()
const enabled = ref(false)
const port = ref<number | string>('')
const portError = ref('')
const copyFeedback = ref('')
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

// 操作失败时保留待修改的输入，实际地址和状态始终取自后端。
watch(() => props.status, (status, previous) => {
  if (!status || (previous && (status.busy || status.error))) return
  enabled.value = status.enabled
  port.value = status.port
}, { immediate: true })
watch(curl, () => { copyFeedback.value = '' })

function save() {
  const value = Number(port.value)
  if (!Number.isInteger(value) || value < 1 || value > 65535) {
    portError.value = '端口必须是 1 到 65535 之间的整数。'
    return
  }
  portError.value = ''
  emit('request', { type: 'applySearchServiceSettings', enabled: enabled.value, port: value })
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

onMounted(() => back.value?.focus())
</script>

<template>
  <section class="settings-workspace" aria-labelledby="settings-title" @keydown.esc.stop="emit('close')">
    <header class="settings-toolbar">
      <button ref="back" type="button" class="outlined-button" @click="emit('close')">返回</button>
      <h1 id="settings-title">设置</h1>
    </header>
    <div class="settings-scroll">
      <div class="settings-content">
        <h2>搜索服务</h2>
        <form novalidate @submit.prevent="save">
          <fieldset :disabled="!connected || !status || busy">
            <label class="settings-toggle"><input v-model="enabled" type="checkbox">启用 HTTP 搜索服务</label>
            <label class="settings-port">
              端口
              <input v-model="port" type="number" min="1" max="65535" step="1" required
                :aria-invalid="portError ? true : undefined" :aria-describedby="portError ? 'search-service-error' : undefined"
                @input="portError = ''">
            </label>
            <div class="settings-actions">
              <button type="submit" class="outlined-button">保存并应用</button>
              <button type="button" class="outlined-button" :disabled="!status?.enabled" @click="restart">重启服务</button>
            </div>
          </fieldset>
        </form>
        <dl class="settings-status">
          <dt>服务状态</dt><dd role="status">{{ serviceState }}</dd>
          <dt>访问地址</dt><dd>{{ status?.address ?? '—' }}</dd>
        </dl>
        <p v-if="status?.portOverridden" class="settings-note">环境变量 NTFS_SEARCH_PORT 覆盖已保存的端口。</p>
        <p v-if="error" id="search-service-error" class="settings-error" role="alert">{{ error }}</p>

        <section v-if="curl" class="settings-example" aria-labelledby="curl-example-title">
          <h3 id="curl-example-title">Linux / WSL 查询示例</h3>
          <div class="settings-command">
            <pre><code>{{ curl }}</code></pre>
            <button type="button" class="outlined-button" @click="copy">复制示例</button>
          </div>
          <p class="settings-note">Windows 系统卷索引就绪后，响应会返回匹配文件的完整路径。</p>
          <p v-if="copyFeedback" class="settings-note" role="status">{{ copyFeedback }}</p>
        </section>
      </div>
    </div>
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
.settings-scroll { flex: 1; overflow: auto; }
.settings-content { max-width: 820px; padding: 20px 16px; }
.settings-content h2 { margin: 0 0 12px; font-size: 16px; }
.settings-content fieldset { display: grid; gap: 12px; margin: 0; padding: 0; border: 0; }
.settings-toggle, .settings-port, .settings-actions { display: flex; align-items: center; gap: 8px; }
.settings-toggle input { accent-color: var(--accent); }
.settings-port input {
  width: 104px;
  padding: 4px 8px;
  border: 1px solid #c2ccdc;
  border-radius: 5px;
  background: var(--surface);
  font-variant-numeric: tabular-nums;
}
.settings-port input:focus { outline: 1px solid var(--accent); }
.settings-status { display: grid; grid-template-columns: auto 1fr; gap: 4px 12px; margin: 16px 0 0; }
.settings-status dt, .settings-note { color: var(--muted); }
.settings-status dd { margin: 0; font-variant-numeric: tabular-nums; }
.settings-note, .settings-error { margin: 5px 0 0; }
.settings-error { color: var(--warning); }
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
