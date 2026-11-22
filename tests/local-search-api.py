"""Exercise the HTTP and CLI against the same desktop backend index."""

from contextlib import ExitStack
import base64
import json
import os
from pathlib import Path
import queue
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request


http = urllib.request.build_opener(urllib.request.ProxyHandler({}))
INDEX_DEFAULTS = dict(precomputeSort=True, indexCreationTime=False, indexModifiedTime=True, indexFileSize=True)


def port_socket():
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    return listener


def request(port, path, body=None):
    url = f"http://127.0.0.1:{port}/v1/{path}"
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"} if data is not None else {}
    with http.open(urllib.request.Request(url, data, headers), timeout=3) as response:
        return json.load(response)


def send(process, message):
    process.stdin.write(json.dumps(message) + "\n")
    process.stdin.flush()


def desktop(command, env, check):
    events = queue.Queue()
    with subprocess.Popen(command + ["--desktop-test"], stdin=subprocess.PIPE,
                          stdout=subprocess.PIPE, encoding="utf-8", env=env) as process:
        def read_events():
            for line in process.stdout:
                events.put(json.loads(line))
            events.put(None)

        threading.Thread(target=read_events, daemon=True).start()

        def receive(predicate):
            deadline = time.monotonic() + 15
            while True:
                event = events.get(timeout=max(0, deadline - time.monotonic()))
                assert event is not None, "桌面后端提前退出"
                assert event.get("scope") != "fatal", event
                if predicate(event):
                    return event

        try:
            check(process, receive)
            send(process, {"type": "quit"})
            finished = receive(lambda event: event["type"] == "quit-complete")
            assert finished.get("error") is None, finished
            assert process.wait(timeout=10) == 0, process.returncode
        finally:
            if process.poll() is None:
                process.kill()


def check_live_api(process, receive, port, run_cli, connections):
    receive(lambda event: event["type"] == "result" and event["total"] == 1000 and event["volumesReady"] == 2)
    try:
        status = request(port, "status")
    except urllib.error.URLError as error:
        raise AssertionError("演示后端未提供本机 HTTP 接口") from error
    assert status["indexed"] == 1000 and status["volumesReady"] == 2, status
    assert {v["root"] for v in status["volumes"] if v["isReady"]} == {"C:\\", "D:\\"}
    assert json.loads(run_cli("status").stdout) == status

    text = '"hello world"'
    send(process, dict(type="query", requestId=1, text=text, sort=2, descending=True, offset=3))
    desktop_result = receive(lambda event: event["type"] == "result" and event["requestId"] == 1)
    result = request(port, "search", dict(text=text, sort="size", descending=True, offset=3, limit=5))
    assert result["total"] == desktop_result["total"] == 59
    assert result["volumesReady"] == 2 and result["offset"] == 3 and result["limit"] == 5
    assert [row["fullPath"] for row in result["rows"]] == [row["fullPath"] for row in desktop_result["rows"][:5]]
    assert [row["pathKey"] for row in result["rows"]] == [row["key"] for row in desktop_result["rows"][:5]]
    assert json.loads(run_cli("search", text, "--sort", "size", "--descending", "--offset", "3", "--limit", "5").stdout) == result
    assert request(port, "search", dict(text="目录009", matchPath=False))["total"] == 0

    filters = dict(minSizeBytes=1024, maxSizeBytes=5120,
                   modifiedFrom="2026-01-01T08:02:00+08:00", modifiedBefore="2026-01-01T00:06:00Z",
                   createdFrom="2025-12-31T00:00:00Z", createdBefore="2025-12-31T00:05:00Z")
    result = request(port, "search", filters | dict(sort="size", offset=1, limit=1))
    path = "C:\\演示\\目录000\\文件 report 000003.json"
    assert result == dict(total=3, volumesReady=2, offset=1, limit=1, rows=[dict(
        name="文件 report 000003.json", fullPath=path, pathKey=base64.b64encode(path.encode("utf-16-le")).decode(),
        isDirectory=False, sizeBytes=3072, createdUtc="2025-12-31T00:03:00Z", modifiedUtc="2026-01-01T00:03:00Z")])
    filtered_cli = run_cli("search", "--sort", "size", "--offset", "1", "--limit", "1",
                           "--min-size-bytes", "1024", "--max-size-bytes", "5120",
                           "--modified-from", filters["modifiedFrom"], "--modified-before", filters["modifiedBefore"],
                           "--created-from", filters["createdFrom"], "--created-before", filters["createdBefore"])
    assert json.loads(filtered_cli.stdout) == result
    directories = request(port, "search", dict(text="深层", matchPath=False))
    assert directories["total"] == 5
    for row in directories["rows"]:
        assert row["isDirectory"] and row["sizeBytes"] is None and row["createdUtc"] is None and row["modifiedUtc"] is None
    assert request(port, "search", dict(text="深层", matchPath=False, minSizeBytes=0))["total"] == 0
    assert request(port, "search", dict(text="深层", matchPath=False, createdBefore="2026-01-01T00:00:00Z"))["total"] == 0
    assert request(port, "search", dict(minSizeBytes=1024, maxSizeBytes=1024, limit=0))["total"] == 1
    assert request(port, "search", dict(offset=1000))["rows"] == []

    for invalid in [dict(limit=1001), dict(offset=-1), dict(sort="invalid"), dict(minSizeBytes=2, maxSizeBytes=1),
                    dict(createdBefore="2026-01-01T00:00:00"), dict(modifiedFrom="2026-01-02T00:00:00Z", modifiedBefore="2026-01-01T00:00:00Z")]:
        try:
            request(port, "search", invalid)
        except urllib.error.HTTPError as error:
            assert error.code == 400 and json.load(error)["error"], invalid
        else:
            raise AssertionError(f"接口接受了无效查询 {invalid}")

    # 服务器开始读请求体后发送 100 Continue，此时每个未写完的请求已占据一个查询名额。
    active = [hold_search_request(connections, port) for _ in range(4)]
    try:
        request(port, "search", {})
    except urllib.error.HTTPError as error:
        assert error.code == 503 and json.load(error)["error"]
    else:
        raise AssertionError("四个查询进行时仍接受了新的查询")
    assert request(port, "status")["volumesReady"] == 2
    active[0].close()
    deadline = time.monotonic() + 3
    while True:
        try:
            assert request(port, "search", {"limit": 0})["total"] == 1000
            break
        except urllib.error.HTTPError as error:
            assert error.code == 503 and time.monotonic() < deadline, "断开的请求未释放查询名额"
            error.close()
            time.sleep(0.01)
    # 剩余连接保持打开，直到桌面退出并完成快照保存。


