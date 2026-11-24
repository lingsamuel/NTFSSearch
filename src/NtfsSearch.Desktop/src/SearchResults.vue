<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { fileMenuActions, type FileAction, type SearchRow, type SortField } from './desktop'
import { fileExtension, fileTypeIcon, setVisibleFileTypes } from './file-type-icons'
import { t } from './localization'

const props = defineProps<{
  rows: SearchRow[]
  sort: SortField
  descending: boolean
  pending: boolean
  emptyMessage: string
  indexFileSize: boolean
  indexModifiedTime: boolean
}>()
const emit = defineEmits<{
  sort: [field: SortField]
  action: [type: FileAction, row: SearchRow]
  drag: [keys: string[]]
}>()

const columns = [
  { field: 0, label: 'search.nameColumn', css: 'name' },
  { field: 1, label: 'search.pathColumn', css: 'path' },
  { field: 2, label: 'search.sizeColumn', css: 'size' },
  { field: 3, label: 'search.modifiedColumn', css: 'modified' },
] as const
const visibleColumns = computed(() => columns.filter(({ field }) =>
  (field !== 2 || props.indexFileSize) && (field !== 3 || props.indexModifiedTime)))
const scroller = ref<HTMLElement>()
const grid = ref<HTMLTableElement>()
const menuElement = ref<HTMLDivElement>()
const menu = shallowRef<{ key: string; left: number; top: number }>()
const columnWidths = shallowRef<number[]>()
const columnDrag = shallowRef<{ index: number; pointer: number; startX: number; widths: number[] }>()
const selected = shallowRef(new Set<string>())
const activeKey = ref<string | null>(null)
const activeIndex = computed(() => props.rows.findIndex(row => row.key === activeKey.value))
const selectionBox = shallowRef<{ left: number; top: number; width: number; height: number }>()
let selectionAnchor: string | null = null
let resultDrag: {
  pointer: number
  startX: number
  startY: number
  contentX: number
  contentY: number
  rowKey?: string
  previous: Set<string>
  x: number
  y: number
} | undefined
let scrollFrame = 0
let suppressDragClick = false
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

function startColumnResize(event: PointerEvent, index: number) {
  if (event.button !== 0 || !event.isPrimary || !grid.value?.tHead) return
  const widths = Array.from(grid.value.tHead.rows[0].cells, cell => cell.offsetWidth)
  columnDrag.value = { index, pointer: event.pointerId, startX: event.clientX, widths }
  const handle = event.currentTarget as HTMLElement
  handle.setPointerCapture(event.pointerId)
}

function resizeColumn(event: PointerEvent) {
  const drag = columnDrag.value
  if (!drag || event.pointerId !== drag.pointer) return
  // 第一拖以实际列宽为起点，锁住其余列，避免表格重新分配列宽。
  const widths = drag.widths.slice()
  widths[drag.index] = Math.max(64, Math.round(widths[drag.index] + event.clientX - drag.startX))
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
  if (activeIndex.value < 0 && props.rows.length) choose(0)
}

function choose(index: number, range = false) {
  const row = props.rows[index]
  if (!row) return
  selectRow(row.key, range)
  grid.value?.tBodies[0]?.rows[index]?.scrollIntoView({ block: 'nearest' })
}

function selectRow(key: string, range = false, toggle = false) {
  activeKey.value = key
  const anchor = range ? props.rows.findIndex(row => row.key === selectionAnchor) : -1
  if (anchor >= 0) {
    const index = activeIndex.value
    selected.value = new Set(props.rows.slice(Math.min(anchor, index), Math.max(anchor, index) + 1).map(row => row.key))
    return
  }
  selectionAnchor = key
  const keys = toggle ? new Set(selected.value) : new Set<string>()
  if (keys.has(key)) keys.delete(key)
  else keys.add(key)
  selected.value = keys
}

function clickRow(event: MouseEvent, row: SearchRow) {
  selectRow(row.key, event.shiftKey, event.ctrlKey)
  grid.value?.focus({ preventScroll: true })
}

function clearSelection() {
  finishResultDrag(true)
  selected.value = new Set()
  activeKey.value = null
  selectionAnchor = null
}

