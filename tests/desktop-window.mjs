import assert from 'node:assert/strict'
import { execFile, spawn } from 'node:child_process'
import { once } from 'node:events'
import { mkdir, readdir, readFile, writeFile } from 'node:fs/promises'
import { createServer as createHttpServer } from 'node:http'
import { createServer } from 'node:net'
import { dirname, join, resolve } from 'node:path'
import { setTimeout as sleep } from 'node:timers/promises'
import { promisify } from 'node:util'

const [executable, artifacts, mode = '--desktop-test'] = process.argv.slice(2)
if (!executable || !artifacts) throw new Error('用法：node tests/desktop-window.mjs <exe> <报告目录> [--desktop-benchmark | --cached-fixture | --tray | --search-results | --desktop-interaction | --native-file-drag | --search-service-settings | --without-http | --index-memory-settings | --exit-save-skip | --space-flash | --space-interaction | --space-capacity-settings]')

const output = resolve(artifacts)
await mkdir(output, { recursive: true })
const trayTest = mode === '--tray'
const searchResultsTest = mode === '--search-results'
const desktopInteractionTest = mode === '--desktop-interaction'
const nativeFileDragTest = mode === '--native-file-drag'
const searchServiceSettingsTest = mode === '--search-service-settings'
const withoutHttpTest = mode === '--without-http'
const indexMemorySettingsTest = mode === '--index-memory-settings'
const exitSaveSkipTest = mode === '--exit-save-skip'
const spaceFlashTest = mode === '--space-flash'
const spaceInteractionTest = mode === '--space-interaction'
const spaceCapacitySettingsTest = mode === '--space-capacity-settings'
const testData = join(output, trayTest ? 'TrayData' : searchServiceSettingsTest ? 'SearchServiceData'
  : withoutHttpTest ? 'WithoutHttpData' : indexMemorySettingsTest ? 'IndexMemoryData' : exitSaveSkipTest ? 'ExitSaveSkipData' : 'ExitSaveData')
const settingsPath = join(testData, 'settings.json')
const snapshotPath = join(testData, 'index.ntsnap')
const previousSnapshot = Buffer.from('isolated exit-save fixture')
if (mode !== '--cached-fixture') {
  await mkdir(testData, { recursive: true })
  await writeFile(settingsPath, JSON.stringify({ SkipExitIndexSave: !trayTest && !exitSaveSkipTest, ...(trayTest ? { IndexCreationTime: true } : {}) }))
  await writeFile(snapshotPath, previousSnapshot)
}
const portProbe = createServer().listen(0, '127.0.0.1')
await once(portProbe, 'listening')
const port = portProbe.address().port
let apiPort
if (trayTest || searchServiceSettingsTest) {
  const apiProbe = createServer().listen(0, '127.0.0.1')
  await once(apiProbe, 'listening')
  apiPort = apiProbe.address().port
  await new Promise(resolve => apiProbe.close(resolve))
}
await new Promise(resolve => portProbe.close(resolve))
const applicationArgs = mode === '--cached-fixture' ? []
  : [trayTest || searchResultsTest || desktopInteractionTest || nativeFileDragTest || searchServiceSettingsTest || withoutHttpTest || indexMemorySettingsTest || exitSaveSkipTest || spaceFlashTest || spaceInteractionTest || spaceCapacitySettingsTest ? '--desktop-test' : mode]