def hold_search_request(connections, port):
    client = connections.enter_context(socket.create_connection(("127.0.0.1", port), timeout=3))
    client.sendall((f"POST /v1/search HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n"
                    "Content-Type: application/json\r\nContent-Length: 2\r\nExpect: 100-continue\r\n\r\n").encode())
    with client.makefile("rb") as reply:
        assert reply.readline().startswith(b"HTTP/1.1 100 "), "正在处理的搜索请求未开始读请求体"
    return client


def check_port_collision(process, receive):
    error = receive(lambda event: event["type"] == "error")
    assert error["scope"] == "action" and "HTTP" in error["message"], error
    send(process, dict(type="query", requestId=1, text='"hello world"'))
    result = receive(lambda event: event["type"] == "result" and event["requestId"] == 1 and event["total"] == 59)
    assert len(result["rows"]) == 59


def service_status(receive):
    return receive(lambda event: event["type"] == "settingsStatus" and not event["busy"])


def change_service(process, receive, action, **values):
    send(process, dict(type=action, **values))
    pending = receive(lambda event: event["type"] == "settingsStatus")
    assert pending["busy"], pending
    return service_status(receive)


def assert_unavailable(port):
    try:
        request(port, "status")
    except urllib.error.URLError as error:
        assert isinstance(error.reason, ConnectionRefusedError), error
    else:
        raise AssertionError(f"端口 {port} 仍在提供 HTTP 服务")


def check_directory(process, receive, request_id, known):
    send(process, dict(type="query", requestId=request_id, text="深层1", matchPath=False,
                       sort=2, descending=True))
    result = receive(lambda event: event["type"] == "result" and event["requestId"] == request_id
                     and not event["directorySortPending"] and event["total"] == 1)
    row, = result["rows"]
    assert row["isDirectory"]
    assert row["sizeBytesText"] == ("15,308,800 B" if known else None), row
    assert row["sizeHint"] is None, row

