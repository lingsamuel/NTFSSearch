"""Exercise the desktop backend through its Windows process and JSON pipes."""

import base64
import json
import queue
import subprocess
import sys
import threading
import time


def check_backend(command):
    events = queue.Queue()
    with subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          encoding="utf-8") as backend:
        def read_events():
            for line in backend.stdout:
                events.put(json.loads(line))
            events.put(None)

        threading.Thread(target=read_events, daemon=True).start()

        def receive(event_type, predicate=lambda event: True):
            deadline = time.monotonic() + 15
            while True:
                event = events.get(timeout=max(0, deadline - time.monotonic()))
                assert event is not None, "后端退出前未发送桌面查询结果"
                assert event["type"] != "error", event
                if event["type"] == event_type and predicate(event):
                    return event

        def send(request):
            backend.stdin.write(json.dumps(request) + "\n")
            backend.stdin.flush()

        request_id = 0

        def query(text, sort=0, descending=False, match_path=True, offset=0):
            nonlocal request_id
            request_id += 1
            request = dict(type="query", requestId=request_id, text=text, sort=sort,
                           descending=descending, matchPath=match_path, offset=offset)
            send(request)
            return receive("result", lambda event: event["requestId"] == request_id)

        try:
            first = receive("result", lambda event: event["total"] == 1000)
            assert len(first["rows"]) == 200
            unknown = ".ntfs-search-fixture-unknown-type"
            send(dict(type="fileTypeIcons", extensions=[".TXT", ".txt", ".png", unknown, ".\\bad"]))
            icons = receive("fileTypeIcons")["icons"]
            assert set(icons) == {".txt", ".png", unknown}, icons.keys()
            assert icons[unknown] is None
            for extension in (".txt", ".png"):
                prefix = "data:image/png;base64,"
                assert icons[extension].startswith(prefix), extension
                png = base64.b64decode(icons[extension][len(prefix):], validate=True)
                assert png.startswith(b"\x89PNG\r\n\x1a\n") and png[12:16] == b"IHDR", extension
            assert icons[".txt"] != icons[".png"]
            send(dict(type="fileTypeIcons", extensions=[".Txt"]))
            assert receive("fileTypeIcons")["icons"] == {".txt": icons[".txt"]}
            for sort in range(4):
                ordered = query('"hello world"', sort)
                reversed_order = query('"hello world"', sort, descending=True)
                assert ordered["total"] == reversed_order["total"] == 59
                assert ordered["rows"] == reversed_order["rows"][::-1]
            exact = query("目录009")
            assert exact["total"] == 100 and query("目录009", match_path=False)["total"] == 0
            row = exact["rows"][0]
            assert base64.b64decode(row["key"]).decode("utf-16-le", "surrogatepass") == row["fullPath"]
            largest = query("", sort=2, descending=True)["rows"][0]
            assert largest["sizeText"] == "999 KB", largest["sizeText"]
            assert largest["sizeBytesText"] == "1,022,976 B"
            assert query("000000")["rows"][0]["sizeText"] == "0 B"
            tail = query("", offset=1200)
            assert tail["total"] == 1000 and tail["offset"] == 800 and len(tail["rows"]) == 200
            send(dict(type="quit"))
            assert receive("saving")["written"] == 0
            assert receive("quit-complete").get("error") is None
        finally:
            backend.stdin.close()
            try:
                backend.wait(timeout=10)
            except subprocess.TimeoutExpired:
                backend.kill()
                raise
        assert backend.returncode == 0, backend.returncode


if __name__ == "__main__":
    command = sys.argv[1:]
    check_backend(command)
    ended = subprocess.run(command, input="", encoding="utf-8", stdout=subprocess.PIPE, timeout=15, check=True)
    assert all(json.loads(line)["type"] not in ("saving", "quit-complete") for line in ended.stdout.splitlines())
    print("桌面进程通信、查询语义、精确计数、排序、分页、文件类型图标和退出通过。")
