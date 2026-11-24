<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { defaultSpaceVolumeSettings, formatPercent, type SpaceVolumeCapacity, type SpaceVolumeSettings } from './desktop'

const props = defineProps<{
  roots: string[]
  root: string
  visible: boolean
  capacities: Record<string, SpaceVolumeCapacity>
  settings?: SpaceVolumeSettings
}>()
const emit = defineEmits<{ select: [root: string]; open: [] }>()
const picker = ref<HTMLDivElement>()
const trigger = ref<HTMLButtonElement>()
const menu = ref<HTMLDivElement>()
const open = ref(false)
const settings = computed(() => props.settings ?? defaultSpaceVolumeSettings)
const options = computed(() => props.roots.map(root => {
  const capacity = settings.value.showSpaceVolumeCapacity ? props.capacities[root] : undefined
  if (!capacity) return { root, text: undefined, fill: undefined }
  const showFree = settings.value.spaceVolumeShowFree
  const fraction = showFree ? 1 - capacity.usedFraction : capacity.usedFraction
  const size = showFree ? capacity.freeText : capacity.usedText
  return {
    root,
    text: `${size} / ${capacity.totalText} (${formatPercent(fraction)})`,
    fill: settings.value.spaceVolumeCapacityBar ? { '--volume-fill': `${fraction * 100}%` } : undefined,
  }
}))
const selected = computed(() => options.value.find(option => option.root === props.root))

watch([() => props.visible, () => props.roots.length], ([visible, count]) => {
  if ((!visible || !count) && menu.value?.matches(':popover-open')) menu.value.hidePopover()
})

function openMenu() {
  if (!trigger.value || !menu.value || !props.roots.length || menu.value.matches(':popover-open')) return
  const rect = trigger.value.getBoundingClientRect()
  Object.assign(menu.value.style, {
    left: `${rect.left}px`, top: `${rect.bottom + 4}px`, minWidth: `${rect.width}px`,
    maxWidth: `${window.innerWidth - rect.left - 8}px`, maxHeight: `${window.innerHeight - rect.bottom - 12}px`,
  })
  menu.value.showPopover()
  const selectedButton = menu.value.querySelector<HTMLButtonElement>('[aria-selected="true"]')
    ?? menu.value.querySelector<HTMLButtonElement>('button')
  selectedButton?.focus()
  emit('open')
}

function closeMenu() {
  menu.value?.hidePopover()
  trigger.value?.focus()
}

function toggleMenu() {
  if (menu.value?.matches(':popover-open')) closeMenu()
  else openMenu()
}

function select(root: string) {
  closeMenu()
  if (root !== props.root) emit('select', root)
}

function navigate(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    event.preventDefault()
    event.stopPropagation()
    closeMenu()
    return
  }
  if (!menu.value || !['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return
  const buttons = Array.from(menu.value.querySelectorAll('button'))
  const current = buttons.indexOf(event.target as HTMLButtonElement)
  let next = current
  switch (event.key) {
    case 'ArrowDown': next = Math.min(current + 1, buttons.length - 1); break
    case 'ArrowUp': next = Math.max(current - 1, 0); break
    case 'Home': next = 0; break
    case 'End': next = buttons.length - 1; break
  }
  event.preventDefault()
  buttons[next]?.focus()
}

function dismissOnBlur(event: FocusEvent) {
  if (menu.value?.matches(':popover-open') && !picker.value?.contains(event.relatedTarget as Node | null))
    menu.value.hidePopover()
}
</script>

<template>
  <div ref="picker" class="space-volume-picker" @focusout="dismissOnBlur">
    <button ref="trigger" type="button" class="scope-select volume-trigger" aria-label="分析磁盘"
      aria-haspopup="listbox" aria-controls="space-volume-options" aria-describedby="space-volume-selection"
      :aria-expanded="open" :disabled="!options.length" :style="selected?.fill" @click="toggleMenu"
      @keydown.down.prevent="openMenu" @keydown.up.prevent="openMenu">
      <span id="space-volume-selection" class="volume-label">
        <span>{{ selected?.root ?? (options.length ? '选择磁盘' : '等待索引') }}</span>
        <span v-if="selected?.text" class="volume-capacity">{{ selected.text }}</span>
      </span>
      <span class="chevron up flipped" aria-hidden="true"></span>
    </button>
    <div id="space-volume-options" ref="menu" class="volume-options" popover role="listbox" aria-label="分析磁盘"
      @toggle="open = $event.newState === 'open'" @keydown="navigate">
      <button v-for="option in options" :key="option.root" type="button" role="option" tabindex="-1"
        class="volume-label" :aria-selected="option.root === root" :style="option.fill" @click="select(option.root)"
        @keydown.enter.prevent="select(option.root)" @keydown.space.prevent="select(option.root)">
        <span>{{ option.root }}</span>
        <span v-if="option.text" class="volume-capacity">{{ option.text }}</span>
      </button>
    </div>
  </div>
</template>

<style scoped>
.volume-trigger, .volume-options button {
  background-image: linear-gradient(to right, #dce8ff var(--volume-fill, 0%), transparent 0);
}
.volume-trigger {
  display: flex;
  min-width: 113px;
  align-items: center;
  gap: 12px;
}
.volume-trigger .chevron { flex: none; }
.volume-label { display: flex; flex: 1; align-items: center; justify-content: space-between; gap: 12px; }
.volume-capacity { color: var(--muted); white-space: nowrap; font-variant-numeric: tabular-nums; }
.volume-options {
  position: fixed;
  inset: auto;
  width: max-content;
  margin: 0;
  padding: 3px;
  border: 1px solid var(--border);
  border-radius: 7px;
  color: inherit;
  background: var(--surface);
  box-shadow: 0 4px 14px #19253820;
  overflow: auto;
}
.volume-options button {
  width: 100%;
  min-height: 34px;
  padding: 5px 8px;
  border: 1px solid transparent;
  border-radius: 4px;
  background-color: transparent;
}
.volume-options button[aria-selected="true"] { border-color: var(--border); }
.volume-options button:hover, .volume-options button:focus-visible {
  background-color: var(--accent-quiet);
  outline-offset: -2px;
}
</style>