def check_settings(backend, cli, sandbox):
    # 便携版 CLI 从自身目录读取设置，即使从其他目录调用也应如此。
    portable = Path(sandbox) / "portable"
    portable.mkdir()
    cli_entry = Path(cli[-1]).resolve()
    for entry in cli_entry.parent.iterdir():
        if entry.is_file() and (entry.suffix in {".dll", ".json", ".exe"} or entry == cli_entry):
            shutil.copy2(entry, portable)
    portable_cli = cli[:-1] + [str(portable / cli_entry.name)]
    data = portable
    legacy = portable / "Data"
    legacy.mkdir()
    legacy_settings = legacy / "settings.json"
    legacy_settings.write_text(json.dumps(dict(SearchServiceEnabled=True, SearchServicePort=1)), encoding="utf-8")
    settings = data / "settings.json"
    settings.write_text(json.dumps(dict(SkipExitIndexSave=True)), encoding="utf-8")
    env = os.environ.copy()
    env.pop("NTFS_SEARCH_PORT", None)
    env["NTFS_SEARCH_TEST_DATA"] = str(data)

    def run_cli(*args, **overrides):
        return subprocess.run(portable_cli + list(args), env=env | overrides, capture_output=True,
                              encoding="utf-8", errors="replace", timeout=15, check=True)

    with port_socket() as first, port_socket() as second, port_socket() as occupied:
        first_port = first.getsockname()[1]
        second_port = second.getsockname()[1]
        occupied_port = occupied.getsockname()[1]
        occupied.listen()
        first.close()
        second.close()

        def apply(process, receive, port, enabled=True, sizes=True, skip=True):
            return change_service(process, receive, "applySettings", enabled=enabled, port=port,
                                  searchDirectorySizes=sizes, skipExitIndexSave=skip)

        def assert_status(status, port, enabled, running, error=False, override=None, sizes=True, skip=True):
            assert status == dict(type="settingsStatus", searchServiceAvailable=True, enabled=enabled, port=port,
                                  address=f"http://127.0.0.1:{override or port}", portOverridden=override is not None,
                                  searchDirectorySizes=sizes, skipExitIndexSave=skip,
                                  running=running, busy=False, error=status["error"] if error else None,
                                  indexRestartRequired=False, **INDEX_DEFAULTS), status
            assert bool(status["error"]) == error, status

        def configure(process, receive):
            assert_status(service_status(receive), 18791, False, False)
            receive(lambda event: event["type"] == "result" and event["total"] == 1000)
            send(process, dict(type="getSettings"))
            assert_status(service_status(receive), 18791, False, False)
            assert_status(apply(process, receive, first_port), first_port, True, True)
            assert json.loads(run_cli("status").stdout)["indexed"] == 1000
            stored = settings.read_bytes()

            assert_status(apply(process, receive, 0), first_port, True, True, error=True)
            assert_status(apply(process, receive, occupied_port, sizes=False, skip=False), first_port, True, True, error=True)
            assert settings.read_bytes() == stored and request(first_port, "status")["volumesReady"] == 2

            settings.unlink()
            settings.mkdir()
            try:
                assert_status(apply(process, receive, second_port), first_port, True, True, error=True)
                assert request(first_port, "status")["indexed"] == 1000
                assert_unavailable(second_port)
            finally:
                settings.rmdir()
                settings.write_bytes(stored)

            assert_status(apply(process, receive, second_port), second_port, True, True)
            assert_unavailable(first_port)
            assert json.loads(run_cli("status").stdout)["indexed"] == 1000
            with ExitStack() as connections:
                hold_search_request(connections, second_port)
                restarted = change_service(process, receive, "restartSearchService")
            assert_status(restarted, second_port, True, True)
            assert request(second_port, "search", dict(text='"hello world"'))["total"] == 59
            send(process, dict(type="query", requestId=1, text='"hello world"'))
            receive(lambda event: event["type"] == "result" and event["requestId"] == 1 and event["total"] == 59)
            assert json.loads(settings.read_bytes())["SkipExitIndexSave"], "保存搜索服务设置覆盖了退出偏好"
            check_directory(process, receive, 2, known=True)
            assert_status(apply(process, receive, second_port, sizes=False, skip=False),
                          second_port, True, True, sizes=False, skip=False)
            check_directory(process, receive, 3, known=False)
            assert request(second_port, "status")["indexed"] == 1000

        def restore(process, receive):
            assert (data / "index.ntsnap").read_bytes().startswith(b"NTSEXIT")
            assert_status(service_status(receive), second_port, True, True, sizes=False, skip=False)
            receive(lambda event: event["type"] == "result" and event["total"] == 1000)
            assert json.loads(run_cli("status").stdout)["indexed"] == 1000
            check_directory(process, receive, 1, known=False)
            send(process, dict(type="setSkipExitIndexSave", skipExitIndexSave=True))
            assert receive(lambda event: event["type"] == "exitSavePreference")["skipExitIndexSave"]
            send(process, dict(type="getSettings"))
            assert_status(service_status(receive), second_port, True, True, sizes=False, skip=True)
            assert_status(apply(process, receive, second_port, enabled=False), second_port, False, False)
            check_directory(process, receive, 2, known=True)
            assert_unavailable(second_port)

        def check_disabled(process, receive):
            assert_status(service_status(receive), second_port, False, False)
            assert_unavailable(second_port)

        def check_override(process, receive):
            assert_status(service_status(receive), second_port, False, False, override=first_port)
            receive(lambda event: event["type"] == "result" and event["total"] == 1000)
            assert_status(apply(process, receive, occupied_port), occupied_port, True, True, override=first_port)
            assert json.loads(run_cli("status", NTFS_SEARCH_PORT=str(first_port)).stdout)["indexed"] == 1000
            assert_status(apply(process, receive, occupied_port, enabled=False), occupied_port, False, False, override=first_port)
            assert_unavailable(first_port)

        def check_invalid_override(process, receive):
            initial = service_status(receive)
            assert initial["address"] is None and "NTFS_SEARCH_PORT" in initial["error"], initial
            disabled = apply(process, receive, first_port, enabled=False)
            assert disabled == dict(type="settingsStatus", searchServiceAvailable=True, enabled=False, port=first_port, address=None,
                                    portOverridden=True, running=False, busy=False, error=None,
                                    searchDirectorySizes=True, skipExitIndexSave=True,
                                    indexRestartRequired=False, **INDEX_DEFAULTS), disabled
            assert json.loads(settings.read_bytes())["SearchServicePort"] == first_port

        def queue_exit_settings(process, receive):
            assert service_status(receive)["skipExitIndexSave"] is False
            receive(lambda event: event["type"] == "result" and event["total"] == 1000)
            # 在退出前送入一批即时保存，最后一次选择不写快照。
            batch = [dict(type="setSkipExitIndexSave", skipExitIndexSave=bool(i % 2)) for i in range(32)]
            process.stdin.write("".join(json.dumps(event) + "\n" for event in batch))
            process.stdin.flush()

        desktop(backend, env, configure)
        desktop(backend, env, restore)
        desktop(backend, env, check_disabled)
        desktop(backend, env | {"NTFS_SEARCH_PORT": str(first_port)}, check_override)
        # 无效环境变量只在启用监听时报告启动错误。
        settings.write_text(json.dumps(json.loads(settings.read_bytes()) | {"SearchServiceEnabled": True}), encoding="utf-8")
        desktop(backend, env | {"NTFS_SEARCH_PORT": "invalid"}, check_invalid_override)
        settings.write_text(json.dumps(json.loads(settings.read_bytes()) | {"SkipExitIndexSave": False}), encoding="utf-8")
        snapshot = data / "index.ntsnap"
        original_snapshot = snapshot.read_bytes()
        desktop(backend, env, queue_exit_settings)
        assert json.loads(settings.read_bytes())["SkipExitIndexSave"] is True, "退出时丢弃了已排队的设置"
        assert snapshot.read_bytes() == original_snapshot, "退出没有遵守最新保存的偏好"
        assert json.loads(legacy_settings.read_bytes())["SearchServicePort"] == 1


