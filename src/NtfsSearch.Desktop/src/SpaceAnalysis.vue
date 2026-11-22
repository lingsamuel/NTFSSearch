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
  details: [requestId: number, directoryIds: string[], manualDirectoryIds: string[], branchIds: string[]]
}>()

interface Tile {
  item: SpaceEntry
  key: string
  level: number
  detailed?: boolean
  preview?: boolean
  ancestors: SpaceEntry[]
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
const branch = ref<SpaceEntry[]>([])
const folderHeaderHeight = 24
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
const preview = computed(() => branch.value.at(-1))
const expandedIds = computed(() => new Set(branch.value.map(item => item.directoryId!)))
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
  const manual = new Set<string>()
  if (preview.value?.directoryId) manual.add(preview.value.directoryId)
  const current = folder.value
  if (current) {
    const area = { left: 0, top: 0, width: width.value, height: height.value }
    const roots = layoutSpaceItems(current.children, Number(current.bytes), area)
      .map(tile => ({ ...tile, key: itemKey(tile.item), level: 1, ancestors: [] }))
    tiles.push(...roots)
    for (const tile of roots) appendDetails(tile)
  }
  return { tiles, ids: [...requested], manualIds: [...manual] }

  function appendDetails(tile: Tile) {
    const id = tile.item.directoryId
    if (!id) return
    const expanded = expandedIds.value.has(id)
    if (expanded) manual.add(id)
    else {
      if (tile.level >= detailLevel.value || tile.width < 96 || tile.height < 64 || requested.size >= 32) return
      requested.add(id)
    }
    const children = props.result?.details[id]
    if (!children) return
    const area = {
      left: tile.left + 3, top: tile.top + folderHeaderHeight,
      width: tile.width - 6, height: tile.height - folderHeaderHeight - 3,
    }
    const nested = layoutSpaceItems(children, Number(tile.item.bytes), area)
    const minimum = expanded ? 6 : 12
    const ancestors = [...tile.ancestors, tile.item]
    for (const child of nested) {
      if (child.width < minimum || child.height < minimum) continue
      tile.detailed = true
      const next = { ...child, fraction: child.fraction * tile.fraction,
        key: tile.key + '/' + itemKey(child.item), level: tile.level + 1, preview: expanded, ancestors }
      tiles.push(next)
      appendDetails(next)
    }
  }
})

const overflowPreview = computed(() => {
  const id = preview.value?.directoryId
  if (!id) return []
  const children = props.result?.details[id]
  if (!children?.length) return []
  const rendered = new Set(drawing.value.tiles.filter(tile => tile.ancestors.at(-1)?.directoryId === id)
    .map(tile => itemKey(tile.item)))
  // 原块放不下子目录时，在当前视图内保留继续单击下探的入口。
  return children.filter(item => rendered.size === 0 || item.directoryId && !rendered.has(itemKey(item)))
})

const overflowPlacement = computed(() => {
  const tile = drawing.value.tiles.find(entry => entry.item.directoryId === preview.value?.directoryId)
  if (!tile) return undefined
  // 子项列表放到标题另一侧，第一击展开时仍能在同一位置识别第二击。
  const above = tile.top
  const below = height.value - tile.top - Math.min(tile.height, folderHeaderHeight)
  return {
    top: above > below ? '8px' : 'auto',
    bottom: above > below ? 'auto' : '8px',
    maxHeight: Math.max(0, Math.min(230, Math.max(above, below) - 12)) + 'px',
  }
})

