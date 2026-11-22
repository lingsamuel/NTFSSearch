<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { formatCount, type IndexStatus, type SpaceAnalysisResult, type SpaceEntry } from './desktop'
import { layoutSpaceItems } from './space-treemap'

const props = defineProps<{
  result?: SpaceAnalysisResult
  root: string
  pending: boolean
  error: string
  pathPending: boolean
  pathError: string
  indexStatus?: IndexStatus
}>()
const emit = defineEmits<{
  view: [root: string, directoryId?: string]
  path: [value: string]
  close: []
  reveal: [key: string]
  details: [requestId: number, directoryIds: string[]]
}>()

interface Tile {
  item: SpaceEntry
  key: string
  level: number
  detailed?: boolean
  fraction: number
  left: number
  top: number
  width: number
  height: number
}

const map = ref<HTMLDivElement>()
const back = ref<HTMLButtonElement>()
const address = ref('')
const addressEdited = ref(false)
const width = ref(0)
const height = ref(0)
const hovered = ref<string>()
const focused = ref<string>()
const detailLevel = ref(2)
const expanded = ref<string>()
const flashTokens = ref<Record<string, number>>({})
const flashTimers = new Map<string, ReturnType<typeof setTimeout>>()
let flashSequence = 0
let clickTimer: ReturnType<typeof setTimeout> | undefined
const formatter = new Intl.NumberFormat('zh-CN', { style: 'percent', maximumFractionDigits: 2 })
const folder = computed(() => props.result?.folder)
const selectedRoot = computed({
  get: () => props.root,
  set: root => emit('view', root),
})
const active = computed(() => drawing.value.tiles.find(tile => tile.key === (hovered.value ?? focused.value)))
let observer: ResizeObserver | undefined

watch(() => props.pending, pending => {
  if (pending) addressEdited.value = false
})
watch([() => props.result?.requestId, () => folder.value?.path], ([, path]) => {
  if (props.pathError || addressEdited.value) return
  address.value = path ?? ''
}, { immediate: true })

function openAddress() {
  addressEdited.value = false
  emit('path', address.value)
}

const drawing = computed(() => {
  const tiles: Tile[] = []
  const requested = new Set<string>()
  const current = folder.value
  if (current) {
    const area = { left: 0, top: 0, width: width.value, height: height.value }
    const roots = layoutSpaceItems(current.children, Number(current.bytes), area)
      .map(tile => ({ ...tile, key: itemKey(tile.item), level: 1 }))
    tiles.push(...roots)
    // 有意展开的分支优先取数据，自动展示使用余下的显示预算。
    const manual = roots.find(tile => tile.item.directoryId === expanded.value)
    if (manual) appendDetails(manual, detailLevel.value + 1, true)
    for (const tile of roots)
      if (tile !== manual) appendDetails(tile, detailLevel.value, false)
  }
  return { tiles, ids: [...requested] }

  function appendDetails(tile: Tile, limit: number, manual: boolean) {
    const id = tile.item.directoryId
    const headerHeight = 24
    if (!id || tile.level >= limit || tile.width < (manual ? 36 : 96) || tile.height < (manual ? 42 : 64)) return
    if (requested.size >= 32 && !requested.has(id)) return
    requested.add(id)
    const children = props.result?.details[id]
    if (!children) return
    const area = {
      left: tile.left + 3, top: tile.top + headerHeight,
      width: tile.width - 6, height: tile.height - headerHeight - 3,
    }
    const nested = layoutSpaceItems(children, Number(tile.item.bytes), area)
    for (const child of nested) {
      if (child.width < 12 || child.height < 12) continue
      tile.detailed = true
      const next = { ...child, fraction: child.fraction * tile.fraction,
        key: tile.key + '/' + itemKey(child.item), level: tile.level + 1 }
      tiles.push(next)
      appendDetails(next, limit, manual)
    }
  }
})