def check_index_settings(backend, cli, sandbox):
    data = Path(sandbox) / "index-settings"
    data.mkdir()
    with port_socket() as listener:
        port = listener.getsockname()[1]
    env = os.environ | {"NTFS_SEARCH_TEST_DATA": str(data), "NTFS_SEARCH_PORT": str(port)}
    settings = dict(precomputeSort=False, indexCreationTime=True, indexModifiedTime=False, indexFileSize=False)
    text = '"hello world"'
    time_filter = "2026-01-01T00:00:00Z"

    def apply(process, receive, **values):
        return change_service(process, receive, "applySettings", enabled=True, port=18791,
                              searchDirectorySizes=True, skipExitIndexSave=True, **values)

    def check_preferences(status, expected, restart_required):
        assert {key: status.get(key) for key in expected} == expected, status
        assert status["indexRestartRequired"] is restart_required and status["searchDirectorySizes"], status
        assert status["error"] is None, status

    def ready(receive, size, modified):
        status = receive(lambda event: event["type"] == "status" and event["volumesReady"] == 2)
        assert status["indexFileSize"] is size and status["indexModifiedTime"] is modified, status

    def reject(query, setting):
        try:
            request(port, "search", query)
        except urllib.error.HTTPError as error:
            with error:
                assert error.code == 400 and setting in json.load(error)["error"], query
        else:
            raise AssertionError(f"接口接受了依赖关闭设置的查询 {query}")

    def configure(process, receive):
        check_preferences(service_status(receive), INDEX_DEFAULTS, False)
        ready(receive, size=True, modified=True)
        reject(dict(createdFrom=time_filter), "索引创建时间")
        row = request(port, "search", dict(text=text, limit=1))["rows"][0]
        assert row["sizeBytes"] is not None and row["modifiedUtc"] and row["createdUtc"] is None, row

        ordered_query = dict(text=text, sort="size", descending=True, offset=6, limit=9)
        ordered = request(port, "search", ordered_query)
        for precompute in (False, True, False, True):
            check_preferences(apply(process, receive, precomputeSort=precompute),
                              INDEX_DEFAULTS | dict(precomputeSort=precompute), False)
            persisted = json.loads((data / "settings.json").read_bytes())
            assert persisted["PrecomputeSort"] is precompute, persisted
            assert request(port, "search", ordered_query) == ordered

        check_preferences(apply(process, receive, **settings), settings, True)
        check_preferences(apply(process, receive, **INDEX_DEFAULTS), INDEX_DEFAULTS, False)
        check_preferences(apply(process, receive, **settings), settings, True)
        # 旧客户端保存时不发送索引字段，重启 HTTP 监听也不会切换正在使用的索引。
        check_preferences(apply(process, receive), settings, True)
        check_preferences(change_service(process, receive, "restartSearchService"), settings, True)
        assert request(port, "search", dict(text=text, sort="size", limit=1))["total"] == 59
        reject(dict(createdBefore=time_filter), "索引创建时间")

    def restart(process, receive):
        check_preferences(service_status(receive), settings, False)
        ready(receive, size=False, modified=False)
        expected = request(port, "search", dict(text=text, sort="name"))
        assert expected["total"] == 59
        assert all(row["sizeBytes"] is None and row["modifiedUtc"] is None and row["createdUtc"]
                   for row in expected["rows"]), expected
        assert request(port, "search", dict(text=text, createdBefore=time_filter))["total"] == 59
        for sort in (2, 3):
            send(process, dict(type="query", requestId=sort, text=text, sort=sort, descending=True))
            results = receive(lambda event: event["type"] == "result" and event["requestId"] == sort)
            assert [row["fullPath"] for row in results["rows"]] == [row["fullPath"] for row in expected["rows"]]
        check_directory(process, receive, 4, known=False)
        send(process, dict(type="spaceAnalysis", requestId=5))
        error = receive(lambda event: event["type"] == "error")
        assert error["scope"] == "action" and "索引文件大小" in error["message"], error

        for query in [dict(sort="size"), dict(minSizeBytes=0), dict(maxSizeBytes=1)]:
            reject(query, "索引文件大小")
        for query in [dict(sort="modified"), dict(modifiedFrom=time_filter), dict(modifiedBefore=time_filter)]:
            reject(query, "索引修改时间")
        cli_error = subprocess.run(cli + ["search", "--sort", "size"], env=env, capture_output=True,
                                   encoding="utf-8", errors="replace", timeout=15)
        assert cli_error.returncode != 0 and not cli_error.stdout and "索引文件大小" in cli_error.stderr, cli_error

        check_preferences(apply(process, receive, indexFileSize=True), settings | dict(indexFileSize=True), True)

    def restore_size(process, receive):
        check_preferences(service_status(receive), settings | dict(indexFileSize=True), False)
        ready(receive, size=True, modified=False)
        check_directory(process, receive, 1, known=True)

    desktop(backend, env, configure)
    desktop(backend, env, restart)
    desktop(backend, env, restore_size)