const scene = computed(() => (props.result?.volumeKey ?? '') + '/' + (folder.value?.breadcrumbs.at(-1)?.id ?? ''))
watch(scene, () => {
  clearTimeout(clickTimer)
  branch.value = []
  hovered.value = focused.value = undefined
  clearFlashes()
})
watch(() => props.result, result => {
  let entries = result?.folder?.children
  for (const [index, item] of branch.value.entries()) {
    if (entries && !entries.some(entry => entry.directoryId === item.directoryId)
        || result?.details[item.directoryId!] === null) {
      branch.value = branch.value.slice(0, index)
      break
    }
    entries = result?.details[item.directoryId!] ?? undefined
  }
})
watch([() => props.result?.requestId, () => drawing.value.ids.join(','), () => drawing.value.manualIds.join(','),
  () => [...expandedIds.value].join(',')],
  ([requestId, ids, manualIds, branchIds], [previousId, previousIds, previousManualIds]) => {
  if (requestId !== undefined && (ids || manualIds || requestId === previousId && (previousIds || previousManualIds)))
    emit('details', requestId, ids ? ids.split(',') : [], manualIds ? manualIds.split(',') : [], branchIds ? branchIds.split(',') : [])
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
  const tileTop = (event.currentTarget as HTMLElement).getBoundingClientRect().top
  clickEntry(tile.item, tile.ancestors, event, event.clientY - tileTop < folderHeaderHeight)
}

function clickEntry(item: SpaceEntry, ancestors: SpaceEntry[], event: MouseEvent, stableTarget = false) {
  if (!item.directoryId) {
    if (item.key) emit('reveal', item.key)
    return
  }
  if (event.detail > 1) return
  clearTimeout(clickTimer)
  const toggle = () => {
    branch.value = preview.value?.directoryId === item.directoryId ? ancestors : [...ancestors, item]
  }
  // 标题区始终留给父目录。点击内部空白时等待双击，防止新子块盖住第二次点击。
  if (event.detail === 0 || stableTarget) toggle()
  else clickTimer = setTimeout(toggle, (props.result?.doubleClickMilliseconds ?? 500) + 16)
}

function enterFolder(item: SpaceEntry) {
  if (!item.directoryId) return
  clearTimeout(clickTimer)
  emit('view', props.root, item.directoryId)
}

function collapsePreview() {
  clearTimeout(clickTimer)
  branch.value = branch.value.slice(0, -1)
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
          :class="{ directory: tile.item.directoryId, others: !tile.item.key, nested: tile.level > 1, preview: tile.preview,
            expanded: tile.item.directoryId && expandedIds.has(tile.item.directoryId) }"
          :aria-disabled="!tile.item.key"
          :aria-expanded="tile.item.directoryId ? expandedIds.has(tile.item.directoryId) : undefined"
          :title="title(tile)" :aria-label="title(tile)" :style="{
            left: tile.left + 'px', top: tile.top + 'px', width: tile.width + 'px', height: tile.height + 'px',
          }" @mouseenter="hovered = tile.key" @mouseleave="hovered = undefined"
          @focus="focused = tile.key" @blur="focused = undefined" @click="clickTile(tile, $event)" @dblclick="enterFolder(tile.item)"
          @keydown.enter="tile.item.directoryId && ($event.preventDefault(), enterFolder(tile.item))">
          <template v-if="tile.width >= 32 && tile.height >= 23">
            <span class="space-tile-name">
              <span>{{ tile.item.name }}</span>
            </span>
            <small v-if="tile.height >= 42 && tile.width >= 47 && !tile.detailed">{{ tile.item.sizeText }}</small>
          </template>
        </button>
        <template v-for="tile in drawing.tiles" :key="'flash/' + tile.key">
          <div v-if="tile.level === 1 && tile.item.directoryId && flashTokens[tile.item.directoryId]"
            :key="flashTokens[tile.item.directoryId]" class="space-flash" aria-hidden="true"
            :style="{ left: tile.left + 'px', top: tile.top + 'px', width: tile.width + 'px', height: tile.height + 'px' }"></div>
        </template>
        <section v-if="preview && overflowPreview.length" class="space-preview-overflow" :aria-label="preview.name"
          :style="overflowPlacement">
          <header>
            <strong>{{ preview.name }}</strong>
            <button type="button" class="outlined-button" @click="collapsePreview">收起一层</button>
          </header>
          <button v-for="item in overflowPreview" :key="itemKey(item)" type="button" class="space-preview-entry"
            :data-directory-id="item.directoryId" :disabled="!item.key" :title="item.name"
            @click="clickEntry(item, branch, $event)" @dblclick="enterFolder(item)"
            @keydown.enter="item.directoryId && ($event.preventDefault(), enterFolder(item))">
            <span>{{ item.name }}</span><small>{{ item.sizeText }}</small>
          </button>
        </section>
      </template>
    </div>
    <footer class="space-details">
      <span v-if="preview && !pending && !error" class="space-preview-status" role="status" :title="preview.name">
        临时展开 · {{ preview.name }}
      </span>
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
.space-tile.expanded { box-shadow: inset 0 0 0 2px var(--accent); background: #c5d7ff; }
.space-tile.preview:not(.directory):not(.others) { background: #c3e6dd; }
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
.space-tile-name, .space-tile small { margin: 0 6px; pointer-events: none; }
.space-tile-name { display: flex; align-items: center; gap: 3px; margin-top: 4px; }
.space-preview-overflow {
  position: absolute; right: 8px; bottom: 8px; z-index: 3;
  width: min(360px, calc(100% - 16px)); max-height: min(60%, 230px); overflow: auto;
  background: var(--surface); border: 1px solid var(--border); border-radius: 6px;
  box-shadow: 0 2px 10px #19253833;
}
.space-preview-overflow header, .space-preview-entry {
  display: flex; gap: 8px; align-items: center; justify-content: space-between; padding: 5px 8px;
}
.space-preview-overflow header { position: sticky; top: 0; background: var(--chrome); }
.space-preview-overflow strong, .space-preview-entry span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.space-preview-overflow header button, .space-preview-entry small { flex: none; }
.space-preview-entry { width: 100%; border: 0; background: var(--surface); text-align: left; }
.space-preview-entry:hover { background: var(--accent-quiet); }
.space-tile small { font-weight: 400; font-variant-numeric: tabular-nums; }
.space-details { display: flex; align-items: center; gap: 10px; padding: 0 16px; min-width: 0; color: var(--muted); }
.space-preview-status { flex: 0 1 auto; max-width: 55%; padding: 2px 7px; border-radius: 4px; background: var(--accent-quiet); color: var(--accent); }
.space-unknown { margin-left: auto; color: var(--warning); }
</style>
