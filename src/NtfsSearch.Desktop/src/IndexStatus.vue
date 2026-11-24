<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import type { IndexStatus } from './desktop'
import { formatCount, t } from './localization'

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
      <span>{{ t(warning ? 'index.statusWarning' : 'index.status') }}</span>
      <span class="chevron up" :class="{ flipped: open }" aria-hidden="true"></span>
    </button>
    <section v-if="open" id="index-panel" ref="panel" class="index-panel" :aria-label="t('index.status')" tabindex="-1">
      <header class="index-header">
        <h2>{{ t('index.status') }}</h2>
        <button type="button" :aria-label="t('index.closeStatus')" class="close-panel" @click="escape">×</button>
      </header>
      <p class="index-summary">
        <template v-if="status?.volumes.length">{{ t('index.summary', {
          ready: formatCount(status.volumesReady), total: formatCount(status.volumes.length), count: formatCount(status.indexed),
        }) }}</template>
        <template v-else>{{ t(status?.discoveryComplete ? 'index.noVolumes' : 'index.discovering') }}</template>
        <template v-if="status?.active">{{ t('index.collecting', { count: formatCount(status.active) }) }}</template>
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
        {{ t('index.skipExitSave') }}
      </label>
      <div class="index-actions">
        <button type="button" class="outlined-button" :disabled="!connected || status?.refreshing" @click="emit('action', 'rescan')">{{ t('index.rescan') }}</button>
        <button type="button" class="outlined-button" :disabled="!connected" @click="emit('action', 'cancel')">{{ t('index.cancelCollection') }}</button>
      </div>
    </section>
  </div>
</template>
