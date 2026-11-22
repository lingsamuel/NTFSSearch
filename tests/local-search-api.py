"""Exercise the HTTP and CLI against the same desktop backend index."""

from contextlib import ExitStack
import base64
import json
import os
from pathlib import Path
import queue
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request


http = urllib.request.build_opener(urllib.request.ProxyHandler({}))


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
    separator = sys.argv.index("--")
    main(sys.argv[1:separator], sys.argv[separator + 1:])