const scene = computed(() => (props.result?.volumeKey ?? '') + '/' + (folder.value?.breadcrumbs.at(-1)?.id ?? ''))
watch(scene, () => {
  clearTimeout(clickTimer)
  expanded.value = undefined
  hovered.value = focused.value = undefined
  clearFlashes()
})
watch(() => folder.value?.children, items => {
  if (expanded.value && !items?.some(item => item.directoryId === expanded.value)) expanded.value = undefined
})
watch([() => props.result?.requestId, () => drawing.value.ids.join(',')], ([requestId, ids], [previousId, previousIds]) => {
  if (requestId !== undefined && (ids || requestId === previousId && previousIds))
    emit('details', requestId, ids ? ids.split(',') : [])
})
watch(() => props.result, result => {
  for (const id of result?.flashes ?? []) {
    clearTimeout(flashTimers.get(id))
    flashTokens.value[id] = ++flashSequence
    flashTimers.set(id, setTimeout(() => {
      delete flashTokens.value[id]
      flashTimers.delete(id)
    }, 450))
  }
})

function clearFlashes() {
  flashTimers.forEach(timer => clearTimeout(timer))
  flashTimers.clear()
  flashTokens.value = {}
}

const message = computed(() => {
  if (props.error) return '无法分析空间：' + props.error
  if (props.pending) return '正在汇总目录大小…'
  if (folder.value?.isEmpty) return '当前目录为空'
  if (folder.value?.bytes === '0') return '当前目录的已知逻辑大小为 0 B'
  if (folder.value) return ''
  if (props.indexStatus?.error || props.indexStatus?.volumes.some(volume => volume.hasError))
    return '暂无可用索引，可返回搜索查看索引状态'
  if (props.indexStatus?.collectionCancelled) return '采集已取消，暂无可用索引'
  if (props.indexStatus?.discoveryComplete && !props.indexStatus.volumes.length) return '未发现可用的 NTFS 磁盘'
  return '正在等待磁盘索引'
})

function itemKey(item: SpaceEntry) {
  return item.directoryId ?? item.key ?? 'other'
}

function title(tile: Tile) {
  const { item, fraction } = tile
  const name = item.key ? item.name : '其他 ' + formatCount(item.count) + ' 项'
  return name + ' · ' + item.sizeBytesText + ' · ' + formatter.format(fraction)
}

function clickTile(tile: Tile, event: MouseEvent) {
  const id = tile.item.directoryId
  if (!id) {
    if (tile.item.key) emit('reveal', tile.item.key)
    return
  }
  if (tile.level !== 1 || event.detail > 1) return
  clearTimeout(clickTimer)
  const toggle = () => { expanded.value = expanded.value === id ? undefined : id }
  // 等系统双击间隔结束再铺子块，否则第二次点击可能落在刚出现的子块上。
  if (event.detail === 0) toggle()
  else clickTimer = setTimeout(toggle, (props.result?.doubleClickMilliseconds ?? 500) + 16)
}

function enterTile(tile: Tile) {
  if (!tile.item.directoryId) return
  clearTimeout(clickTimer)
  emit('view', props.root, tile.item.directoryId)
}

function detailShortcut(event: KeyboardEvent) {
  if (!event.ctrlKey || event.target instanceof HTMLInputElement) return
  if (event.key !== '+' && event.key !== '=' && event.key !== '-') return
  event.preventDefault()
  detailLevel.value = Math.max(1, Math.min(3, detailLevel.value + (event.key === '-' ? -1 : 1)))
}

onMounted(() => {
  observer = new ResizeObserver(([entry]) => {
    width.value = entry.contentRect.width
    height.value = entry.contentRect.height
  })
  if (map.value) observer.observe(map.value)
  back.value?.focus()
})
onBeforeUnmount(() => {
  observer?.disconnect()
  clearTimeout(clickTimer)
  clearFlashes()
})
</script>

