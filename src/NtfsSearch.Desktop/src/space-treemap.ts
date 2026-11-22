import type { SpaceEntry } from './desktop'

interface Rectangle {
  left: number
  top: number
  width: number
  height: number
}

export function layoutSpaceItems(entries: SpaceEntry[], total: number, area: Rectangle) {
  const rectangles: (Rectangle & { item: SpaceEntry; fraction: number })[] = []
  if (area.width <= 0 || area.height <= 0 || !entries.length || total <= 0) return rectangles
  const items = entries.map(item => ({ item, fraction: Number(item.bytes) / total }))
    .sort((left, right) => right.fraction - left.fraction)
  split(0, items.length, 1, area)
  return rectangles

  function split(start: number, end: number, weightTotal: number, { left, top, width, height }: Rectangle) {
    if (start + 1 === end) {
      rectangles.push({ ...items[start], left, top, width, height })
      return
    }

    // 沿长边按权重切分。两组分别求和，避免极小的条目在从 1 相减时损失精度。
    let middle = start + 1
    let weight = items[start].fraction
    while (middle < end - 1 && weight + items[middle].fraction / 2 < weightTotal / 2)
      weight += items[middle++].fraction
    let remaining = 0
    for (let index = middle; index < end; index++) remaining += items[index].fraction
    const ratio = weight / (weight + remaining)
    const otherRatio = remaining / (weight + remaining)
    if (width >= height) {
      const cut = width * ratio
      split(start, middle, weight, { left, top, width: cut, height })
      split(middle, end, remaining, { left: left + cut, top, width: width * otherRatio, height })
    } else {
      const cut = height * ratio
      split(start, middle, weight, { left, top, width, height: cut })
      split(middle, end, remaining, { left, top: top + cut, width, height: height * otherRatio })
    }
  }
}