function startResultDrag(event: PointerEvent) {
  const area = scroller.value
  const body = grid.value?.tBodies[0]
  const target = event.target
  suppressDragClick = false
  if (!area || !(target instanceof Element) || event.pointerType !== 'mouse' || event.button !== 0) return
  if (props.pending || !props.rows.length || (target !== area && !body?.contains(target))) return
  const rect = area.getBoundingClientRect()
  if (event.clientX >= rect.left + area.clientWidth || event.clientY >= rect.top + area.clientHeight) return
  const name = target.closest('.entry-drag-source')
  const row = name ? props.rows[(name.closest('tr') as HTMLTableRowElement).sectionRowIndex] : undefined
  resultDrag = {
    pointer: event.pointerId, startX: event.clientX, startY: event.clientY,
    contentX: event.clientX - rect.left + area.scrollLeft,
    contentY: event.clientY - rect.top + area.scrollTop,
    rowKey: row?.key, previous: selected.value, x: event.clientX, y: event.clientY,
  }
}

function moveResultDrag(event: PointerEvent) {
  const drag = resultDrag
  const area = scroller.value
  if (!drag || !area || event.pointerId !== drag.pointer) return
  if (!(event.buttons & 1)) return finishResultDrag(false)
  drag.x = event.clientX
  drag.y = event.clientY
  if (!selectionBox.value) {
    if (Math.max(Math.abs(drag.x - drag.startX), Math.abs(drag.y - drag.startY)) < 4) return
    suppressDragClick = true
    grid.value?.focus({ preventScroll: true })
    if (drag.rowKey !== undefined) {
      resultDrag = undefined
      if (!props.rows.some(row => row.key === drag.rowKey)) return
      if (!selected.value.has(drag.rowKey)) selectRow(drag.rowKey)
      emit('drag', props.rows.filter(row => selected.value.has(row.key)).map(row => row.key))
      return
    }
    area.setPointerCapture(event.pointerId)
  }
  event.preventDefault()
  updateSelectionBox()
}

function updateSelectionBox() {
  const drag = resultDrag
  const area = scroller.value
  const body = grid.value?.tBodies[0]
  if (!drag || !area || !body) return
  const viewport = area.getBoundingClientRect()
  const header = grid.value?.tHead?.offsetHeight ?? 0
  const width = area.clientWidth
  const height = area.clientHeight
  const x = Math.max(0, Math.min(width, drag.x - viewport.left))
  const y = Math.max(header, Math.min(height, drag.y - viewport.top))
  const startX = drag.contentX - area.scrollLeft
  const startY = drag.contentY - area.scrollTop
  const left = Math.min(x, startX)
  const right = Math.max(x, startX)
  const top = Math.min(y, startY)
  const bottom = Math.max(y, startY)
  const visibleTop = Math.max(header, top)
  const visibleLeft = Math.max(0, left)
  selectionBox.value = {
    left: visibleLeft, top: visibleTop,
    width: Math.max(1, Math.min(width, right) - visibleLeft),
    height: Math.max(1, Math.min(height, bottom) - visibleTop),
  }
  const keys = new Set<string>()
  for (const row of body.rows) {
    const bounds = row.getBoundingClientRect()
    if (bounds.bottom >= viewport.top + top && bounds.top <= viewport.top + bottom
      && bounds.right >= viewport.left + left && bounds.left <= viewport.left + right) {
      const item = props.rows[row.sectionRowIndex]
      if (item) keys.add(item.key)
    }
  }
  selected.value = keys
  const edge = 24
  const distance = drag.y - viewport.top
  const above = header + edge - distance
  const below = distance - height + edge
  const speed = above > 0 ? -above : Math.max(0, below)
  autoScrollSelection(Math.max(-20, Math.min(20, speed)))
}

function autoScrollSelection(speed: number) {
  cancelAnimationFrame(scrollFrame)
  if (!speed) return
  scrollFrame = requestAnimationFrame(() => {
    const area = scroller.value
    if (!area) return
    const previousTop = area.scrollTop
    area.scrollTop += Math.sign(speed) * Math.ceil(Math.abs(speed) / 2)
    if (area.scrollTop !== previousTop) updateSelectionBox()
  })
}