<template>
  <section class="space-workspace" aria-label="空间分析" @keydown="detailShortcut">
    <header class="space-toolbar">
      <button ref="back" type="button" class="outlined-button" aria-label="返回搜索" @click="emit('close')">返回搜索</button>
      <select v-model="selectedRoot" class="scope-select" aria-label="分析磁盘" :disabled="!result?.roots.length">
        <option v-if="!result?.roots.length" value="">等待索引</option>
        <option v-for="drive in result?.roots" :key="drive" :value="drive">{{ drive }}</option>
      </select>
      <select v-model.number="detailLevel" class="scope-select space-level" aria-label="细节层级">
        <option v-for="level in 3" :key="level" :value="level">细节 {{ level }} 层</option>
      </select>
      <span v-if="folder && !pending" class="space-total" :title="folder.sizeBytesText">逻辑大小 <strong>{{ folder.sizeText }}</strong></span>
    </header>
    <form class="space-address" @submit.prevent="openAddress">
      <div class="space-address-field">
        <input v-model="address" type="text" aria-label="目录路径" placeholder="输入本地目录路径"
          autocomplete="off" spellcheck="false" :aria-invalid="!!pathError"
          :aria-describedby="pathError || pathPending ? 'space-path-feedback' : undefined" @input="addressEdited = true">
        <button type="submit" class="outlined-button" :disabled="!address.trim()">打开</button>
      </div>
      <div v-if="pathError || pathPending" id="space-path-feedback" class="space-path-feedback" :role="pathError ? 'alert' : 'status'">
        {{ pathError || '正在定位目录…' }}
      </div>
    </form>
    <nav class="space-breadcrumb" aria-label="当前目录">
      <template v-for="(crumb, position) in pending ? [] : folder?.breadcrumbs" :key="crumb.id">
        <span v-if="position > 0" aria-hidden="true">›</span>
        <span v-if="position === (folder?.breadcrumbs.length ?? 0) - 1" aria-current="location">{{ crumb.name }}</span>
        <button v-else type="button" :title="crumb.name" @click="emit('view', root, crumb.id)">{{ crumb.name }}</button>
      </template>
    </nav>
    <div ref="map" class="space-map" :aria-busy="pending">
      <div v-if="message" class="space-message" :role="error ? 'alert' : 'status'">
        <span>{{ message }}</span>
        <button v-if="error && root" type="button" class="outlined-button"
          @click="emit('view', root, folder?.breadcrumbs.at(-1)?.id)">重试</button>
      </div>
      <template v-else>
        <button v-for="tile in drawing.tiles" :key="tile.key" type="button" class="space-tile"
          :class="{ directory: tile.item.directoryId, others: !tile.item.key, nested: tile.level > 1,
            expanded: tile.level === 1 && !!tile.item.directoryId && tile.item.directoryId === expanded }"
          :aria-disabled="!tile.item.key"
          :aria-expanded="tile.level === 1 && tile.item.directoryId ? tile.item.directoryId === expanded : undefined"
          :title="title(tile)" :aria-label="title(tile)" :style="{
            left: tile.left + 'px', top: tile.top + 'px', width: tile.width + 'px', height: tile.height + 'px',
          }" @mouseenter="hovered = tile.key" @mouseleave="hovered = undefined"
          @focus="focused = tile.key" @blur="focused = undefined" @click="clickTile(tile, $event)" @dblclick="enterTile(tile)"
          @keydown.enter="tile.item.directoryId && ($event.preventDefault(), enterTile(tile))">
          <template v-if="tile.width >= 32 && tile.height >= 23">
            <span>{{ tile.item.name }}</span>
            <small v-if="tile.height >= 42 && tile.width >= 47 && !tile.detailed">{{ tile.item.sizeText }}</small>
          </template>
        </button>
        <template v-for="tile in drawing.tiles" :key="'flash/' + tile.key">
          <div v-if="tile.level === 1 && tile.item.directoryId && flashTokens[tile.item.directoryId]"
            :key="flashTokens[tile.item.directoryId]" class="space-flash" aria-hidden="true"
            :style="{ left: tile.left + 'px', top: tile.top + 'px', width: tile.width + 'px', height: tile.height + 'px' }"></div>
        </template>
      </template>
    </div>
    <footer class="space-details">
      <span v-if="active && !pending && !error" :title="title(active)">{{ title(active) }}</span>
      <span v-if="folder?.unknownFiles && !pending" class="space-unknown">
        {{ formatCount(folder.unknownFiles) }} 个文件的大小未知
      </span>
    </footer>
  </section>