const applicationEnv = {
  ...process.env,
  ...(apiPort ? { NTFS_SEARCH_PORT: String(apiPort) } : {}),
  NTFS_SEARCH_TEST_DATA: testData,
  WEBVIEW2_USER_DATA_FOLDER: join(output, 'WebView'),
  WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}`,
}
let application
let exited
function launchApplication(args = applicationArgs) {
  application = spawn(resolve(executable), args, { stdio: 'ignore', env: applicationEnv })
  exited = once(application, 'exit')
}
launchApplication()
const exec = promisify(execFile)
const powerShell = script => exec('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand',
  Buffer.from(script, 'utf16le').toString('base64')], {
  encoding: 'utf8', timeout: 20_000, env: { ...process.env, TMP: output, TEMP: output }
})

async function mainWindowAction(hwnd, action) {
  const { stdout } = await powerShell(`
    $ErrorActionPreference = 'Stop'
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DesktopUi {
  [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("user32")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
}
'@
    $main = [IntPtr]${hwnd}
    switch ('${action}') {
      'visible' { [DesktopUi]::IsWindowVisible($main) }
      'activate' { [DesktopUi]::PostMessage($main, 0x0006, [IntPtr]1, [IntPtr]::Zero) }
      'close' { [DesktopUi]::PostMessage($main, 0x0112, [IntPtr]0xf060, [IntPtr]::Zero) }
    }
  `)
  return stdout.trim() === 'True'
}

async function windowWithTitle(pid, title) {
  const { stdout } = await powerShell(`
    $ErrorActionPreference = 'Stop'
    Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class TitledWindow {
  delegate bool WindowCallback(IntPtr hwnd, IntPtr arg);
  [DllImport("user32")] static extern bool EnumWindows(WindowCallback callback, IntPtr arg);
  [DllImport("user32")] static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
  [DllImport("user32", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder title, int capacity);
  public static long Find(int pid, string title) {
    long result = 0;
    EnumWindows((hwnd, arg) => {
      int owner;
      GetWindowThreadProcessId(hwnd, out owner);
      if (owner != pid) return true;
      var text = new StringBuilder(260);
      GetWindowText(hwnd, text, text.Capacity);
      if (!text.ToString().StartsWith(title, StringComparison.Ordinal)) return true;
      result = hwnd.ToInt64();
      return false;
    }, IntPtr.Zero);
    return result;
  }
}
'@
    [TitledWindow]::Find(${pid}, '${title}')
  `)
  return Number(stdout.trim())
}

async function until(fn, label, milliseconds = 15_000) {
  const deadline = Date.now() + milliseconds
  do {
    const found = await fn()
    if (found) return found
    if (application.exitCode != null) throw new Error(`桌面进程已退出，退出码 ${application.exitCode}`)
    await sleep(50)
  } while (Date.now() < deadline)
  throw new Error(`等待超时：${label}`)
}

const resultCount = () => document.querySelector('.result-count').textContent.trim()
const expected = count => `${count.toLocaleString('zh-CN')} 项结果`
let socket
let nextId = 0
const requests = new Map()

function cdp(method, params = {}) {
  const id = ++nextId
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      requests.delete(id)
      reject(new Error(`WebView2 调用超时：${method}`))
    }, 20_000)
    requests.set(id, response => {
      clearTimeout(timer)
      if (response.error) reject(new Error(response.error.message))
      else resolve(response.result)
    })
    socket.send(JSON.stringify({ id, method, params }))
  })
}

async function evaluate(expression) {
  const response = await cdp('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })
  if (response.exceptionDetails) throw new Error(response.exceptionDetails.exception?.description ?? response.exceptionDetails.text)
  return response.result.value
}

const requestQuit = () => evaluate("void window.__TAURI_INTERNALS__.invoke('send_request', { request: { type: 'quit' } })")

async function quitApplication() {
  await requestQuit()
  const [code] = await Promise.race([exited, sleep(8000, undefined, { ref: false }).then(() => { throw new Error('等待正常退出超时') })])
  assert.equal(code, 0)
}

async function screenshot(name) {
  const png = await cdp('Page.captureScreenshot', { format: 'png' })
  await writeFile(join(output, name), Buffer.from(png.data, 'base64'))
}

async function query(text, count) {
  return await evaluate(`(async () => {
    const input = document.querySelector('.search-field input');
    const start = performance.now();
    input.value = ${JSON.stringify(text)};
    input.dispatchEvent(new InputEvent('input', { bubbles: true }));
    const label = document.querySelector('.result-count');
    await new Promise(resolve => {
      const observer = new MutationObserver(() => {
        if (label.textContent.trim() === ${JSON.stringify(expected(count))}) {
          observer.disconnect();
          resolve();
        }
      });
      observer.observe(label, { subtree: true, childList: true, characterData: true });
    });
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    return { wallMilliseconds: performance.now() - start,
      queryMilliseconds: parseFloat(document.querySelector('.query-time').textContent) };
  })()`)
}

async function verifySearchResults() {
  const bold = column => evaluate(`[...document.querySelectorAll('tbody tr:first-child .entry-${column} strong')].map(part => part.textContent)`)
  await query('HELLO 000901', 1)
  assert.deepEqual(await bold('name'), ['hello', '000901'])
  assert.deepEqual(await bold('path'), ['hello', '000901'])
  await query('目录009 HELLO', 6)
  assert.deepEqual(await bold('name'), ['hello'])
  assert.deepEqual(await bold('path'), ['目录009', 'hello'])
  await query('"目录009/HELLO WORLD 000901.txt"', 1)
  assert.deepEqual(await bold('name'), ['hello world 000901.txt'])
  assert.deepEqual(await bold('path'), ['目录009\\hello world 000901.txt'])
  assert.equal(await evaluate("[...document.querySelectorAll('tbody td.entry-name, tbody td.entry-path')].every(cell => cell.textContent === cell.title)"), true, '加粗保留原始名称和路径')

  await evaluate("const scope = document.querySelector('.scope-select'); scope.value = 'name'; scope.dispatchEvent(new Event('change'))")
  await query('文件 REPORT', 936)
  await evaluate("document.querySelector('button[aria-label=下一页]').click()")
  await until(() => evaluate("document.querySelector('.page-number').textContent.trim() === '2 / 5'"), '带加粗的第二页')
  assert.deepEqual(await bold('name'), ['文件', 'report'])
  assert.deepEqual(await bold('path'), ['文件', 'report'])
  await query('', 1000)
  assert.deepEqual(await bold('name'), [])
  assert.deepEqual(await bold('path'), [])

  await cdp('Emulation.setDeviceMetricsOverride', { width: 660, height: 420, deviceScaleFactor: 1, mobile: false })
  const windowControls = () => evaluate(`['.search-toolbar', '.search-field input', '.status-bar'].map(selector => {
    const { left, right } = document.querySelector(selector).getBoundingClientRect();
    return [left, right];
  })`)
  const controls = await windowControls()
  const handle = await evaluate(`(() => {
    const rect = document.querySelector('.column-resizer').getBoundingClientRect();
    return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
  })()`)
  await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', ...handle, button: 'left', buttons: 1, clickCount: 1 })
  await cdp('Input.dispatchMouseEvent', { type: 'mouseMoved', ...handle, x: handle.x + 260, button: 'left', buttons: 1 })
  await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', ...handle, x: handle.x + 260, button: 'left', clickCount: 1 })
  const dimensions = () => evaluate(`(() => {
    const area = document.querySelector('.results-scroll');
    return { left: area.scrollLeft, top: area.scrollTop, viewport: area.clientWidth, table: area.scrollWidth };
  })()`)
  await until(async () => { const area = await dimensions(); return area.table > area.viewport }, '宽结果表可横向滚动')
  assert.deepEqual(await windowControls(), controls, '拖宽列不移动搜索框和底栏')
  const wheel = (deltaY, modifiers) => cdp('Input.dispatchMouseEvent', { type: 'mouseWheel', x: 200, y: 150, deltaX: 0, deltaY, modifiers })
  await wheel(2000, 8)
  await until(async () => { const area = await dimensions(); return area.left >= area.table - area.viewport - 1 }, 'Shift 加滚轮到右侧')
  assert.equal((await dimensions()).top, 0)
  assert.equal(await evaluate(`(() => {
    const head = document.querySelector('thead th:last-child').getBoundingClientRect();
    const cell = document.querySelector('tbody td:last-child').getBoundingClientRect();
    return Math.abs(head.left - cell.left) < 1 && head.right <= innerWidth && head.left >= 0;
  })()`), true, '最右列可见且表头与内容对齐')
  assert.deepEqual(await windowControls(), controls, '横向滚动不移动搜索框和底栏')
  await wheel(-2000, 8)
  await until(async () => (await dimensions()).left === 0, 'Shift 加滚轮回到左侧')
  await wheel(150, 0)
  await until(async () => (await dimensions()).top > 0, '普通滚轮纵向滚动')
  assert.equal((await dimensions()).left, 0)
}

async function verifyDesktopInteraction() {
  const point = index => evaluate(`(() => {
    const { left, top, height } = document.querySelectorAll('tbody .entry-path')[${index}].getBoundingClientRect();
    return { x: left + 10, y: top + height / 2 };
  })()`)
  const mouse = (type, position, modifiers = 0) => cdp('Input.dispatchMouseEvent', {
    type, ...position, modifiers, button: 'left', buttons: type === 'mouseReleased' ? 0 : 1, clickCount: 1,
  })
  const rowClick = async (index, modifiers = 0) => {
    const position = await point(index)
    await mouse('mousePressed', position, modifiers)
    await mouse('mouseReleased', position, modifiers)
  }
  const selection = () => evaluate("[...document.querySelectorAll('tbody tr')].flatMap((row, index) => row.getAttribute('aria-selected') === 'true' ? [index] : [])")
  const key = (key, modifiers = 0) => cdp('Input.dispatchKeyEvent', { type: 'keyDown', key, modifiers })
  const beginFrame = async () => {
    await mouse('mousePressed', await point(3))
    await mouse('mouseMoved', await point(6))
  }

  await rowClick(0)
  await rowClick(2, 2)
  assert.deepEqual(await selection(), [0, 2], 'Ctrl 保留非连续选中项')
  await rowClick(4, 8)
  assert.deepEqual(await selection(), [2, 3, 4], 'Shift 从活动行选取范围')
  await key('a', 2)
  assert.equal((await selection()).length, 200, 'Ctrl+A 选中当前页')

  await beginFrame()
  assert.deepEqual(await selection(), [3, 4, 5, 6], '辅助列起框实时选中相交行')
  await key('Escape')
  await mouse('mouseReleased', await point(6))
  assert.equal((await selection()).length, 200, 'Esc 恢复画框前的选择')
  await beginFrame()
  await mouse('mouseReleased', await point(6))
  assert.deepEqual(await selection(), [3, 4, 5, 6], '松开鼠标完成框选')

  const hwnd = await windowWithTitle(application.pid, 'NTFS Search')
  assert.ok(hwnd, '找到了 NTFS Search 主窗口')
  const searchFocused = () => evaluate("document.activeElement === document.querySelector('.search-field input')")
  assert.equal(await searchFocused(), false)
  assert.equal(await mainWindowAction(hwnd, 'activate'), true)
  await until(searchFocused, 'Windows 激活后聚焦搜索框')
  assert.deepEqual(await selection(), [3, 4, 5, 6], '前台激活保留所选结果')

  await click('设置')
  const draft = "document.querySelector('input[name=search-directory-sizes]')"
  await until(() => evaluate(`${draft}?.matches(':enabled')`), '设置草稿可编辑')
  const previous = await evaluate(`${draft}.checked`)
  await evaluate(`${draft}.click()`)
  assert.equal(await mainWindowAction(hwnd, 'activate'), true)
  await until(searchFocused, '设置页激活后回搜索')
  await click('设置')
  await until(() => evaluate(`${draft}?.matches(':enabled')`), '恢复后设置草稿可编辑')
  assert.equal(await evaluate(`${draft}.checked`), !previous, '重新打开设置可继续编辑草稿')
  await click('返回')

  const bottom = await evaluate(`(() => {
    const area = document.querySelector('.results-scroll');
    return area.getBoundingClientRect().top + area.clientHeight - 2;
  })()`)
  const origin = await point(3)
  const edge = { x: origin.x, y: bottom }
  await mouse('mousePressed', origin)
  await mouse('mouseMoved', edge)
  await until(() => evaluate("document.querySelector('.results-scroll').scrollTop >= 60"), '框选靠近下缘自动滚动')
  await mouse('mouseReleased', edge)
  assert.ok((await selection()).at(-1) > 10, '框选包含滚动后的结果')

  const missing = 'drag-missing-fixture.txt'
  await showDragFixtures([
    { name: '已存在.txt', path: settingsPath },
    { name: missing, path: join(testData, missing) },
  ])
  await rowClick(0)
  await rowClick(1, 2)
  const name = await evaluate(`(() => {
    const { left, top, height } = document.querySelector('.entry-drag-source').getBoundingClientRect();
    return { x: left + 8, y: top + height / 2 };
  })()`)
  await mouse('mousePressed', name)
  await mouse('mouseMoved', { x: name.x + 12, y: name.y })
  await mouse('mouseReleased', { x: name.x + 12, y: name.y })
  await until(() => evaluate(`document.querySelector('.operation-feedback.error')?.textContent.includes('${missing}')`),
    '拖动文件名时原生桥接收到整组路径')
  assert.deepEqual(await selection(), [0, 1], '拖动选中行保留整组选中项')
}

async function showDragFixtures(files) {
  await evaluate(`(async () => {
    const tauri = window.__TAURI_INTERNALS__;
    const handler = tauri.transformCallback(({ payload }) => {
      if (payload.type === 'result') window.dragFixtureResult = payload;
    });
    await tauri.invoke('plugin:event|listen', { event: 'backend-message', target: { kind: 'Any' }, handler });
  })()`)
  await query('HELLO 000901', 1)
  const rows = files.map(file => ({ name: file.name, fullPath: file.path, key: Buffer.from(file.path, 'utf16le').toString('base64') }))
  await evaluate(`(() => {
    const event = 'backend-message';
    const result = window.dragFixtureResult;
    const rows = ${JSON.stringify(rows)}.map(row => ({ ...result.rows[0], ...row, matchRanges: [] }));
    const ids = Object.getOwnPropertyNames(window.__internal_unstable_listeners_object_id__[event]).map(Number);
    window.__internal_unstable_listeners_function_id__({ event, payload: { ...result, rows, total: rows.length } }, ids);
  })()`)
}

async function verifyNativeFileDrag() {
  const files = ['拖放 甲.txt', '拖放 乙.txt'].map((name, index) => ({
    name, text: `文件内容 ${index + 1}`, path: join(output, 'drag-files', String(index), name),
  }))
  for (const file of files) {
    await mkdir(dirname(file.path), { recursive: true })
    await writeFile(file.path, file.text)
  }
  await showDragFixtures(files)
  let received
  let pageRequested = false
  const server = createHttpServer(async (request, response) => {
    if (request.method === 'POST') {
      const chunks = []
      for await (const chunk of request) chunks.push(chunk)
      received = JSON.parse(Buffer.concat(chunks).toString('utf8'))
      response.end()
      return
    }
    pageRequested = true
    response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' })
    response.end(`<title>NTFS Search 拖放验收</title><style>body { min-height: 90vh }</style><p>拖入两个测试文件</p><script>
      document.addEventListener('dragover', event => { event.preventDefault(); event.dataTransfer.dropEffect = 'copy' });
      document.addEventListener('drop', async event => {
        event.preventDefault();
        const files = [...event.dataTransfer.files];
        const received = await Promise.all(files.map(async file => ({ name: file.name, text: await file.text() })));
        await fetch(location.href, { method: 'POST', body: JSON.stringify(received) });
      });
    </script>`)
  }).listen(0, '127.0.0.1')
  await once(server, 'listening')
  const browser = spawn(join(process.env['ProgramFiles(x86)'], 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
    [`--user-data-dir=${join(output, 'Edge')}`, '--no-first-run', '--no-default-browser-check',
      `http://127.0.0.1:${server.address().port}/`], { stdio: 'ignore' })
  try {
    await once(browser, 'spawn')
    await until(() => pageRequested, 'Edge 上传页加载')
    const edge = await until(() => windowWithTitle(browser.pid, 'NTFS Search 拖放验收'), 'Edge 上传页窗口')
    const hwnd = await windowWithTitle(application.pid, 'NTFS Search')
    assert.ok(hwnd, '找到了拖放源主窗口')
    await evaluate(`(() => {
      const rows = document.querySelectorAll('tbody tr');
      rows[0].click();
      rows[1].dispatchEvent(new MouseEvent('click', { bubbles: true, ctrlKey: true }));
    })()`)
    assert.equal(await evaluate("document.querySelectorAll('tbody tr[aria-selected=true]').length"), files.length)
    const source = await evaluate(`(() => {
      const { left, top, height } = document.querySelector('.entry-drag-source').getBoundingClientRect();
      return { x: Math.ceil((left + 8) * devicePixelRatio), y: Math.round((top + height / 2) * devicePixelRatio) };
    })()`)
    // 真实鼠标按键经过 WebView2 和 Shell 发送给独立的浏览器进程。
    await powerShell(`
      $ErrorActionPreference = 'Stop'
      Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DesktopDrag {
  [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
  [DllImport("user32")] public static extern bool SetProcessDPIAware();
  [DllImport("user32")] public static extern int GetSystemMetrics(int index);
  [DllImport("user32")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
  [DllImport("user32")] public static extern bool ShowWindow(IntPtr hwnd, int command);
  [DllImport("user32")] public static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
  [DllImport("user32")] public static extern bool GetCursorPos(out Point point);
  [DllImport("user32")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32")] public static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extra);
}
'@
      [void][DesktopDrag]::SetProcessDPIAware()
      $screen = [DesktopDrag]::GetSystemMetrics(0)
      if ($screen -lt 1100) { throw '拖放验收需要至少 1100 个屏幕像素' }
      $middle = [int]($screen / 2)
      $height = [Math]::Min(620, [DesktopDrag]::GetSystemMetrics(1) - 60)
      [void][DesktopDrag]::ShowWindow([IntPtr]${edge}, 9)
      [void][DesktopDrag]::MoveWindow([IntPtr]${edge}, $middle, 30, ($screen - $middle - 15), $height, $true)
      [void][DesktopDrag]::MoveWindow([IntPtr]${hwnd}, 0, 30, ($middle - 10), $height, $true)
      if (!(New-Object -ComObject WScript.Shell).AppActivate(${application.pid})) { throw '未找到拖放源窗口' }
      Start-Sleep -Milliseconds 250
      $source = New-Object DesktopDrag+Point
      $source.X = ${source.x}; $source.Y = ${source.y}
      if (![DesktopDrag]::ClientToScreen([IntPtr]${hwnd}, [ref]$source)) { throw '无法转换源坐标' }
      $original = New-Object DesktopDrag+Point
      [void][DesktopDrag]::GetCursorPos([ref]$original)
      try {
        [void][DesktopDrag]::SetCursorPos($source.X, $source.Y)
        [DesktopDrag]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 90
        [void][DesktopDrag]::SetCursorPos(($source.X + 12), $source.Y)
        Start-Sleep -Milliseconds 400
        [void][DesktopDrag]::SetCursorPos(($middle + 100), 230)
        Start-Sleep -Milliseconds 350
      } finally {
        [DesktopDrag]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 150
        [void][DesktopDrag]::SetCursorPos($original.X, $original.Y)
      }
    `)
    assert.deepEqual(await until(() => received, '浏览器接收拖放文件'), files.map(({ name, text }) => ({ name, text })))
    for (const file of files) assert.equal(await readFile(file.path, 'utf8'), file.text)
  } finally {
    browser.kill()
    server.close()
  }
}

const visible = selector => evaluate(`(() => {
  const element = document.querySelector(${JSON.stringify(selector)});
  return !!element?.getClientRects().length && getComputedStyle(element).visibility === 'visible';
})()`)
const click = label => evaluate(`(() => {
  const button = [...document.querySelectorAll('button')].find(button => button.getClientRects().length
    && getComputedStyle(button).visibility === 'visible'
    && (button.getAttribute('aria-label') ?? button.textContent.trim()) === ${JSON.stringify(label)});
  if (!button || button.disabled) throw new Error('缺少可操作的按钮：' + ${JSON.stringify(label)});
  button.click();
})()`)

async function verifySearchServiceSettings() {
  const enabled = '.settings-workspace input[name=search-service-enabled]'
  const searchSizes = '.settings-workspace input[name=search-directory-sizes]'
  const saveExit = '.settings-workspace input[name=save-exit-index]'
  const quickSkip = '.index-exit-preference input[type=checkbox]'
  const checked = selector => evaluate(`document.querySelector('${selector}').checked`)
  const toggle = selector => evaluate(`document.querySelector('${selector}').click()`)
  const portInput = '.settings-workspace input[type=number]'
  const setPort = value => evaluate(`(() => {
    const field = document.querySelector('${portInput}');
    field.value = '${value}';
    field.dispatchEvent(new Event('input', { bubbles: true }));
  })()`)
  const status = expected => evaluate(`document.querySelector('.settings-workspace [role=status]')?.textContent.trim() === '${expected}'`)
  const address = `http://127.0.0.1:${apiPort}`
  const curl = String.raw`curl -sS --json '{"text":"C:\\Windows\\explorer.exe","limit":1}' ${address}/v1/search`

  await query('HELLO 000901', 1)
  await click('设置')
  await until(() => visible('#service-settings-title'), '搜索服务标签')
  await click('搜索服务')
  await until(() => status('运行中'), '设置页显示 HTTP 服务状态')
  assert.equal(await visible('.search-toolbar'), false)
  assert.equal(await evaluate(`document.querySelector('${enabled}').checked`), true)
  assert.equal(await checked(searchSizes), true)
  assert.equal(await checked(saveExit), false)
  assert.equal(await evaluate("Boolean(document.querySelector('.settings-footer button[type=submit]'))"), true)
  assert.equal(await evaluate(`document.querySelector('${portInput}').value`), '18791')
  assert.match(await evaluate("document.querySelector('.settings-workspace').textContent"), /NTFS_SEARCH_PORT/)
  assert.equal(await evaluate("document.querySelector('.settings-command code').textContent"), curl)

  await setPort(65536)
  await click('保存并应用')
  assert.match(await evaluate("document.querySelector('.settings-workspace [role=alert]').textContent"), /65535/)
  assert.equal(await status('运行中'), true)
  await setPort(18791)
  await evaluate(`document.querySelector('${enabled}').click()`)
  await click('保存并应用')
  await until(() => status('已禁用'), '应用后停止 HTTP 服务')
  await evaluate(`document.querySelector('${enabled}').click()`)
  await click('保存并应用')
  await until(() => status('运行中'), '应用后启动 HTTP 服务')
  await toggle(searchSizes)
  await toggle(saveExit)
  await click('重启服务')
  await until(() => status('运行中'), '重启后恢复 HTTP 服务')
  assert.equal(await checked(searchSizes), false)
  assert.equal(await checked(saveExit), true)
  assert.equal((await fetch(`${address}/v1/search`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text: 'HELLO 000901', limit: 1 }), signal: AbortSignal.timeout(5000),
  })).status, 200)
  await click('保存并应用')
  await until(async () => {
    const settings = JSON.parse(await readFile(settingsPath, 'utf8'))
    return await status('运行中') && settings.SearchDirectorySizes === false && settings.SkipExitIndexSave === false
  }, '全局保存搜索与索引设置')

  await click('返回')
  assert.equal(await visible('.settings-workspace'), false)
  assert.equal(await evaluate("document.querySelector('.search-field input').value"), 'HELLO 000901')
  assert.equal(await evaluate(`(${resultCount})()`), expected(1))
  await click('索引状态')
  assert.equal(await checked(quickSkip), false)
  await toggle(quickSkip)
  await until(() => checked(quickSkip), '索引状态即时保存')
  await click('关闭索引状态')
  await click('设置')
  await click('搜索服务')
  await until(() => status('运行中'), '重新打开设置')
  assert.equal(await checked(saveExit), false)
  assert.equal(await checked(searchSizes), false)
  await toggle(searchSizes)
  await click('返回')
  await click('空间分析')
  await until(() => visible('.space-map'), '进入空间分析')
  await evaluate("const level = document.querySelector('.space-level'); level.value = '3'; level.dispatchEvent(new Event('change'))")
  // 用 WebView 的 Tauri 事件派发模拟托盘通知，保持生产的事件权限和窗口状态。
  await evaluate(`(() => {
    const event = 'open-settings';
    const listeners = window.__internal_unstable_listeners_object_id__[event];
    const ids = Object.getOwnPropertyNames(listeners ?? {}).map(Number);
    window.__internal_unstable_listeners_function_id__({ event, payload: null }, ids);
  })()`)
  await click('搜索服务')
  await until(() => status('运行中'), '托盘事件打开同一设置页')
  assert.equal(await checked(searchSizes), false, '返回时丢弃未保存的目录设置')
  assert.equal(await visible('.space-map'), false)
  await click('返回')
  assert.equal(await visible('.space-map'), true)
  assert.equal(await evaluate("document.querySelector('.space-level').value"), '3')
}