def check_without_http(backend, cli):
    with port_socket() as listener:
        port = listener.getsockname()[1]
    artifact_root = Path(__file__).resolve().parents[1] / "artifacts"
    artifact_root.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=artifact_root) as sandbox:
        env = os.environ | {"NTFS_SEARCH_PORT": str(port), "NTFS_SEARCH_TEST_DATA": sandbox}
        settings = Path(sandbox) / "settings.json"
        settings.write_text(json.dumps(dict(SearchServiceEnabled=True, SearchServicePort=port)), encoding="utf-8")

        def verify(process, receive):
            status = service_status(receive)
            assert status.get("searchServiceAvailable") is False and not status["running"], status
            receive(lambda event: event["type"] == "result" and event["total"] == 1000)
            assert_unavailable(port)
            send(process, dict(type="applySettings", searchDirectorySizes=False))
            saved = receive(lambda event: event["type"] == "settingsStatus" and not event["busy"])
            assert saved["error"] is None and not saved["searchDirectorySizes"], saved
            data = json.loads(settings.read_bytes())
            assert data["SearchServiceEnabled"] and data["SearchServicePort"] == port

        desktop(backend, env, verify)
        assert (Path(sandbox) / "index.ntsnap").is_file()
        cli_error = subprocess.run(cli + ["status"], env=env, capture_output=True,
                                   encoding="utf-8", errors="replace", timeout=15)
        assert cli_error.returncode != 0 and "带查询服务" in cli_error.stderr, cli_error
    print("无查询服务版桌面搜索、设置、快照、端口和 CLI 验证通过。")


