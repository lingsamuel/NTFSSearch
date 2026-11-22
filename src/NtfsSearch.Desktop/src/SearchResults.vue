<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import type { FileAction, SearchRow, SortField } from './desktop'
import { fileExtension, fileTypeIcon, setVisibleFileTypes } from './file-type-icons'

const props = defineProps<{
  rows: SearchRow[]
  sort: SortField
  descending: boolean
  pending: boolean
  emptyMessage: string
}>()
const selected = defineModel<string | null>('selectedKey', { required: true })
const emit = defineEmits<{
  sort: [field: SortField]
  action: [type: FileAction, row: SearchRow]
}>()

const columns = [
  { field: 0, name: '名称', css: 'name' },
  { field: 1, name: '完整路径', css: 'path' },
  { field: 2, name: '大小', css: 'size' },
  { field: 3, name: '修改时间', css: 'modified' },
] as const
const actions = [
  { type: 'open', label: '打开', shortcut: 'Enter' },
  { type: 'reveal', label: '打开所在目录' },
  { type: 'copy', label: '复制完整路径', shortcut: 'Ctrl+C' },
] as const
const scroller = ref<HTMLElement>()
const grid = ref<HTMLTableElement>()
const menuElement = ref<HTMLDivElement>()
const menu = shallowRef<{ key: string; left: number; top: number }>()
const columnWidths = shallowRef<number[]>()
const columnDrag = shallowRef<{ field: SortField; pointer: number; startX: number; widths: number[] }>()
const rowExtensions = computed(() => props.rows.map(fileExtension))
const rowParts = computed(() => props.rows.map(row => ({
  name: textParts(row.name, row.matchRanges, row.fullPath.length - row.name.length),
  path: textParts(row.fullPath, row.matchRanges, 0),
})))
const visibleRows = new Set<HTMLTableRowElement>()
let iconObserver: IntersectionObserver | undefined

function textParts(value: string, ranges: number[], offset: number) {
  const parts: { text: string; matched: boolean }[] = []
  let cursor = 0
  for (let index = 0; index < ranges.length; index += 2) {
    const start = Math.max(0, ranges[index] - offset)
    const end = ranges[index + 1] - offset
    if (end <= start) continue
    if (start > cursor) parts.push({ text: value.slice(cursor, start), matched: false })
    parts.push({ text: value.slice(start, end), matched: true })
    cursor = end
  }
  if (cursor < value.length) parts.push({ text: value.slice(cursor), matched: false })
  return parts
}

function scrollHorizontally(event: WheelEvent) {
  if (!event.shiftKey || event.ctrlKey || event.deltaX !== 0 || event.deltaY === 0) return
  const area = event.currentTarget as HTMLElement
  let distance = event.deltaY
  if (event.deltaMode === WheelEvent.DOM_DELTA_LINE) distance *= 29
  if (event.deltaMode === WheelEvent.DOM_DELTA_PAGE) distance *= area.clientWidth
  event.preventDefault()
  area.scrollLeft += distance
}

function observeFileTypes() {
  if (!iconObserver) return
  iconObserver.takeRecords()
  iconObserver.disconnect()
  visibleRows.clear()
  setVisibleFileTypes(new Set())
  for (const row of grid.value?.tBodies[0]?.rows ?? []) {
    if (row.dataset.fileExtension) iconObserver.observe(row)
  }
}

function requestVisibleFileTypes(entries: IntersectionObserverEntry[]) {
  for (const entry of entries) {
    const row = entry.target as HTMLTableRowElement
    if (entry.isIntersecting) visibleRows.add(row)
    else visibleRows.delete(row)
  }
  const extensions = new Set<string>()
  for (const row of visibleRows) {
    const extension = row.dataset.fileExtension
    if (extension) extensions.add(extension)
  }
  setVisibleFileTypes(extensions)
}

