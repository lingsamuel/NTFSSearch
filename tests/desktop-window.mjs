import assert from 'node:assert/strict'
import { execFile, spawn } from 'node:child_process'
import { once } from 'node:events'
import { mkdir, writeFile } from 'node:fs/promises'
import { createServer } from 'node:net'
import { join, resolve } from 'node:path'
import { setTimeout as sleep } from 'node:timers/promises'
import { promisify } from 'node:util'

const [executable, artifacts, mode = '--desktop-test'] = process.argv.slice(2)
if (!executable || !artifacts) throw new Error('用法：node tests/desktop-window.mjs <exe> <报告目录> [--desktop-benchmark | --cached-fixture]')

const output = resolve(artifacts)
await mkdir(output, { recursive: true })
const portProbe = createServer().listen(0, '127.0.0.1')
await once(portProbe, 'listening')
const port = portProbe.address().port
await new Promise(resolve => portProbe.close(resolve))
const application = spawn(resolve(executable), mode === '--cached-fixture' ? [] : [mode], {
  stdio: 'ignore',
  env: {
    ...process.env,
    WEBVIEW2_USER_DATA_FOLDER: join(output, 'WebView'),
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}`,
  }
})
const exited = once(application, 'exit')
const exec = promisify(execFile)
const powerShell = script => exec('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand',
  Buffer.from(script, 'utf16le').toString('base64')], { encoding: 'utf8' })

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

try {
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
  const benchmark = mode === '--desktop-benchmark'
  const total = benchmark ? 1_000_000 : 1000
  await until(async () => {
    const error = await evaluate("document.querySelector('.startup-error span')?.textContent")
    if (error) throw new Error(error)
    return await evaluate(`document.querySelector('.result-count') && (${resultCount})() === ${JSON.stringify(expected(total))}`)
  }, '初始精确计数', 90_000)
  assert.equal(await evaluate("document.activeElement.getAttribute('aria-label')"), '搜索文件和文件夹')
  assert.equal(await evaluate("document.querySelectorAll('tbody tr').length"), 200)

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
  } else {
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
    await until(() => evaluate("document.querySelector('th:nth-child(3)').getAttribute('aria-sort') === 'descending' && document.querySelector('tbody tr td:nth-child(3)').textContent === '999 KB' && document.querySelector('tbody tr td:nth-child(3)').title === '1,022,976 B'"), '大小降序及精确字节数')

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
      await until(() => evaluate("document.querySelector('.space-total')?.title === '511,488,000 B'"), '卷根逻辑大小')
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
      await cdp('Emulation.setDeviceMetricsOverride', { width: 480, height: 170, deviceScaleFactor: 1, mobile: false })
      await until(() => evaluate("document.querySelectorAll('.space-tile.nested').length === 0"), '狭小空间自动收起细节')
      await cdp('Emulation.clearDeviceMetricsOverride')
      await until(() => evaluate("document.querySelectorAll('.space-tile.nested').length > 0"), '尺寸恢复后重新显示细节')
      await evaluate(`(() => {
        const level = document.querySelector('select[aria-label=细节层级]');
        level.value = '1'; level.dispatchEvent(new Event('change', { bubbles: true }));
      })()`)
      await until(() => evaluate("document.querySelectorAll('.space-tile.nested').length === 0"), '细节降到一层')
      await evaluate(`${topFolder('目录009')}.click()`)
      await until(() => evaluate(`${topFolder('目录009')}.getAttribute('aria-expanded') === 'true' &&
        document.querySelectorAll('.space-tile.nested').length > 0`), '单击临时展开')
      assert.equal(await evaluate("document.querySelector('.space-total').title"), '511,488,000 B')
      await evaluate(`${topFolder('目录008')}.click()`)
      await until(() => evaluate(`${topFolder('目录008')}.getAttribute('aria-expanded') === 'true' &&
        ${topFolder('目录009')}.getAttribute('aria-expanded') === 'false'`), '切换时收起原目录')
      await evaluate(`${topFolder('目录008')}.click()`)
      await until(() => evaluate("document.querySelectorAll('.space-tile.nested').length === 0"), '再次单击收起')
      const { x, y } = await evaluate(`(() => {
        const rect = ${topFolder('目录009')}.getBoundingClientRect();
        return { x: Math.floor(rect.left + 12), y: Math.floor(rect.top + 10) };
      })()`)
      for (let clickCount = 1; clickCount <= 2; clickCount++) {
        await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount })
        await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount })
      }
      await until(() => evaluate("document.querySelector('.space-total')?.title === '97,228,800 B'"), '下钻目录逻辑大小')
      await until(() => evaluate("document.querySelectorAll('.space-tile').length === 100"), '下钻文件矩形')
      assert.equal(await evaluate(`Array.from(document.querySelectorAll('.space-tile')).every(tile => {
        const rect = tile.getBoundingClientRect();
        return Math.abs(rect.width - parseFloat(tile.style.width)) < 0.1 &&
          Math.abs(rect.height - parseFloat(tile.style.height)) < 0.1;
      })`), true)
      await screenshot('space-analysis.png')
      await evaluate("document.querySelector('.space-breadcrumb button').click()")
      await until(() => evaluate("document.querySelector('.space-total')?.title === '511,488,000 B'"), '面包屑返回卷根')
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
          const drive = document.querySelector('select[aria-label="分析磁盘"]');
          drive.value = pendingDrive;
          drive.dispatchEvent(new Event('change', { bubbles: true }));
        }
        replaceAddress(${JSON.stringify(path)});
        input.form.requestSubmit();
        const draft = ${JSON.stringify(draft ?? null)};
        if (draft !== null) replaceAddress(draft);
      })()`)
      assert.equal(await readAddress(), 'C:\\')
      await openAddress('"c:\\演示\\目录009\\"')
      await until(() => evaluate("document.querySelector('.space-total')?.title === '97,228,800 B'"), '粘贴地址下钻')
      assert.equal(await readAddress(), 'C:\\演示\\目录009')
      await openAddress('C:\\演示\\不存在')
      await until(() => evaluate("document.querySelector('.space-path-feedback')?.textContent.includes('找不到')"), '无效目录提示')
      assert.equal(await readAddress(), 'C:\\演示\\不存在')
      assert.equal(await evaluate("document.querySelector('.space-total').title"), '97,228,800 B')
      await openAddress('无效地址', undefined, 'D:\\')
      await until(() => evaluate("document.querySelector('.space-path-feedback')?.textContent.includes('绝对目录路径')"), '处理切卷期间的无效地址')
      assert.equal(await evaluate("document.querySelector('select[aria-label=分析磁盘]').value"), 'C:\\')
      await openAddress('D:', 'C:\\下一条输入')
      await until(() => evaluate("document.querySelector('.space-message')?.textContent.includes('当前目录为空')"), '跨卷进入空目录')
      assert.equal(await readAddress(), 'C:\\下一条输入')
      await openAddress('D:')
      await until(async () => await readAddress() === 'D:\\', '确认后同步地址栏')
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
    await screenshot('desktop.png')
    await evaluate("document.querySelector('.index-toggle').click()")
    await until(() => evaluate("document.querySelector('#index-panel .volume-list li')?.textContent.includes('C:')"), '逐卷索引状态')
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

  const processIds = await powerShell(`
    @(Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq ${application.pid} -and $_.Name -eq 'NtfsSearch.Backend.exe' } | ForEach-Object ProcessId) -join ','
  `)
  const backend = Number(processIds.stdout.trim())
  assert.ok(backend, '未找到桌面程序的 .NET 子进程')
  await powerShell(`(Get-Process -Id ${application.pid}).CloseMainWindow() | Out-Null`)
  const [code] = await Promise.race([exited, sleep(8000, undefined, { ref: false }).then(() => { throw new Error('桌面窗口未退出') })])
  assert.equal(code, 0)
  const leftover = await powerShell(`if (Get-Process -Id ${backend} -ErrorAction SilentlyContinue) { 'running' }`)
  assert.equal(leftover.stdout.trim(), '')
  console.log(benchmark ? 'Windows 桌面性能采样与进程退出通过。' : 'Windows 桌面启动、搜索、排序、分页、文件图标、键盘、右键、窄窗口、四列拖动、大小单位和退出通过。')
} finally {
  socket?.close()
  if (application.exitCode == null) {
    await powerShell(`(Get-Process -Id ${application.pid} -ErrorAction SilentlyContinue).CloseMainWindow() | Out-Null`).catch(() => {})
    await Promise.race([exited, sleep(4000)])
    if (application.exitCode == null) application.kill()
  }
}
