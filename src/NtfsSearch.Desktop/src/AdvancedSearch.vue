<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import type { AdvancedSearchCriteria } from './desktop'

const props = defineProps<{
  criteria: AdvancedSearchCriteria
  extensions: string[]
  extensionsPending: boolean
  extensionsError: string
  indexReady: boolean
  indexCreationTime: boolean
  indexModifiedTime: boolean
  indexFileSize: boolean
}>()
const emit = defineEmits<{
  apply: [criteria: AdvancedSearchCriteria]
  close: []
  retry: []
}>()

const dialog = ref<HTMLDialogElement>()
const draft = ref<AdvancedSearchCriteria>({ ...props.criteria, extensions: [...(props.criteria.extensions ?? [])] })
const extensionText = ref('')
const extensionPageSize = 120
const extensionLimit = ref(extensionPageSize)
const error = ref('')
const choices = computed(() => {
  const text = extensionText.value.trim().toLowerCase()
  return [...new Set([...props.extensions, ...(draft.value.extensions ?? [])])]
    .filter(extension => (extension || '无后缀').toLowerCase().includes(text))
})
watch(extensionText, () => { extensionLimit.value = extensionPageSize })

function apply() {
  const { createdFrom, createdThrough, modifiedFrom, modifiedThrough, minSizeBytes, maxSizeBytes, extensions } = draft.value
  if (createdFrom && createdThrough && createdFrom > createdThrough
      || modifiedFrom && modifiedThrough && modifiedFrom > modifiedThrough) {
    error.value = '起始日期须早于或等于结束日期。'
    return
  }
  const min = minSizeBytes ? BigInt(minSizeBytes) : 0n
  const max = maxSizeBytes ? BigInt(maxSizeBytes) : 9223372036854775807n
  if (min > max || max > 9223372036854775807n) {
    error.value = '请输入有效的文件大小范围。'
    return
  }
  emit('apply', {
    createdFrom: createdFrom || undefined, createdThrough: createdThrough || undefined,
    modifiedFrom: modifiedFrom || undefined, modifiedThrough: modifiedThrough || undefined,
    minSizeBytes: minSizeBytes || undefined, maxSizeBytes: maxSizeBytes || undefined,
    extensions: extensions?.length ? [...extensions] : undefined,
  })
}

function clear() {
  draft.value = { extensions: [] }
  extensionText.value = ''
  error.value = ''
}

onMounted(() => dialog.value?.showModal())
</script>

<template>
  <dialog ref="dialog" class="advanced-search" aria-labelledby="advanced-title" @cancel.prevent="emit('close')">
    <form @submit.prevent="apply">
      <h2 id="advanced-title">高级搜索</h2>
      <fieldset :disabled="!indexCreationTime">
        <legend>创建日期</legend>
        <div v-if="!indexCreationTime" class="advanced-hint">在「设置 → 索引」开启创建时间并重启后可用。</div>
        <div v-else class="advanced-range">
          <label>起始日 <input v-model="draft.createdFrom" name="created-from" type="date" max="9999-12-31"></label>
          <label>结束日 <input v-model="draft.createdThrough" name="created-through" type="date" max="9999-12-31"></label>
        </div>
      </fieldset>
      <fieldset :disabled="!indexModifiedTime">
        <legend>修改日期</legend>
        <div v-if="!indexModifiedTime" class="advanced-hint">在「设置 → 索引」开启修改时间并重启后可用。</div>
        <div v-else class="advanced-range">
          <label>起始日 <input v-model="draft.modifiedFrom" name="modified-from" type="date" max="9999-12-31"></label>
          <label>结束日 <input v-model="draft.modifiedThrough" name="modified-through" type="date" max="9999-12-31"></label>
        </div>
      </fieldset>
      <fieldset :disabled="!indexFileSize">
        <legend>文件大小（字节）</legend>
        <div v-if="!indexFileSize" class="advanced-hint">在「设置 → 索引」开启文件大小并重启后可用。</div>
        <div v-else class="advanced-range">
          <label>最小值 <input v-model="draft.minSizeBytes" name="min-size" inputmode="numeric" pattern="[0-9]+"></label>
          <label>最大值 <input v-model="draft.maxSizeBytes" name="max-size" inputmode="numeric" pattern="[0-9]+"></label>
        </div>
      </fieldset>
      <fieldset>
        <legend>文件后缀</legend>
        <div class="advanced-extensions-header">
          <input v-model="extensionText" type="search" aria-label="查找后缀" placeholder="查找后缀" @keydown.enter.prevent>
          <span class="advanced-hint">{{ draft.extensions?.length ? `已选 ${draft.extensions.length} 项` : '所有后缀' }}</span>
        </div>
        <div v-if="!indexReady" class="advanced-hint" role="status">等待索引就绪…</div>
        <div v-else-if="extensionsPending" class="advanced-hint" role="status">正在读取索引中的后缀…</div>
        <div v-else-if="extensionsError" class="extension-error">
          <span role="alert">{{ extensionsError }}</span>
          <button class="outlined-button" type="button" @click="emit('retry')">重试</button>
        </div>
        <div v-else-if="!choices.length" class="advanced-hint">没有可选后缀</div>
        <div v-if="choices.length" class="advanced-extensions">
          <label v-for="extension in choices.slice(0, extensionLimit)" :key="extension">
            <input v-model="draft.extensions" type="checkbox" name="file-extension" :value="extension">
            <span :title="extension">{{ extension || '无后缀' }}</span>
          </label>
        </div>
        <button v-if="choices.length > extensionLimit" class="outlined-button more-extensions" type="button"
          @click="extensionLimit += extensionPageSize">显示更多后缀</button>
      </fieldset>
      <p v-if="error" class="advanced-error" role="alert">{{ error }}</p>
      <footer class="advanced-actions">
        <button class="outlined-button" type="button" @click="clear">清除条件</button>
        <button class="outlined-button" type="button" @click="emit('close')">取消</button>
        <button class="outlined-button" type="submit">应用</button>
      </footer>
    </form>
  </dialog>
</template>

<style scoped>
.advanced-search {
  width: min(510px, calc(100vw - 32px));
  max-height: calc(100vh - 32px);
  overflow: auto;
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 18px;
  background: #fff;
  color: inherit;
}
.advanced-search::backdrop { background: rgb(27 40 61 / 30%); }
h2 { margin: 0 0 12px; font-size: 17px; }
fieldset { margin: 0 0 8px; padding: 7px 10px 10px; border: 1px solid var(--border); border-radius: 6px; }
legend { padding: 0 4px; font-weight: 600; }
.advanced-range { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
.advanced-range label { display: grid; gap: 3px; min-width: 0; }
.advanced-range input, .advanced-extensions-header input {
  width: 100%;
  min-width: 0;
  min-height: 30px;
  padding: 3px 6px;
  border: 1px solid var(--border);
  border-radius: 4px;
  background: #fff;
}
.advanced-extensions-header, .extension-error { display: flex; align-items: center; gap: 10px; margin: 0 0 6px; }
.advanced-extensions-header span { white-space: nowrap; }
.advanced-hint { color: var(--muted); }
.advanced-extensions { max-height: 120px; overflow: auto; display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); }
.advanced-extensions label { display: flex; align-items: center; gap: 4px; min-width: 0; padding: 2px 4px 2px 0; }
.advanced-extensions span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.more-extensions { margin-top: 6px; }
.advanced-error, .extension-error { color: #a01c25; }
.advanced-error { margin: 6px 0; }
.advanced-actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 14px; }
.advanced-actions button:first-child { margin-right: auto; }
</style>