function startColumnResize(event: PointerEvent, field: SortField) {
  if (event.button !== 0 || !event.isPrimary || !grid.value?.tHead) return
  const widths = Array.from(grid.value.tHead.rows[0].cells, cell => cell.offsetWidth)
  columnDrag.value = { field, pointer: event.pointerId, startX: event.clientX, widths }
  const handle = event.currentTarget as HTMLElement
  handle.setPointerCapture(event.pointerId)
}

function resizeColumn(event: PointerEvent) {
  const drag = columnDrag.value
  if (!drag || event.pointerId !== drag.pointer) return
  // 第一拖以实际列宽为起点，锁住其余列，避免表格重新分配列宽。
  const widths = drag.widths.slice()
  widths[drag.field] = Math.max(64, Math.round(widths[drag.field] + event.clientX - drag.startX))
  columnWidths.value = widths
}

function finishColumnResize(event: PointerEvent) {
  if (columnDrag.value?.pointer === event.pointerId) columnDrag.value = undefined
}


function scrollToStart() {
  scroller.value?.scrollTo({ top: 0 })
  closeMenu()
}

function focusSelection() {
  grid.value?.focus({ preventScroll: true })
  if (!selected.value && props.rows.length) choose(0)
}

function choose(index: number) {
  const row = props.rows[index]
  if (!row) return
  selected.value = row.key
  grid.value?.tBodies[0]?.rows[index]?.scrollIntoView({ block: 'nearest' })
}

function clickRow(row: SearchRow) {
  selected.value = row.key
  grid.value?.focus({ preventScroll: true })
}

function runAction(type: FileAction, key: string | null) {
  const row = props.rows.find(row => row.key === key)
  if (row && !props.pending) emit('action', type, row)
  if (menu.value) {
    closeMenu()
    grid.value?.focus({ preventScroll: true })
  }
}

async function openMenu(row: SearchRow, x: number, y: number) {
  if (props.pending) return
  selected.value = row.key
  menu.value = { key: row.key, left: x, top: y }
  await nextTick()
  const element = menuElement.value
  if (!element || !menu.value) return
  menu.value = {
    key: row.key,
    left: Math.max(4, Math.min(x, window.innerWidth - element.offsetWidth - 4)),
    top: Math.max(4, Math.min(y, window.innerHeight - element.offsetHeight - 4)),
  }
  element.querySelector('button')?.focus({ preventScroll: true })
}

function closeMenu() {
  menu.value = undefined
}

function handleMenuKey(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    closeMenu()
    grid.value?.focus()
    event.preventDefault()
  }
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    const buttons = Array.from(menuElement.value?.querySelectorAll('button') ?? [])
    const index = buttons.findIndex(button => button === document.activeElement)
    if (buttons.length) buttons[(index + buttons.length + (event.key === 'ArrowDown' ? 1 : -1)) % buttons.length].focus()
    event.preventDefault()
  }
}

function handleGridKey(event: KeyboardEvent) {
  if (event.target !== grid.value) return
  const count = props.rows.length
  if (count === 0) return
  const index = props.rows.findIndex(row => row.key === selected.value)
  switch (event.key) {
    case 'ArrowDown': choose(Math.min(count - 1, index + 1)); break
    case 'ArrowUp': choose(Math.max(0, index - 1)); break
    case 'Home': choose(0); break
    case 'End': choose(count - 1); break
    case 'Enter': runAction('open', selected.value); break
    case 'c':
    case 'C':
      if (!event.ctrlKey) return
      runAction('copy', selected.value)
      break
    case 'F10':
    case 'ContextMenu': {
      if (event.key === 'F10' && !event.shiftKey) return
      const rect = grid.value?.tBodies[0]?.rows[index]?.getBoundingClientRect()
      if (rect) void openMenu(props.rows[index], rect.left + 24, rect.bottom)
      break
    }
    default: return
  }
  event.preventDefault()
}