function finishResultDrag(cancelled: boolean) {
  const drag = resultDrag
  const area = scroller.value
  resultDrag = undefined
  if (!drag || !selectionBox.value) return
  selectionBox.value = undefined
  autoScrollSelection(0)
  if (area?.hasPointerCapture(drag.pointer)) area.releasePointerCapture(drag.pointer)
  if (cancelled) {
    selected.value = new Set(props.rows.filter(row => drag.previous.has(row.key)).map(row => row.key))
    return
  }
  // 框选的起止方向决定后续键盘操作使用哪一端。
  const keys = [...selected.value]
  const down = drag.y - (area?.getBoundingClientRect().top ?? 0) + (area?.scrollTop ?? 0) >= drag.contentY
  activeKey.value = (down ? keys.at(-1) : keys[0]) ?? null
  selectionAnchor = (down ? keys[0] : keys.at(-1)) ?? null
}

function endResultPointer(event: PointerEvent) {
  if (event.pointerId === resultDrag?.pointer) finishResultDrag(event.type !== 'pointerup')
}

function blockDragClick(event: MouseEvent) {
  if (suppressDragClick && event.detail !== 0) {
    event.preventDefault()
    event.stopPropagation()
  }
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
  if (selected.value.has(row.key)) activeKey.value = row.key
  else selectRow(row.key)
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
  if (event.key === 'Escape' && selectionBox.value) {
    finishResultDrag(true)
    event.preventDefault()
    return
  }
  const count = props.rows.length
  if (count === 0) return
  const index = activeIndex.value
  switch (event.key) {
    case 'ArrowDown': choose(Math.min(count - 1, index + 1), event.shiftKey); break
    case 'ArrowUp': choose(Math.max(0, index - 1), event.shiftKey); break
    case 'Home': choose(0, event.shiftKey); break
    case 'End': choose(count - 1, event.shiftKey); break
    case 'Enter': runAction('open', activeKey.value); break
    case 'a':
    case 'A':
      if (!event.ctrlKey) return
      selected.value = new Set(props.rows.map(row => row.key))
      if (activeKey.value === null) activeKey.value = selectionAnchor = props.rows[0].key
      break
    case 'c':
    case 'C':
      if (!event.ctrlKey) return
      runAction('copy', activeKey.value)
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

function handleGridBlur() {
  if (selectionBox.value) finishResultDrag(true)
}

watch(visibleColumns, () => {
  columnDrag.value = undefined
  columnWidths.value = undefined
})
watch(() => props.rows, rows => {
  const keys = new Set(rows.map(row => row.key))
  selected.value = new Set([...selected.value].filter(key => keys.has(key)))
  if (activeKey.value !== null && !keys.has(activeKey.value)) activeKey.value = selected.value.values().next().value ?? null
  if (selectionAnchor !== null && !keys.has(selectionAnchor)) selectionAnchor = null
})
watch(rowExtensions, observeFileTypes, { flush: 'post' })
onMounted(() => {
  window.addEventListener('pointerdown', closeMenu)
  window.addEventListener('pointermove', moveResultDrag)
  window.addEventListener('pointerup', endResultPointer)
  window.addEventListener('pointercancel', endResultPointer)
  iconObserver = new IntersectionObserver(requestVisibleFileTypes, { root: scroller.value, rootMargin: '58px 0px' })
  observeFileTypes()
})
onBeforeUnmount(() => {
  window.removeEventListener('pointerdown', closeMenu)
  window.removeEventListener('pointermove', moveResultDrag)
  window.removeEventListener('pointerup', endResultPointer)
  window.removeEventListener('pointercancel', endResultPointer)
  finishResultDrag(true)
  iconObserver?.disconnect()
  setVisibleFileTypes(new Set())
})
defineExpose({ clearSelection, focusSelection, scrollToStart })
</script>

<template>
  <section class="results-panel" :aria-label="t('search.results')">
    <div ref="scroller" class="results-scroll" @scroll="closeMenu" @wheel="scrollHorizontally"
      @pointerdown="startResultDrag" @lostpointercapture="endResultPointer"
      @dragstart.prevent @click.capture="blockDragClick" @dblclick.capture="blockDragClick">
      <table ref="grid" class="results-table" :class="{ resizing: !!columnDrag }"
        :style="columnWidths ? { width: columnWidths.reduce((total, width) => total + width, 0) + 'px' } : undefined"
        role="grid" :aria-label="t('search.results')" :aria-busy="pending" aria-multiselectable="true" tabindex="0"
        :aria-activedescendant="activeIndex >= 0 ? 'result-' + activeIndex : undefined"
        @keydown="handleGridKey" @blur="handleGridBlur">
        <colgroup>
          <col v-for="(column, index) in visibleColumns" :key="column.field" :class="'column-' + column.css"
            :style="columnWidths ? { width: columnWidths[index] + 'px' } : undefined">
        </colgroup>
        <thead>
          <tr>
            <th v-for="(column, index) in visibleColumns" :key="column.field" scope="col" :class="{ sorted: sort === column.field }"
              :aria-sort="sort === column.field ? (descending ? 'descending' : 'ascending') : undefined">
              <button type="button" @click="emit('sort', column.field)">
                <span class="column-label">{{ t(column.label) }}</span>
                <span v-if="sort === column.field" class="sort-caret" :class="{ descending }" aria-hidden="true"></span>
              </button>
              <span class="column-resizer" aria-hidden="true" @pointerdown.prevent="startColumnResize($event, index)"
                @pointermove="resizeColumn" @lostpointercapture="finishColumnResize"></span>
            </th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(row, index) in rows" :id="'result-' + index" :key="row.key" :aria-selected="selected.has(row.key)"
            :data-file-extension="rowExtensions[index]"
            @click="clickRow($event, row)" @dblclick="runAction('open', row.key)"
            @contextmenu.prevent="openMenu(row, $event.clientX, $event.clientY)">
            <td class="entry-name" :title="row.name">
              <span class="entry-drag-source">
                <img v-if="fileTypeIcon(rowExtensions[index])" :src="fileTypeIcon(rowExtensions[index]) ?? undefined"
                  class="entry-file-type-icon" alt="" draggable="false">
                <span v-else class="entry-icon" :class="{ folder: row.isDirectory }" aria-hidden="true"></span>
                <template v-for="(part, partIndex) in rowParts[index].name" :key="partIndex">
                  <strong v-if="part.matched">{{ part.text }}</strong><template v-else>{{ part.text }}</template>
                </template>
              </span>
            </td>
            <td class="entry-path" :title="row.fullPath">
              <template v-for="(part, partIndex) in rowParts[index].path" :key="partIndex">
                <strong v-if="part.matched">{{ part.text }}</strong><template v-else>{{ part.text }}</template>
              </template>
            </td>
            <td v-if="indexFileSize" class="entry-size" :title="row.sizeBytesText ?? row.sizeHint ?? undefined">{{ row.sizeText }}</td>
            <td v-if="indexModifiedTime" class="entry-date">{{ row.modifiedText }}</td>
          </tr>
        </tbody>
      </table>
    </div>
    <div v-if="selectionBox" class="results-selection-box" aria-hidden="true"
      :style="{ left: selectionBox.left + 'px', top: selectionBox.top + 'px',
        width: selectionBox.width + 'px', height: selectionBox.height + 'px' }"></div>
    <div v-if="!rows.length" class="empty-results">
      <slot />
      <p v-if="emptyMessage" role="status">{{ emptyMessage }}</p>
    </div>
    <div v-if="menu" ref="menuElement" class="file-menu" role="menu" :aria-label="t('file.actions')"
      :style="{ left: menu.left + 'px', top: menu.top + 'px' }" @pointerdown.stop @keydown="handleMenuKey"
      @focusout="(event) => { if (!menuElement?.contains(event.relatedTarget as Node)) closeMenu() }">
      <button v-for="action in fileMenuActions" :key="action.type" type="button" role="menuitem" @click="runAction(action.type, menu.key)">
        {{ t(action.label) }}
        <kbd v-if="'shortcut' in action">{{ t(action.shortcut) }}</kbd>
      </button>
    </div>
  </section>
</template>
