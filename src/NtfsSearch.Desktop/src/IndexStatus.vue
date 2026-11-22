<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { formatCount, type IndexStatus } from './desktop'

const props = defineProps<{ status?: IndexStatus; connected: boolean; preferencePending: boolean }>()
const emit = defineEmits<{ action: [type: 'rescan' | 'cancel']; savePreference: [skip: boolean] }>()
const open = ref(false)
const container = ref<HTMLElement>()
const trigger = ref<HTMLButtonElement>()
const panel = ref<HTMLElement>()
const warning = computed(() => props.status?.error || props.status?.volumes.some(volume => volume.hasError))
const busy = computed(() => !props.status || props.status.active > 0 || props.status.refreshing)

async function toggle() {
  open.value = !open.value
  if (open.value) {
    await nextTick()
    panel.value?.focus()
  }
}

function dismiss(event: PointerEvent) {
  if (!container.value?.contains(event.target as Node)) open.value = false
}

function escape() {
  if (!open.value) return
  open.value = false
  trigger.value?.focus()
}

function changeSavePreference(event: Event) {
  const checkbox = event.currentTarget as HTMLInputElement
  const skip = checkbox.checked
  checkbox.checked = props.status?.skipExitIndexSave ?? false
  emit('savePreference', skip)
}

onMounted(() => window.addEventListener('pointerdown', dismiss))
onBeforeUnmount(() => window.removeEventListener('pointerdown', dismiss))
</script>

<template>
  <div ref="container" class="index-control" @keydown.esc.stop="escape">
    <button ref="trigger" class="index-toggle" type="button" :aria-expanded="open" aria-controls="index-panel" @click="toggle">
      <span class="status-dot" :class="{ warning, busy }" aria-hidden="true"></span>
      <span>{{ warning ? '索引状态有异常' : '索引状态' }}</span>
      <span class="chevron up" :class="{ flipped: open }" aria-hidden="true"></span>
    </button>
    <section v-if="open" id="index-panel" ref="panel" class="index-panel" aria-label="索引状态" tabindex="-1">
      <header class="index-header">
        <h2>索引状态</h2>
        <button type="button" aria-label="关闭索引状态" class="close-panel" @click="escape">×</button>
      </header>
      <p class="index-summary">
        <template v-if="status?.volumes.length">{{ status.volumesReady }} / {{ status.volumes.length }} 个卷可搜索 · {{ formatCount(status.indexed) }} 项已索引</template>
        <template v-else>{{ status?.discoveryComplete ? '未发现可用的 NTFS 磁盘' : '正在发现本机 NTFS 磁盘' }}</template>
        <template v-if="status?.active"> · 正在采集 {{ status.active }} 个卷</template>
      </p>
      <p v-if="status?.error" class="index-error" role="alert">{{ status.error }}</p>
      <p v-if="status?.progress && (busy || !status.volumesReady || status.collectionCancelled)" class="index-progress">
        {{ status.progress }}
      </p>
      <ul v-if="status?.volumes.length" class="volume-list">
        <li v-for="volume in status.volumes" :key="volume.root" :class="{ warning: volume.hasError }">
          <span>{{ volume.root }}</span><span>{{ volume.message }}</span>
        </li>
      </ul>
      <label class="index-exit-preference">
        <input type="checkbox" :checked="status?.skipExitIndexSave" :disabled="!connected || !status || preferencePending"
          @change="changeSavePreference">
        退出时不保存索引
      </label>
      <div class="index-actions">
        <button type="button" class="outlined-button" :disabled="!connected || status?.refreshing" @click="emit('action', 'rescan')">重新扫描磁盘</button>
        <button type="button" class="outlined-button" :disabled="!connected" @click="emit('action', 'cancel')">取消采集</button>
      </div>
    </section>
  </div>
</template>
