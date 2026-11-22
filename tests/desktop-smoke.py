"""Exercise the desktop backend through its Windows process and JSON pipes."""

import base64
import json
import os
import queue
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path


def check_backend(command, env=None, verify_quit=None):
    events = queue.Queue()
    with subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          encoding="utf-8", env=env) as backend:
        def read_events():
            for line in backend.stdout:
                events.put(json.loads(line))
            events.put(None)

        threading.Thread(target=read_events, daemon=True).start()

        def receive(event_types, predicate=lambda event: True):
            if isinstance(event_types, str):
                event_types = (event_types,)
            deadline = time.monotonic() + 15
            while True:
                event = events.get(timeout=max(0, deadline - time.monotonic()))
                assert event is not None, "后端退出前未发送桌面查询结果"
                assert event["type"] != "error", event
                if event["type"] in event_types and predicate(event):
                    return event

        def send(*requests):
            backend.stdin.write("".join(json.dumps(request) + "\n" for request in requests))
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
            if verify_quit is not None:
                return verify_quit(backend, send, receive)
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
            expected_folder_size = f"{sum(range(100, 200)) * 1024:,} B"
            folder = query("深层1", match_path=False)["rows"][0]
            assert folder["isDirectory"], folder
            if folder.get("sizeHint") == "正在计算目录大小":
                updates = receive("directorySizes", lambda event: event["requestId"] == request_id)["rows"]
                folder.update(next(row for row in updates if row["key"] == folder["key"]))
            assert folder["sizeBytesText"] == expected_folder_size, folder
            sorted_page = query("", sort=2, descending=True)
            if sorted_page.get("directorySortPending"):
                sorted_page = receive("result", lambda event: event["requestId"] == request_id
                                      and not event.get("directorySortPending"))
            largest = sorted_page["rows"][0]
            assert largest["isDirectory"] and largest["sizeBytesText"] == expected_folder_size, largest
            largest_file = query("000999", sort=2, descending=True)["rows"][0]
            assert largest_file["sizeText"] == "999 KB", largest_file
            assert largest_file["sizeBytesText"] == "1,022,976 B"
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


def check_skip_exit_save(command):
    artifacts = Path(__file__).resolve().parents[1] / "artifacts"
    artifacts.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=artifacts) as directory:
        env = os.environ | {"NTFS_SEARCH_TEST_DATA": directory}
        env.pop("NTFS_SEARCH_PORT", None)
        data = Path(directory)
        snapshot = data / "index.ntsnap"

        def quit_backend(skip=False):
            def verify_quit(backend, send, receive):
                quit_request = dict(type="quit")
                if skip:
                    skip_request = dict(type="skipExitSave")
                    send(quit_request, skip_request, skip_request)
                else:
                    send(quit_request)
                phases = []
                while (event := receive(("saving", "quit-complete")))["type"] == "saving":
                    phases.append(event.get("phase"))
                assert event.get("error") is None, event
                assert backend.wait(timeout=10) == 0
                assert not list(data.glob("index.ntsnap.*.tmp"))
                return phases

            return check_backend(command, env, verify_quit)

        first_skip = quit_backend(skip=True)
        assert first_skip in (["skipping"], ["writing", "skipping"]), first_skip
        assert not snapshot.exists()

        normal_save = quit_backend()
        assert normal_save[0] == "writing" and normal_save[-1] == "committing", normal_save
        assert all(phase == "writing" for phase in normal_save[:-1]), normal_save
        previous = snapshot.read_bytes()

        # 改变索引布局后，若这次错误地发布，将覆盖成不同的快照。
        (data / "settings.json").write_text(json.dumps(dict(IndexFileSize=False)), encoding="utf-8")
        later_skip = quit_backend(skip=True)
        assert later_skip in (["skipping"], ["writing", "skipping"]), later_skip
        assert snapshot.read_bytes() == previous
        assert quit_backend()[-1] == "committing"
        assert snapshot.read_bytes() != previous


if __name__ == "__main__":
    command = sys.argv[1:]
    check_backend(command)
    check_skip_exit_save(command)
    ended = subprocess.run(command, input="", encoding="utf-8", stdout=subprocess.PIPE, timeout=15, check=True)
    assert all(json.loads(line)["type"] not in ("saving", "quit-complete") for line in ended.stdout.splitlines())
    print("桌面进程通信、查询语义、精确计数、排序、分页、文件类型图标、退出保存和跳过本次保存通过。")