</template>

<style scoped>
.space-workspace {
  grid-row: 1 / -1;
  display: grid;
  grid-template-rows: auto auto 36px minmax(0, 1fr) 34px;
  min-width: 0;
  min-height: 0;
}

.space-toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 13px 16px;
  border-bottom: 1px solid var(--border);
  background: var(--chrome);
}
.space-toolbar button { height: 39px; white-space: nowrap; }
.space-level { min-width: 94px; }
.space-total { margin-left: auto; white-space: nowrap; color: var(--muted); }
.space-total strong { margin-left: 6px; color: #192538; font-variant-numeric: tabular-nums; }

.space-address { padding: 8px 16px 0; }
.space-address-field { display: flex; gap: 8px; }
.space-address-field input {
  flex: 1;
  min-width: 0;
  padding: 5px 9px;
  border: 1px solid #c2ccdc;
  border-radius: 6px;
  background: var(--surface);
}
.space-address-field input:focus { outline: 1px solid var(--accent); }
.space-address-field button { flex: none; }
.space-path-feedback { margin-top: 4px; color: var(--muted); }
.space-path-feedback[role="alert"] { color: var(--warning); }

.space-breadcrumb {
  display: flex;
  align-items: center;
  gap: 7px;
  min-width: 0;
  padding: 0 16px;
  overflow-x: auto;
  white-space: nowrap;
}
.space-breadcrumb button { padding: 2px 4px; border: 0; border-radius: 4px; color: var(--accent); background: transparent; }
.space-breadcrumb button:hover { background: var(--accent-quiet); }
.space-breadcrumb [aria-current] { font-weight: 600; }

.space-map { position: relative; margin: 0 16px; overflow: hidden; background: #f5f7fb; border-radius: 6px; }
.space-message { height: 100%; display: grid; place-content: center; justify-items: center; gap: 9px; color: var(--muted); }
.space-message[role="alert"] { color: var(--warning); }
.space-tile {
  position: absolute;
  display: grid;
  align-content: start;
  gap: 1px;
  min-width: 0;
  padding: 0;
  overflow: hidden;
  border: 0;
  border-radius: 3px;
  background: #dcece8;
  box-shadow: inset 0 0 0 1px #f5f7fb;
  color: #1f4942;
  text-align: left;
}
.space-tile.directory { background: #dce6ff; color: #223e84; font-weight: 600; }
.space-tile.directory.nested { background: #cddcff; }
.space-tile.expanded { box-shadow: inset 0 0 0 2px var(--accent); }
.space-tile.others { background: #e6ebf3; color: #4e5b71; cursor: default; }
.space-tile:hover { filter: brightness(.96); }
.space-tile:focus-visible { outline-offset: -2px; }
.space-flash {
  position: absolute;
  z-index: 2;
  pointer-events: none;
  border-radius: 3px;
  box-shadow: inset 0 0 0 3px #e89e20;
  background: #ffd88055;
  animation: space-activity 450ms ease-out both;
}
@keyframes space-activity { from { opacity: 1; } to { opacity: 0; } }
@media (prefers-reduced-motion: reduce) { .space-flash { animation: none; } }
.space-tile span, .space-tile small, .space-details span { overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
.space-tile span, .space-tile small { margin: 0 6px; }
.space-tile span { margin-top: 4px; }
.space-tile small { font-weight: 400; font-variant-numeric: tabular-nums; }
.space-details { display: flex; align-items: center; gap: 10px; padding: 0 16px; min-width: 0; color: var(--muted); }
.space-unknown { margin-left: auto; color: var(--warning); }
</style>