async function waitForDesktop(total) {
  const page = await until(async () => {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()
      return targets.find(target => target.type === 'page')
    } catch { return undefined }
  }, 'WebView2 页面', 30_000)
  socket = new WebSocket(page.webSocketDebuggerUrl)
  socket.addEventListener('message', event => {
    const response = JSON.parse(event.data)
    const resolve = requests.get(response.id)
    requests.delete(response.id)
    resolve?.(response)
  })
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true })
    socket.addEventListener('error', reject, { once: true })
  })
  await until(async () => {
    const error = await evaluate("document.querySelector('.startup-error span')?.textContent")
    if (error) throw new Error(error)
    return await evaluate(`document.querySelector('.result-count') && (${resultCount})() === ${JSON.stringify(expected(total))}`)
  }, '初始精确计数', 90_000)
  assert.equal(await evaluate("document.activeElement.getAttribute('aria-label')"), '搜索文件和文件夹')
  assert.equal(await evaluate("document.querySelectorAll('tbody tr').length"), 200)
}

async function verifySpaceFlash() {
  await cdp('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1120, deviceScaleFactor: 1, mobile: false })
  await evaluate(`(async () => {
    const event = 'backend-message';
    const listeners = window.__internal_unstable_listeners_object_id__[event];
    const ids = Object.getOwnPropertyNames(listeners).map(Number);
    const tauri = window.__TAURI_INTERNALS__;
    const handler = tauri.transformCallback(({ payload }) => {
      if (payload.type === 'spaceAnalysis') window.lastSpaceResult = payload;
    });
    await tauri.invoke('plugin:event|listen', { event, target: { kind: 'Any' }, handler });
    // 真实演示索引提供布局。通过现有事件入口注入一批文件变更，避免依赖实盘写入时机。
    window.sendSpaceActivity = activityPaths => {
      const payload = { ...window.lastSpaceResult, activityPaths };
      window.__internal_unstable_listeners_function_id__({ event, payload }, ids);
    };
  })()`)
  await click('空间分析')
  await until(() => visible('.space-map'), '打开空间分析')

  const setLevel = level => evaluate(`(() => {
    const select = document.querySelector('.space-level');
    select.value = '${level}';
    select.dispatchEvent(new Event('change', { bubbles: true }));
  })()`)
  const folderTile = name => `Array.from(document.querySelectorAll('.space-tile.directory'))
    .find(tile => tile.title.split(' · ', 1)[0].endsWith(${JSON.stringify(name)}))`
  const folderKey = name => until(() => evaluate(`${folderTile(name)}?.dataset.tileKey`), '显示' + name)
  const expectFlashes = async (paths, keys, label) => {
    const flashed = await evaluate(`(async () => {
      window.sendSpaceActivity(${JSON.stringify(paths)});
      await Promise.resolve();
      const folders = new Map(Array.from(document.querySelectorAll('.space-tile.directory'),
        tile => [tile.style.cssText, tile.dataset.tileKey]));
      return Array.from(document.querySelectorAll('.space-flash'), overlay => folders.get(overlay.style.cssText)).sort();
    })()`)
    assert.deepEqual(flashed, keys.sort(), label)
    await until(() => evaluate("document.querySelector('.space-flash') === null"), '闪烁结束')
  }

  await setLevel(3)
  const automatic = await folderKey('深层2')
  const other = await folderKey('目录008')
  const path = automatic.split('/')
  assert.equal(path.length, 3)
  await expectFlashes([path, path, [other]], [automatic, other], '自动展开、多分支与同批去重')

  await evaluate(`${folderTile('深层2')}.click()`)
  const manual = await folderKey('深层3')
  const manualPath = manual.split('/')
  await expectFlashes([manualPath, path], [manual, automatic], '手动展开与同一分支的两处独立变化')

  await evaluate(`${folderTile('目录001')}.click(); ${folderTile('目录001')}.click()`)
  await setLevel(1)
  await until(() => evaluate("document.querySelector('.space-tile.nested') === null"), '收起子目录')
  await expectFlashes([manualPath], [path[0]], '子目录收起时高亮最近的可见上级')
}

async function verifySpaceInteraction() {
  const menu = ".file-menu[aria-label='文件操作']"
  const tileMenu = menu + ':popover-open'
  const labels = selector => evaluate(`Array.from(document.querySelectorAll(${JSON.stringify(selector + ' button')}),
    button => button.firstChild.textContent.trim())`)
  const openMenu = target => evaluate(`${target}.dispatchEvent(new MouseEvent('contextmenu', {
    clientX: 150, clientY: 150, bubbles: true, cancelable: true
  }))`)
  await openMenu("document.querySelector('tbody tr')")
  const searchActions = await labels(menu)
  assert.deepEqual(searchActions, ['打开', '打开所在目录', '复制完整路径'])
  await evaluate("document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))")

  await click('空间分析')
  await until(() => visible('.space-total'), '等待首次空间分析')
  const address = "document.querySelector('.space-address input')"
  const setAddress = path => evaluate(`(() => {
    const input = ${address};
    input.value = ${JSON.stringify(path)};
    input.dispatchEvent(new Event('input', { bubbles: true }));
  })()`)
  const go = async path => {
    await setAddress(path)
    await evaluate(`${address}.form.requestSubmit()`)
    await until(() => evaluate(`document.querySelector('.space-breadcrumb [aria-current]')?.textContent.endsWith(${JSON.stringify(path.split('\\').at(-1))})`),
      '进入 ' + path)
  }
  const folder = "document.querySelector('.space-tile.directory:not(.nested)')"
  await go('C:\\演示\\目录009')
  await openMenu("document.querySelector('.space-tile:not(.others)')")
  await until(() => visible(tileMenu), '打开文件菜单')
  assert.deepEqual(await labels(tileMenu), [...searchActions, '打开方式…', '临时排除'])
  await click('临时排除')
  const exclusions = '[popovertarget=space-exclusions]'
  await until(() => visible(exclusions), '排除一个文件')

  await go('C:\\演示\\目录001')
  await openMenu(folder)
  await until(() => visible(tileMenu), '打开目录菜单')
  assert.deepEqual(await labels(tileMenu), [...searchActions, '临时排除'])
  const expanded = () => evaluate(`${folder}?.getAttribute('aria-expanded') === 'true'`)
  await evaluate(`${folder}.click(); const level = document.querySelector('.space-level');
    level.value = '3'; level.dispatchEvent(new Event('change'))`)
  await until(expanded, '展开当前分支')
  const draft = 'C:\\保留未提交的地址'
  await setAddress(draft)
  await click('返回搜索')
  assert.equal(await visible('.space-map'), false)
  assert.equal(await visible(tileMenu), false)
  await click('空间分析')
  await until(() => visible('.space-map'), '再次进入空间分析')
  assert.match(await evaluate("document.querySelector('.space-breadcrumb [aria-current]')?.textContent ?? ''"), /目录001$/)
  assert.equal(await evaluate(`${address}.value`), draft)
  assert.equal(await evaluate("document.querySelector('.space-level').value"), '3')
  assert.equal(await expanded(), true)
  assert.equal(await visible(exclusions), true)
  await click('已排除 1 项')
  await click('全部恢复')
  await until(async () => !(await visible(exclusions)), '恢复切换前排除的文件')

  await evaluate(`(async () => {
    const tauri = window.__TAURI_INTERNALS__;
    const handler = tauri.transformCallback(() => { window.spaceActivated = true; });
    await tauri.invoke('plugin:event|listen', { event: 'activate-search', target: { kind: 'Any' }, handler });
    ${address}.focus();
  })()`)
  const hwnd = await windowWithTitle(application.pid, 'NTFS Search')
  assert.ok(hwnd, '找到空间分析主窗口')
  assert.equal(await mainWindowAction(hwnd, 'activate'), true)
  await until(() => evaluate('window.spaceActivated === true'), '收到 Windows 前台激活')
  assert.equal(await visible('.space-map'), true)
  assert.equal(await evaluate(`document.activeElement === ${address}`), true)
  assert.equal(await evaluate(`${address}.value`), draft)
}

async function verifyIndexMemorySettings(total) {
  const switches = {
    PrecomputeSort: 'precompute-sort', IndexCreationTime: 'index-creation-time',
    IndexModifiedTime: 'index-modified-time', IndexFileSize: 'index-file-size',
  }
  const defaults = { PrecomputeSort: true, IndexCreationTime: false, IndexModifiedTime: true, IndexFileSize: true }
  const disabledColumns = { PrecomputeSort: false, IndexCreationTime: true, IndexModifiedTime: false, IndexFileSize: false }
  const field = name => `document.querySelector('.settings-workspace input[name=${name}]')`
  const ready = () => evaluate("document.querySelector('.settings-footer button[type=submit]')?.disabled === false")
  const columns = () => evaluate("[...document.querySelectorAll('thead .column-label')].map(label => label.textContent)")
  const pendingRestart = () => evaluate("Boolean(document.querySelector('.settings-feedback [role=status]'))")
  const folderSizes = () => evaluate(`(() => {
    const input = ${field('search-directory-sizes')};
    return { checked: input.checked, disabled: input.disabled };
  })()`)
  const spaceEnabled = () => evaluate("document.querySelector('button[aria-label=空间分析]')?.disabled === false")
  async function openSettings() {
    await click('设置')
    await until(ready, '索引设置加载完成')
    await click('索引')
  }
  async function checkSwitches(values) {
    for (const [name, checked] of Object.entries(values)) {
      assert.equal(await evaluate(`${field(switches[name])}?.checked`), checked, switches[name])
    }
  }
  async function editSwitches(values) {
    for (const [name, checked] of Object.entries(values)) {
      await evaluate(`(() => {
        const input = ${field(switches[name])};
        if (input.checked !== ${checked}) input.click();
      })()`)
    }
  }
  async function saveSwitches(values, needsRestart) {
    await click('保存并应用')
    await until(async () => {
      const saved = JSON.parse(await readFile(settingsPath, 'utf8'))
      return await ready() && saved.SearchDirectorySizes === true
        && Object.entries(values).every(([name, checked]) => saved[name] === checked)
    }, '保存索引设置和文件夹大小偏好')
    assert.equal(await pendingRestart(), needsRestart, '只有元数据列变更需要重启')
  }
  async function restartApplication() {
    await quitApplication()
    socket.close()
    launchApplication()
    await waitForDesktop(total)
  }

  const allColumns = ['名称', '完整路径', '大小', '修改时间']
  assert.deepEqual(await columns(), allColumns)
  assert.equal(await spaceEnabled(), true)
  await click('大小')
  await until(() => evaluate("document.querySelector('thead th[aria-sort=ascending]')?.textContent.trim() === '大小'"), '选择大小排序')
  await openSettings()
  await checkSwitches(defaults)
  assert.equal(await pendingRestart(), false)
  for (const precompute of [false, true, false, true]) {
    const values = { PrecomputeSort: precompute }
    await editSwitches(values)
    await saveSwitches(values, false)
    await click('返回')
    assert.deepEqual(await columns(), allColumns)
    assert.equal(await evaluate("document.querySelector('thead th[aria-sort=ascending]').textContent.trim()"), '大小')
    await query(precompute ? '' : 'HELLO 000901', precompute ? total : 1)
    await openSettings()
    await checkSwitches(values)
  }
  assert.match(await evaluate(`(() => {
    const input = ${field('precompute-sort')};
    return document.getElementById(input.getAttribute('aria-describedby'))?.textContent ?? '';
  })()`), /保存后立即生效/)
  assert.match(await evaluate("document.querySelector('[aria-labelledby=index-settings-title]').textContent"), /保存后，以下索引设置将在下次重启时生效/)
  await editSwitches(disabledColumns)
  assert.deepEqual(await folderSizes(), { checked: true, disabled: true })
  assert.match(await evaluate("document.querySelector('[aria-labelledby=search-settings-title]').textContent"), /依赖.*索引文件大小/)
  assert.equal(await pendingRestart(), false)
  await saveSwitches(disabledColumns, true)
  await click('返回')
  assert.deepEqual(await columns(), allColumns, '保存后仍使用当前进程的列')
  assert.equal(await spaceEnabled(), true, '保存后空间分析继续使用当前进程设置')
  assert.equal(await evaluate("document.querySelector('thead th[aria-sort=ascending]').textContent.trim()"), '大小')

  await openSettings()
  await checkSwitches(disabledColumns)
  assert.equal(await pendingRestart(), true, '重新打开设置仍显示待重启')
  await editSwitches(defaults)
  assert.deepEqual(await folderSizes(), { checked: true, disabled: false })
  await saveSwitches(defaults, false)
  await editSwitches(disabledColumns)
  await saveSwitches(disabledColumns, true)
  await restartApplication()

  assert.deepEqual(await columns(), ['名称', '完整路径'])
  assert.equal(await evaluate("document.querySelector('tbody tr:first-child').cells.length"), 2)
  assert.equal(await evaluate("document.querySelector('thead th[aria-sort=ascending]').textContent.trim()"), '名称')
  assert.equal(await spaceEnabled(), false)
  assert.match(await evaluate("document.querySelector('button[aria-label=空间分析]').getAttribute('aria-description')"), /索引文件大小/)
  await openSettings()
  await checkSwitches(disabledColumns)
  assert.equal(await pendingRestart(), false)
  assert.deepEqual(await folderSizes(), { checked: true, disabled: true })
  const restoredSize = { ...disabledColumns, IndexFileSize: true }
  await editSwitches(restoredSize)
  assert.deepEqual(await folderSizes(), { checked: true, disabled: false })
  await saveSwitches(restoredSize, true)
  await restartApplication()

  assert.deepEqual(await columns(), ['名称', '完整路径', '大小'])
  assert.equal(await spaceEnabled(), true)
  await click('大小')
  await until(() => evaluate("document.querySelector('thead th[aria-sort=ascending]')?.textContent.trim() === '大小'"), '选择大小升序')
  // 演示目录大于单个文件，降序首页才能验证目录合计与文件混排。
  await click('大小')
  await until(() => evaluate(`document.querySelector('thead th[aria-sort=descending]')?.textContent.trim() === '大小'
    && [...document.querySelectorAll('tbody tr')].some(row => row.querySelector('.entry-icon.folder')
      && / B$/.test(row.querySelector('.entry-size').title))`), '恢复保存的文件夹大小统计')
}

async function verifySpaceCapacitySettings() {
  const picker = 'document.querySelector("button[aria-label=分析磁盘]")'
  const selectedTab = () => evaluate('document.querySelector("[role=tab][aria-selected=true]")?.textContent.trim()')
  const diskText = () => evaluate(`${picker}?.textContent.trim() ?? ''`)
  const diskFill = () => evaluate(`getComputedStyle(${picker}).getPropertyValue('--volume-fill').trim()`)
  const field = name => `document.querySelector('[name=${name}]')`
  const ready = () => evaluate("document.querySelector('.settings-footer button')?.disabled === false")
  const openSettings = async () => {
    await click('设置')
    await until(ready, '空间分析设置已加载')
    assert.equal(await selectedTab(), '空间分析')
  }
  const save = async () => {
    await click('保存并应用')
    await until(ready, '保存设置')
    assert.equal(await evaluate("document.querySelector('.settings-error')?.textContent ?? ''"), '')
  }
  await click('空间分析')
  await until(async () => (await diskText()).includes('768 GB / 1 TB (75%)'), '已用容量文字')
  assert.equal(await diskFill(), '75%')
  await click('分析磁盘')
  assert.deepEqual(await evaluate(`[...document.querySelectorAll('[role=listbox] [role=option]')]
    .map(option => [option.getAttribute('aria-selected'), ...[...option.children].map(label => label.textContent)])`),
  [['true', 'C:\\', '768 GB / 1 TB (75%)'], ['false', 'D:\\', '512 GB / 2 TB (25%)']])
  const activeRoot = () => evaluate("document.activeElement?.closest('[role=option]')?.firstElementChild.textContent")
  const key = async (key, windowsVirtualKeyCode) => {
    await cdp('Input.dispatchKeyEvent', { type: 'keyDown', key, code: key, windowsVirtualKeyCode })
    await cdp('Input.dispatchKeyEvent', { type: 'keyUp', key, code: key, windowsVirtualKeyCode })
  }
  assert.equal(await activeRoot(), 'C:\\')
  await key('ArrowDown', 40)
  assert.equal(await activeRoot(), 'D:\\')
  await key('Enter', 13)
  await until(async () => (await diskText()).includes('D:\\'), '键盘切换磁盘')
  assert.equal(await diskFill(), '25%')
  await click('分析磁盘')
  assert.equal(await activeRoot(), 'D:\\')
  await evaluate(`${picker}.focus(); document.querySelector('.space-level').focus();`)
  assert.equal(await visible('#space-volume-options'), false, '焦点离开磁盘下拉框后收起')

  await openSettings()
  await evaluate(`const select = ${field('space-volume-display')};
    select.value = 'free'; select.dispatchEvent(new Event('change'));`)
  await click('搜索')
  assert.equal(await selectedTab(), '搜索')
  assert.equal(await visible('#settings-panel-space'), false)
  await save()
  await click('返回')
  assert.match(await diskText(), /1\.5 TB \/ 2 TB \(75%\)/)
  assert.equal(await diskFill(), '75%')

  await openSettings()
  await evaluate(`${field('space-volume-capacity-bar')}.click()`)
  await save()
  await click('返回')
  assert.match(await diskText(), /1\.5 TB \/ 2 TB \(75%\)/)
  assert.equal(await diskFill(), '')

  await openSettings()
  await evaluate(`${field('show-space-volume-capacity')}.click()`)
  assert.equal(await evaluate(`${field('space-volume-display')}.disabled`), true)
  await save()
  await click('返回')
  assert.equal(await diskText(), 'D:\\')
  assert.equal(await diskFill(), '')
  assert.equal(JSON.parse(await readFile(settingsPath, 'utf8')).SpaceVolumeShowFree, true)
}

async function verifyExitSaveSkip() {
  // 先用正常退出保存一份较小的快照，再用现有的大型演示索引留出写入时的操作窗口。
  await quitApplication()
  const snapshot = await readFile(snapshotPath)
  assert.match(snapshot.toString('ascii', 0, 8), /^NTSEXIT\d$/, '已生成上次完整快照')
  socket.close()
  const benchmarkArgs = ['--desktop-benchmark']
  launchApplication(benchmarkArgs)
  await waitForDesktop(1_000_000)

  await cdp('Runtime.addBinding', { name: 'reportExitSaveState' })
  const states = []
  socket.addEventListener('message', event => {
    const message = JSON.parse(event.data)
    if (message.method === 'Runtime.bindingCalled' && message.params.name === 'reportExitSaveState')
      states.push(JSON.parse(message.params.payload))
  })
  await evaluate(`(() => {
    let clicked = false;
    let previous;
    new MutationObserver(() => {
      const overlay = document.querySelector('.save-overlay');
      if (!overlay) return;
      const button = [...overlay.querySelectorAll('button')].find(button => button.textContent.trim() === '此次不保存');
      const status = overlay.querySelector('strong').textContent.trim();
      const enabled = !!button && !button.disabled;
      const state = JSON.stringify({ status, enabled });
      if (state !== previous) window.reportExitSaveState(previous = state);
      if (enabled && !clicked) {
        clicked = true;
        button.click();
      }
    }).observe(document.body, { childList: true, attributes: true, characterData: true, subtree: true });
  })()`)
  await quitApplication()
  assert.ok(states.some(state => state.status === '正在保存退出快照' && state.enabled), '写入时显示可操作的「此次不保存」')
  assert.ok(states.some(state => state.status === '正在跳过本次保存…' && !state.enabled), '点击后显示跳过状态并禁用按钮')
  assert.ok((await readFile(snapshotPath)).equals(snapshot), `跳过时逐字节保留旧快照。保存阶段 ${JSON.stringify(states)}`)
  assert.equal(JSON.parse(await readFile(settingsPath, 'utf8')).SkipExitIndexSave, false, '跳过后保留退出保存偏好')
  assert.deepEqual((await readdir(testData)).filter(name => name.endsWith('.tmp')), [], '跳过后清理临时快照')

  socket.close()
  launchApplication(benchmarkArgs)
  await waitForDesktop(1_000_000)
  return snapshot
}

try {
  const benchmark = mode === '--desktop-benchmark'
  const total = benchmark ? 1_000_000 : 1000
  await waitForDesktop(total)

  let snapshotBeforeSkip
  if (benchmark) {
    await sleep(2000)
    const memory = await powerShell(`
      $all = @(Get-CimInstance Win32_Process)
      $ids = @(${application.pid})
      do {
        $children = @($all | Where-Object { $_.ParentProcessId -in $ids -and $_.ProcessId -notin $ids } | ForEach-Object ProcessId)
        $ids += $children
      } while ($children.Count)
      Get-Process -Id $ids | Select-Object ProcessName,Id,WorkingSet64,PrivateMemorySize64 | ConvertTo-Json -Compress
    `)
    const samples = []
    for (let run = 0; run < 3; run++) {
      for (const [text, count] of [['"hello world"', 58_824], ['目录009', 100], ['ntfs-absent-fixture', 0]]) {
        samples.push({ text, count, run, ...(await query(text, count)) })
      }
    }
    await writeFile(join(output, 'benchmark.json'), JSON.stringify({ rows: total, processes: JSON.parse(memory.stdout), samples }, null, 2))
  } else if (searchResultsTest) {
    await verifySearchResults()
  } else if (desktopInteractionTest) {
    await verifyDesktopInteraction()
  } else if (nativeFileDragTest) {
    await verifyNativeFileDrag()
  } else if (searchServiceSettingsTest) {
    await verifySearchServiceSettings()
  } else if (spaceFlashTest) {
    await verifySpaceFlash()
  } else if (spaceInteractionTest) {
    await verifySpaceInteraction()
  } else if (spaceCapacitySettingsTest) {
    await verifySpaceCapacitySettings()
  } else if (withoutHttpTest) {
    await click('设置')
    const settingsReady = () => evaluate("document.querySelector('.settings-footer button[type=submit]')?.disabled === false")
    await until(settingsReady, '无查询服务版设置加载完成')
    assert.equal(await evaluate("Boolean(document.querySelector('.settings-service'))"), false)
    assert.deepEqual(await evaluate("[...document.querySelectorAll('[role=tab]')].map(tab => tab.textContent)"), ['搜索', '索引', '空间分析'])
    await evaluate("document.querySelector('input[name=search-directory-sizes]').click()")
    await click('保存并应用')
    await until(async () => await settingsReady() && JSON.parse(await readFile(settingsPath, 'utf8')).SearchDirectorySizes === false,
      '无查询服务版保存桌面设置')
    await click('返回')
    await query('HELLO 000901', 1)
  } else if (indexMemorySettingsTest) {
    await verifyIndexMemorySettings(total)
  } else if (exitSaveSkipTest) {
    snapshotBeforeSkip = await verifyExitSaveSkip()
  } else if (!trayTest) {
    const fileIcon = extension => `document.querySelector('td.entry-name[title$="${extension}"] img.entry-file-type-icon')`
    const textIcon = await until(() => evaluate(`(() => {
      const icon = ${fileIcon('.txt')};
      return icon?.naturalWidth > 0 && icon.src;
    })()`), '文本文件类型图标')
    assert.equal(await evaluate(`Boolean(${fileIcon('.png')})`), false, '屏外文件类型尚未加载')
    await evaluate("document.querySelector('td.entry-name[title$=\".png\"]').scrollIntoView({ block: 'center' })")
    const imageIcon = await until(() => evaluate(`(() => {
      const icon = ${fileIcon('.png')};
      return icon?.naturalWidth > 0 && icon.src;
    })()`), '滚动后加载图片文件类型图标')
    assert.notEqual(textIcon, imageIcon)

    await evaluate("document.querySelector('button[aria-label=\"下一页\"]').click()")
    await until(() => evaluate("document.querySelector('.page-number').textContent.trim() === '2 / 5'"), '第二页')
    await evaluate("document.querySelector('th:nth-child(3) button').click()")
    await until(() => evaluate("document.querySelector('.page-number').textContent.trim() === '1 / 5'"), '排序重置到第一页')
    await evaluate("document.querySelector('th:nth-child(3) button').click()")
    const largestSize = benchmark ? '1,023,998,976 B' : '15,308,800 B'
    await until(() => evaluate(`document.querySelector('th:nth-child(3)').getAttribute('aria-sort') === 'descending'
      && document.querySelector('tbody tr td:nth-child(3)').title === '${largestSize}'
      && !document.querySelector('.directory-sort-status')`), '大小降序及精确字节数')

    await query('目录009', 100)
    await evaluate("const scope = document.querySelector('select'); scope.value = 'name'; scope.dispatchEvent(new Event('change'));")
    await until(() => evaluate("document.querySelector('.empty-results')?.textContent.includes('没有匹配')"), '仅名称过滤')
    await evaluate("document.querySelector('.clear-search').click()")
    await until(() => evaluate(`(${resultCount})() === ${JSON.stringify(expected(total))}`), '清空搜索')
    if (mode === '--desktop-test') {
      assert.equal(await evaluate("Boolean(document.querySelector('button[aria-label=空间分析]'))"), true)
      const scrollBeforeAnalysis = await evaluate(`(() => {
        const results = document.querySelector('.results-scroll');
        results.scrollTop = 124;
        return results.scrollTop;
      })()`)
      await evaluate("document.querySelector('button[aria-label=空间分析]').click()")
      await until(() => evaluate("document.querySelector('.space-total strong')?.textContent === '487.79 MB'"), '卷根逻辑大小')
      const topFolder = name => `[...document.querySelectorAll('.space-tile.directory:not(.nested)')]
        .find(tile => tile.title.includes(${JSON.stringify(name)}))`
      const automatic = await until(() => evaluate(`(() => {
        const tiles = [...document.querySelectorAll('.space-tile.nested')].map(tile => tile.getBoundingClientRect());
        const roots = [...document.querySelectorAll('.space-tile.directory:not(.nested)')].map(tile => tile.getBoundingClientRect());
        return roots.filter(root => tiles.some(tile => tile.left >= root.left && tile.right <= root.right &&
          tile.top >= root.top && tile.bottom <= root.bottom)).length >= 2;
      })()`), '多个目录自动显示内部结构')
      assert.equal(automatic, true)
      assert.equal(await evaluate("document.querySelectorAll('.space-flash').length"), 0)
      const clickFolder = (name, inside = false) => evaluate(`(async () => {
        const tile = ${topFolder(name)};
        const rect = tile.getBoundingClientRect();
        const clientY = rect.top + (${inside} ? rect.height / 2 : 10);
        tile.dispatchEvent(new MouseEvent('click', { detail: 1, clientY, clientX: rect.left + rect.width / 2, bubbles: true }));
        await Promise.resolve();
        return tile.getAttribute('aria-expanded');
      })()`)
      await evaluate(`${topFolder('目录009')}.dispatchEvent(new MouseEvent('mouseenter'))`)
      assert.ok(await evaluate("document.querySelector('.space-details').textContent.includes('92.72 MB')"), '底栏自动选用容量单位')
      assert.equal(await clickFolder('目录009'), 'true', '默认两层时单击目录标题立即展开')
      assert.equal(await clickFolder('目录008'), 'true')
      assert.equal(await evaluate(`${topFolder('目录009')}.getAttribute('aria-expanded')`), 'false')
      assert.equal(await clickFolder('目录008'), 'false')
      await cdp('Emulation.setDeviceMetricsOverride', { width: 480, height: 170, deviceScaleFactor: 1, mobile: false })
      await until(() => evaluate("document.querySelectorAll('.space-tile.nested').length === 0"), '狭小空间自动收起细节')
      await cdp('Emulation.clearDeviceMetricsOverride')
      await until(() => evaluate("document.querySelectorAll('.space-tile.nested').length > 0"), '尺寸恢复后重新显示细节')
      await evaluate(`(() => {
        const level = document.querySelector('select[aria-label=细节层级]');
        level.value = '1'; level.dispatchEvent(new Event('change', { bubbles: true }));
      })()`)
      await until(() => evaluate("document.querySelectorAll('.space-tile.nested').length === 0"), '细节降到一层')
      assert.equal(await clickFolder('目录009', true), 'true', '点目录内部也立即响应')
      await until(() => evaluate(`${topFolder('目录009')}.getAttribute('aria-expanded') === 'true' &&
        document.querySelectorAll('.space-tile.nested').length > 0`), '单击临时展开')
      assert.equal(await evaluate("document.querySelector('.space-total strong').textContent"), '487.79 MB')
      await evaluate(`${topFolder('目录008')}.click()`)
      await until(() => evaluate(`${topFolder('目录008')}.getAttribute('aria-expanded') === 'true' &&
        ${topFolder('目录009')}.getAttribute('aria-expanded') === 'false'`), '切换时收起原目录')
      await evaluate(`${topFolder('目录008')}.click()`)
      await until(() => evaluate("document.querySelectorAll('.space-tile.nested').length === 0"), '再次单击收起')
      await cdp('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1120, deviceScaleFactor: 1, mobile: false })
      await evaluate(`${topFolder('目录001')}.click()`)
      for (let depth = 1; depth <= 5; depth++) {
        const subfolder = `[...document.querySelectorAll('.space-tile.directory.nested')]
          .find(tile => tile.title.startsWith(${JSON.stringify('深层' + depth)}))`
        await until(() => evaluate(`Boolean(${subfolder})`), '为单击显示第 ' + depth + ' 层目录')
        await evaluate(`${subfolder}.click()`)
        await until(() => evaluate(`${subfolder}?.getAttribute('aria-expanded') === 'true'`),
          '在原视图单击展开第 ' + depth + ' 层目录')
      }
      assert.equal(await evaluate("document.querySelector('.space-total strong').textContent"), '487.79 MB')
      assert.equal(await evaluate("document.querySelector('select[aria-label=细节层级]').value"), '1')
      await evaluate(`${topFolder('目录008')}.click()`)
      await until(() => evaluate(`${topFolder('目录008')}.getAttribute('aria-expanded') === 'true' &&
        ${topFolder('目录001')}.getAttribute('aria-expanded') === 'false'`), '切换后收起整条旧分支')
      await cdp('Emulation.clearDeviceMetricsOverride')
      const { x, y } = await until(() => evaluate(`(() => {
        const area = document.querySelector('.space-map').getBoundingClientRect();
        const tiles = [...document.querySelectorAll('.space-tile:not(.nested)')].map(tile => tile.getBoundingClientRect());
        if (Math.abs(Math.max(...tiles.map(r => r.right)) - area.right) > 1 ||
            Math.abs(Math.max(...tiles.map(r => r.bottom)) - area.bottom) > 1) return null;
        const tile = ${topFolder('目录009')};
        const rect = tile.getBoundingClientRect();
        const x = Math.floor(rect.left + rect.width / 2), y = Math.floor(rect.top + rect.height / 2);
        return document.elementFromPoint(x, y) === tile ? { x, y } : null;
      })()`), '窗口尺寸恢复后定位双击测试目录')
      for (let clickCount = 1; clickCount <= 2; clickCount++) {
        await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount })
        await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount })
        if (clickCount === 1) {
          await until(() => evaluate(`(() => {
            const tile = ${topFolder('目录009')};
            const bounds = tile.getBoundingClientRect();
            return tile.getAttribute('aria-expanded') === 'true' &&
              [...document.querySelectorAll('.space-tile.nested')].some(child => {
                const r = child.getBoundingClientRect();
                return r.left > bounds.left && r.top > bounds.top && r.right < bounds.right && r.bottom < bounds.bottom;
              }) && document.elementFromPoint(${x}, ${y})?.closest('.space-tile.nested');
          })()`), '首击展开后，在同一位置点击会命中新子块')
        }
      }
      await until(() => evaluate("document.querySelector('.space-total strong')?.textContent === '92.72 MB'"), '下钻目录逻辑大小')
      await until(() => evaluate("document.querySelectorAll('.space-tile').length === 100"), '下钻文件矩形')
      assert.equal(await evaluate(`Array.from(document.querySelectorAll('.space-tile')).every(tile => {
        const rect = tile.getBoundingClientRect();
        return Math.abs(rect.width - parseFloat(tile.style.width)) < 0.1 &&
          Math.abs(rect.height - parseFloat(tile.style.height)) < 0.1;
      })`), true)
      await screenshot('space-analysis.png')
      await evaluate("document.querySelector('.space-breadcrumb button').click()")
      await until(() => evaluate("document.querySelector('.space-total strong')?.textContent === '487.79 MB'"), '面包屑返回卷根')
      const address = 'input[aria-label="目录路径"]'
      const readAddress = () => evaluate(`document.querySelector(${JSON.stringify(address)}).value`)
      const openAddress = (path, draft, pendingDrive) => evaluate(`(() => {
        const input = document.querySelector(${JSON.stringify(address)});
        const replaceAddress = value => {
          input.value = value;
          input.dispatchEvent(new InputEvent('input', { bubbles: true }));
        };
        const pendingDrive = ${JSON.stringify(pendingDrive ?? null)};
        if (pendingDrive) {
          document.querySelector('button[aria-label="分析磁盘"]').click();
          [...document.querySelectorAll('[role=listbox] [role=option]')]
            .find(option => option.textContent.trim().startsWith(pendingDrive)).click();
        }
        replaceAddress(${JSON.stringify(path)});
        input.form.requestSubmit();
        const draft = ${JSON.stringify(draft ?? null)};
        if (draft !== null) replaceAddress(draft);
      })()`)
      assert.equal(await readAddress(), 'C:\\')
      await openAddress('"c:\\演示\\目录009\\"')
      await until(() => evaluate("document.querySelector('.space-total strong')?.textContent === '92.72 MB'"), '粘贴地址下钻')
      assert.equal(await readAddress(), 'C:\\演示\\目录009')
      await openAddress('C:\\演示\\不存在')
      await until(() => evaluate("document.querySelector('.space-path-feedback')?.textContent.includes('找不到')"), '无效目录提示')
      assert.equal(await readAddress(), 'C:\\演示\\不存在')
      assert.equal(await evaluate("document.querySelector('.space-total strong').textContent"), '92.72 MB')
      await openAddress('无效地址', undefined, 'D:\\')
      await until(() => evaluate("document.querySelector('.space-path-feedback')?.textContent.includes('绝对目录路径')"), '处理切卷期间的无效地址')
      assert.match(await evaluate("document.querySelector('button[aria-label=分析磁盘]').textContent.trim()"), /^C:\\/)
      await openAddress('D:', 'C:\\下一条输入')
      await until(() => evaluate("document.querySelector('.space-message')?.textContent.includes('当前目录为空')"), '跨卷进入空目录')
      assert.equal(await readAddress(), 'C:\\下一条输入')
      await openAddress('D:')
      await until(async () => await readAddress() === 'D:\\', '确认后同步地址栏')

      const tileMenu = 'document.querySelector(".space-popover[aria-label=文件操作]:popover-open")'
      const excluded = 'document.querySelector("[popovertarget=space-exclusions]")'
      const restorePanel = 'document.querySelector("#space-exclusions:popover-open")'
      const menuLabels = () => evaluate(`Array.from(${tileMenu}.querySelectorAll('button'), button => button.textContent.trim())`)
      const excludeTile = () => evaluate(`${tileMenu}.querySelector('button:last-child').click()`)
      await openAddress('C:')
      await until(() => evaluate(`Boolean(${topFolder('目录009')})`), '返回测试目录')
      assert.match(await evaluate(`${topFolder('目录009')}.title`), /92\.72 MB$/)
      assert.match(await evaluate(`${topFolder('目录009')}.textContent`), /92\.72 MB \([\d.]+%\)/)
      const folderShadow = () => evaluate(`getComputedStyle(${topFolder('目录009')}).boxShadow`)
      const originalShadow = await folderShadow()
      await clickFolder('目录009')
      assert.equal(await folderShadow(), originalShadow)
      await evaluate(`${topFolder('目录009')}.dispatchEvent(new MouseEvent('contextmenu', { clientX: 150, clientY: 150, bubbles: true, cancelable: true }))`)
      await until(() => evaluate(`Boolean(${tileMenu})`), '目录右键操作')
      assert.deepEqual(await menuLabels(), ['打开', '打开所在目录', '复制完整路径', '临时排除'])
      await excludeTile()
      await until(() => evaluate(`!${topFolder('目录009')} && Boolean(${excluded})`), '隐藏目录并重新排版')
      assert.equal(await evaluate("document.querySelector('.space-total strong').textContent"), '395.07 MB')
      await openAddress('C:\\演示\\目录009')
      await until(() => evaluate("document.querySelector('.space-path-feedback')?.textContent.includes('已临时排除')"), '排除目录的地址反馈')
      await openAddress('D:')
      await until(async () => await readAddress() === 'D:\\', '切换至其他盘')
      await evaluate(`${excluded}.click()`)
      await until(() => evaluate(`Boolean(${restorePanel})`), '跨盘查看排除项')
      await evaluate(`${restorePanel}.querySelector('li button').click()`)
      await until(() => evaluate(`!${excluded}`), '恢复单个排除项')
      await openAddress('C:\\演示\\目录009')
      await until(() => evaluate("document.querySelectorAll('.space-tile').length === 100"), '恢复目录及文件')
      const firstFile = 'document.querySelector(".space-tile[data-tile-key]")'
      const openFileMenu = () => evaluate(`${firstFile}.dispatchEvent(new KeyboardEvent('keydown', {
        key: 'F10', shiftKey: true, bubbles: true, cancelable: true
      }))`)
      await openFileMenu()
      await until(() => evaluate(`Boolean(${tileMenu})`), '文件键盘菜单')
      assert.deepEqual(await menuLabels(), ['打开', '打开所在目录', '复制完整路径', '打开方式…', '临时排除'])
      await excludeTile()
      await until(() => evaluate("document.querySelectorAll('.space-tile').length === 99"), '文件排除')
      await evaluate(`${excluded}.click()`)
      await until(() => evaluate(`Boolean(${restorePanel})`), '打开恢复列表')
      await evaluate(`${restorePanel}.querySelector(':scope > button').click()`)
      await until(() => evaluate("document.querySelectorAll('.space-tile').length === 100"), '全部恢复')
      await openFileMenu()
      await until(() => evaluate(`Boolean(${tileMenu})`), '重新打开文件菜单')
      await excludeTile()
      await until(() => evaluate(`Boolean(${excluded})`), '再次排除文件')
      await evaluate("document.querySelector('button[aria-label=返回搜索]').click()")
      await evaluate("document.querySelector('button[aria-label=空间分析]').click()")
      await until(() => evaluate("document.querySelectorAll('.space-tile').length === 99"), '恢复原目录及排除项')
      assert.equal(await evaluate(`Boolean(${excluded})`), true)
      await evaluate("document.querySelector('button[aria-label=返回搜索]').click()")
      assert.equal(await evaluate(`(${resultCount})() === ${JSON.stringify(expected(total))}`), true)
      assert.equal(await evaluate("document.querySelector('.results-scroll').scrollTop"), scrollBeforeAnalysis)
    }
    await evaluate("document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))")
    assert.equal(await evaluate("document.querySelector('tbody tr[aria-selected=true]')?.id"), 'result-0')
    await evaluate("document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))")
    assert.equal(await evaluate("document.querySelector('tbody tr[aria-selected=true]')?.id"), 'result-1')
    await evaluate("document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'f', ctrlKey: true, bubbles: true }))")
    assert.equal(await evaluate("document.activeElement.getAttribute('aria-label')"), '搜索文件和文件夹')
    await evaluate("document.querySelector('tbody tr').dispatchEvent(new MouseEvent('contextmenu', { clientX: 150, clientY: 115, bubbles: true, cancelable: true }))")
    await until(() => evaluate("document.querySelectorAll('[role=menuitem]').length === 3"), '右键菜单')
    await evaluate("document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))")
    assert.equal(await evaluate("document.querySelector('[role=menu]') === null"), true)
    await evaluate("document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))")
    await until(() => evaluate("document.querySelector('.operation-feedback.error')?.textContent.length > 1"), '打开不存在的结果反馈')
    await evaluate("document.querySelector('button[aria-label=关闭提示]').click()")
    if (mode === '--desktop-test') {
      await evaluate("document.querySelector('tbody tr').dispatchEvent(new MouseEvent('contextmenu', { clientX: 150, clientY: 115, bubbles: true, cancelable: true }))")
      await until(() => evaluate("document.querySelectorAll('[role=menuitem]').length === 3"), '重新打开结果菜单')
      await evaluate("[...document.querySelectorAll('[role=menuitem]')].find(button => button.textContent.trim() === '打开所在目录').click()")
      await until(() => evaluate("document.querySelector('.operation-feedback.error')?.textContent.length > 1"), '定位不存在的文件反馈')
      await evaluate("document.querySelector('button[aria-label=关闭提示]').click()")
    }
    await screenshot('desktop.png')
    await evaluate("document.querySelector('.index-toggle').click()")
    await until(() => evaluate("document.querySelector('#index-panel .volume-list li')?.textContent.includes('C:')"), '逐卷索引状态')
    if (mode === '--desktop-test') {
      const preference = "document.querySelector('.index-exit-preference input')"
      assert.equal(await evaluate(`${preference}.checked`), true)
      await evaluate(`${preference}.click()`)
      await until(() => evaluate(`!${preference}.checked && !${preference}.disabled`), '关闭退出保存开关')
      assert.equal(JSON.parse(await readFile(settingsPath, 'utf8')).SkipExitIndexSave, false)
      await evaluate(`${preference}.click()`)
      await until(() => evaluate(`${preference}.checked && !${preference}.disabled`), '开启退出时不保存索引')
    }
    await cdp('Emulation.setDeviceMetricsOverride', { width: 660, height: 420, deviceScaleFactor: 1, mobile: false })
    assert.equal(await evaluate("document.querySelector('.results-scroll').scrollWidth === document.querySelector('.results-scroll').clientWidth"), true)
    assert.equal(await evaluate("document.querySelector('#index-panel').getBoundingClientRect().bottom < window.innerHeight"), true)
    await screenshot('desktop-compact.png')
    await evaluate("document.querySelector('button[aria-label=关闭索引状态]').click()")
    const columnWidths = () => evaluate("Array.from(document.querySelectorAll('th'), header => header.offsetWidth)")
    const initialWidths = await columnWidths()
    for (let column = 0; column < initialWidths.length; column++) {
      for (const delta of [-20, 20]) {
        const handle = await evaluate(`(() => {
          const resizer = document.querySelectorAll('th')[${column}].querySelector('.column-resizer');
          if (!resizer) return null;
          const rect = resizer.getBoundingClientRect();
          return {x: rect.left + rect.width / 2, y: rect.top + rect.height / 2};
        })()`)
        assert.ok(handle, `缺少第 ${column + 1} 列的拖动手柄`)
        const before = await columnWidths()
        await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', ...handle, button: 'left', buttons: 1, clickCount: 1 })
        await cdp('Input.dispatchMouseEvent', { type: 'mouseMoved', ...handle, x: handle.x + delta, button: 'left', buttons: 1 })
        await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', ...handle, x: handle.x + delta, button: 'left', clickCount: 1 })
        const after = await until(async () => {
          const widths = await columnWidths()
          return Math.abs(widths[column] - before[column] - delta) <= 1 ? widths : undefined
        }, `第 ${column + 1} 列调整宽度`)
        for (let index = 0; index < after.length; index++) {
          if (index !== column) assert.ok(Math.abs(after[index] - before[index]) <= 1, `第 ${index + 1} 列跟随拖动改变`)
        }
      }
    }
    assert.equal(await evaluate("document.querySelector('th:nth-child(3)').getAttribute('aria-sort')"), 'descending')

  }

  const backendId = async () => Number((await powerShell(`
    @(Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq ${application.pid} -and $_.Name -eq 'NtfsSearch.Backend.exe' } | ForEach-Object ProcessId) -join ','
  `)).stdout.trim())
  const backend = await backendId()
  assert.ok(backend, '未找到桌面程序的 .NET 子进程')
  if (trayTest) {
    const hwnd = (await powerShell(`(Get-Process -Id ${application.pid}).MainWindowHandle.ToInt64()`)).stdout.trim()
    assert.notEqual(hwnd, '0', '启动时显示搜索窗口')
    const visible = () => mainWindowAction(hwnd, 'visible')
    const queryText = 'ntfs-absent-tray-fixture'
    await query(queryText, 0)
    assert.equal(await mainWindowAction(hwnd, 'close'), true)
    await until(async () => !(await visible()), '窗口关闭按钮隐藏窗口')
    assert.equal(await backendId(), backend, '隐藏后保留原搜索后端')
    const filters = {
      text: '000003', minSizeBytes: 3072, maxSizeBytes: 3072,
      createdFrom: '2025-12-31T00:03:00Z', modifiedFrom: '2026-01-01T00:03:00Z',
    }
    const response = await fetch(`http://127.0.0.1:${apiPort}/v1/search`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(filters), signal: AbortSignal.timeout(5000),
    })
    assert.equal(response.status, 200, '窗口隐藏时 HTTP 查询可用')
    const hiddenResult = await response.json()
    assert.equal(hiddenResult.total, 1)
    assert.equal(hiddenResult.rows[0].createdUtc, filters.createdFrom)
    assert.equal(hiddenResult.rows[0].modifiedUtc, filters.modifiedFrom)
    assert.equal(hiddenResult.rows[0].sizeBytes, 3072)
    const cli = join(dirname(resolve(executable)), 'NtfsSearch.Cli.exe')
    const { stdout } = await exec(cli, [
      'search', filters.text, '--min-size-bytes', '3072', '--max-size-bytes', '3072',
      '--created-from', filters.createdFrom, '--modified-from', filters.modifiedFrom,
    ], { encoding: 'utf8', env: applicationEnv, timeout: 10_000 })
    assert.deepEqual(JSON.parse(stdout), hiddenResult, '窗口隐藏时 CLI 查询相同索引')
    await exec(resolve(executable), applicationArgs, { cwd: output, env: applicationEnv, timeout: 10_000 })
    await until(visible, '再次启动同一便携版恢复原窗口')
    assert.equal(await evaluate("document.querySelector('.search-field input').value"), queryText)
    assert.equal(await backendId(), backend, '再次启动复用原搜索后端')
  }
  // 使用现有桌面通信完成退出握手，避免测试依赖通知区图标是否被 Windows 折叠。
  await quitApplication()
  const leftover = await powerShell(`if (Get-Process -Id ${backend} -ErrorAction SilentlyContinue) { 'running' }`)
  assert.equal(leftover.stdout.trim(), '')
  if (trayTest) {
    const snapshot = await readFile(snapshotPath)
    assert.match(snapshot.toString('ascii', 0, 8), /^NTSEXIT\d$/, '正常退出保存了演示快照')
  }
  if (exitSaveSkipTest) {
    const snapshot = await readFile(snapshotPath)
    assert.match(snapshot.toString('ascii', 0, 8), /^NTSEXIT\d$/, '下次退出仍保存索引')
    assert.ok(!snapshot.equals(snapshotBeforeSkip), '下次退出原子替换旧快照')
  }
  if (mode === '--desktop-test') {
    const restart = exec(join(dirname(resolve(executable)), 'NtfsSearch.Backend.exe'), ['--desktop-test'],
      { encoding: 'utf8', timeout: 10_000, env: { ...process.env, NTFS_SEARCH_TEST_DATA: testData } })
    restart.child.stdin.end('{"type":"quit"}\n')
    const { stdout } = await restart
    const messages = stdout.trim().split(/\r?\n/).map(line => JSON.parse(line))
    assert.equal(messages.find(message => message.type === 'status')?.skipExitIndexSave, true)
    assert.equal(messages.some(message => message.type === 'saving'), false)
    assert.equal(messages.find(message => message.type === 'quit-complete')?.error, null)
    assert.deepEqual(await readFile(snapshotPath), previousSnapshot)
  }
  if (trayTest) console.log('Windows 关窗隐藏、HTTP/CLI 查询、重复启动复用及退出快照通过。')
  else if (searchResultsTest) console.log('Windows 搜索匹配加粗、列宽、横向和纵向滚动通过。')
  else if (desktopInteractionTest) console.log('Windows 前台聚焦、结果多选与框选通过。')
  else if (nativeFileDragTest) console.log('Windows 向浏览器拖出跨目录多选文件通过。')
  else if (searchServiceSettingsTest) console.log('Windows 搜索服务设置、监听控制及返回原视图通过。')
  else if (indexMemorySettingsTest) console.log('Windows 预计算排序即时切换、元数据列重启生效和文件夹大小偏好通过。')
  else if (exitSaveSkipTest) console.log('Windows 退出保存按钮、状态、旧快照保留及再次退出保存通过。')
  else if (spaceFlashTest) console.log('Windows 空间分析自动及手动展开、同批多处闪烁、收起回退通过。')
  else if (spaceInteractionTest) console.log('Windows 空间分析右键、返回搜索后恢复会话以及前台激活通过。')
  else if (spaceCapacitySettingsTest) console.log('Windows 磁盘容量、背景比例、键盘选盘和跨标签保存通过。')
  else console.log(benchmark ? 'Windows 桌面性能采样与退出通过。' : 'Windows 桌面搜索、交互和退出通过。')
} finally {
  if (application.exitCode == null) {
    if (socket?.readyState === WebSocket.OPEN) await requestQuit().catch(() => {})
    await Promise.race([exited, sleep(4000)])
    if (application.exitCode == null) application.kill()
  }
  socket?.close()
}