watch(rowExtensions, observeFileTypes, { flush: 'post' })
onMounted(() => {
  window.addEventListener('pointerdown', closeMenu)
  iconObserver = new IntersectionObserver(requestVisibleFileTypes, { root: scroller.value, rootMargin: '58px 0px' })
  observeFileTypes()
})
onBeforeUnmount(() => {
  window.removeEventListener('pointerdown', closeMenu)
  iconObserver?.disconnect()
  setVisibleFileTypes(new Set())
})
defineExpose({ focusSelection, scrollToStart })
</script>

<template>
  <section class="results-panel" aria-label="搜索结果">
    <div ref="scroller" class="results-scroll" @scroll="closeMenu" @wheel="scrollHorizontally">
      <table ref="grid" class="results-table" :class="{ resizing: !!columnDrag }"
        :style="columnWidths ? { width: columnWidths.reduce((total, width) => total + width, 0) + 'px' } : undefined"
        role="grid" aria-label="搜索结果" :aria-busy="pending" tabindex="0"
        :aria-activedescendant="selected ? 'result-' + rows.findIndex(row => row.key === selected) : undefined"
        @keydown="handleGridKey">
        <colgroup>
          <col v-for="column in columns" :key="column.field" :class="'column-' + column.css"
            :style="columnWidths ? { width: columnWidths[column.field] + 'px' } : undefined">
        </colgroup>
        <thead>
          <tr>
            <th v-for="column in columns" :key="column.field" scope="col" :class="{ sorted: sort === column.field }"
              :aria-sort="sort === column.field ? (descending ? 'descending' : 'ascending') : undefined">
              <button type="button" @click="emit('sort', column.field)">
                <span class="column-label">{{ column.name }}</span>
                <span v-if="sort === column.field" class="sort-caret" :class="{ descending }" aria-hidden="true"></span>
              </button>
              <span class="column-resizer" aria-hidden="true" @pointerdown.prevent="startColumnResize($event, column.field)"
                @pointermove="resizeColumn" @lostpointercapture="finishColumnResize"></span>
            </th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(row, index) in rows" :id="'result-' + index" :key="row.key" :aria-selected="selected === row.key"
            :data-file-extension="rowExtensions[index]"
            @click="clickRow(row)" @dblclick="runAction('open', row.key)"
            @contextmenu.prevent="openMenu(row, $event.clientX, $event.clientY)">
            <td class="entry-name" :title="row.name">
              <img v-if="fileTypeIcon(rowExtensions[index])" :src="fileTypeIcon(rowExtensions[index]) ?? undefined"
                class="entry-file-type-icon" alt="">
              <span v-else class="entry-icon" :class="{ folder: row.isDirectory }" aria-hidden="true"></span>
              <template v-for="(part, partIndex) in rowParts[index].name" :key="partIndex">
                <strong v-if="part.matched">{{ part.text }}</strong><template v-else>{{ part.text }}</template>
              </template>
            </td>
            <td class="entry-path" :title="row.fullPath">
              <template v-for="(part, partIndex) in rowParts[index].path" :key="partIndex">
                <strong v-if="part.matched">{{ part.text }}</strong><template v-else>{{ part.text }}</template>
              </template>
            </td>
            <td class="entry-size" :title="row.sizeBytesText ?? row.sizeHint ?? undefined">{{ row.sizeText }}</td>
            <td class="entry-date">{{ row.modifiedText }}</td>
          </tr>
        </tbody>
      </table>
    </div>
    <div v-if="!rows.length" class="empty-results">
      <slot />
      <p v-if="emptyMessage" role="status">{{ emptyMessage }}</p>
    </div>
    <div v-if="menu" ref="menuElement" class="file-menu" role="menu" aria-label="文件操作"
      :style="{ left: menu.left + 'px', top: menu.top + 'px' }" @pointerdown.stop @keydown="handleMenuKey"
      @focusout="(event) => { if (!menuElement?.contains(event.relatedTarget as Node)) closeMenu() }">
      <button v-for="action in actions" :key="action.type" type="button" role="menuitem" @click="runAction(action.type, menu.key)">
        {{ action.label }}
        <kbd v-if="'shortcut' in action">{{ action.shortcut }}</kbd>
      </button>
    </div>
  </section>
</template>