def main(backend, cli):
    with port_socket() as listener:
        port = listener.getsockname()[1]
    env = os.environ | {"NTFS_SEARCH_PORT": str(port)}

    def run_cli(*args, check=True):
        return subprocess.run(cli + list(args), env=env, capture_output=True,
                              encoding="utf-8", errors="replace", timeout=15, check=check)

    artifact_root = Path(__file__).resolve().parents[1] / "artifacts"
    artifact_root.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=artifact_root) as sandbox:
        env["NTFS_SEARCH_TEST_DATA"] = sandbox
        (Path(sandbox) / "settings.json").write_text(json.dumps(dict(IndexCreationTime=True)), encoding="utf-8")
        with ExitStack() as connections:
            desktop(backend, env, lambda process, receive: check_live_api(process, receive, port, run_cli, connections))
        assert (Path(sandbox) / "index.ntsnap").is_file()
        offline = run_cli("search", "demo", check=False)
        assert offline.returncode != 0 and not offline.stdout and str(port) in offline.stderr, offline

        with port_socket() as listener:
            listener.listen()
            env["NTFS_SEARCH_PORT"] = str(listener.getsockname()[1])
            desktop(backend, env, check_port_collision)
    print("本机 HTTP、实时 CLI、共享索引、筛选边界、正常退出和端口占用验证通过。")


if __name__ == "__main__":
    # 支持直接传入 Windows EXE，或在其他系统传入 dotnet 和 DLL。
    args = sys.argv[1:]
    settings_only = args[0] == "--settings"
    without_http = args[0] == "--without-http"
    if settings_only or without_http:
        args = args[1:]
    separator = args.index("--")
    backend, cli = args[:separator], args[separator + 1:]
    if settings_only:
        artifact_root = Path(__file__).resolve().parents[1] / "artifacts"
        artifact_root.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=artifact_root) as sandbox:
            check_index_settings(backend, cli, sandbox)
            check_settings(backend, cli, sandbox)
        print("全局设置、索引开关、目录统计、退出偏好、HTTP 监听和 CLI 端口读取验证通过。")
    elif without_http:
        check_without_http(backend, cli)
    else:
        main(backend, cli)
