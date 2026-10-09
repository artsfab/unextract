"""unextract 手動確認 (docs/MANUAL_TESTS.md の M05・M08・M14) 用 fixture 生成スクリプト。

使い方 (リポジトリ直下で):
    py scripts/make_manual_fixtures.py "<unextract.exe のパス>"

- %TEMP%\\unextract-manual\\<日時>\\ の下に、シナリオ別の ZIP と target を新規作成する。
- 既存のファイルは削除も上書きもしない (毎回新しい日時フォルダを作る)。
- 実在データは使わない。作るのは小さなテスト用ファイルだけ。exe は実行しない。
- 最後に、各シナリオの項目 ID・期待と実行コマンド (PowerShell 用) を表示する。
- M14 (GUI) は yn-y と yn-n を GUI から使う。手順は手順書 (MANUAL_TESTS.md) のものを使う。
- 表示が文字化けする・UnicodeEncodeError になる場合は、PowerShell で $env:PYTHONIOENCODING = 'utf-8' を設定してから実行する。
"""
import os
import sys
import tempfile
import zipfile
from datetime import datetime

EXE = sys.argv[1] if len(sys.argv) > 1 else r"<unextract.exe のパス>"
BASE = os.path.join(tempfile.gettempdir(), "unextract-manual",
                    datetime.now().strftime("%Y%m%d-%H%M%S"))
os.makedirs(BASE)  # 既存なら例外で止まる (上書きしない)


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)


def make_zip(path, entries):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in entries:
            if name.endswith("/"):
                z.writestr(zipfile.ZipInfo(name), b"")
            else:
                z.writestr(name, data)


def basic(name):
    """MATCHED 3 / MODIFIED 1 / MISSING 1 / ZIP にないファイル 1。"""
    d = os.path.join(BASE, name)
    make_zip(os.path.join(d, "archive.zip"), [
        ("same1.txt", b"hello1"), ("same2.txt", b"hello2"),
        ("docs/", b""), ("docs/deep.txt", b"deep"),
        ("changed.txt", b"hello"), ("missing.txt", b"x"),
    ])
    t = os.path.join(d, "target")
    write(os.path.join(t, "same1.txt"), b"hello1")
    write(os.path.join(t, "same2.txt"), b"hello2")
    write(os.path.join(t, "docs", "deep.txt"), b"deep")
    write(os.path.join(t, "changed.txt"), b"hellO")
    write(os.path.join(t, "unrelated.txt"), b"not in zip")
    return d


def progress_scenario():
    d = os.path.join(BASE, "progress")
    entries = [(f"dir{i % 10}/file{i:04d}.txt", f"content {i}".encode())
               for i in range(500)]
    make_zip(os.path.join(d, "archive.zip"), entries)
    for n, data in entries:
        write(os.path.join(d, "target", n.replace("/", os.sep)), data)
    return d


scenarios = {
    "yn-n": basic("yn-n"),
    "yn-y": basic("yn-y"),
    "progress": progress_scenario(),
}

print(f"作成先: {BASE}\n")
tips = {
    "yn-n": "M08: --fast の analyze と delete で警告を見て、[y/N] に n + Enter で中止 (削除 0 件、終了コード 2)。M14 の Fast にも使う",
    "yn-y": "M14: GUI で Strict の削除 (same1.txt / same2.txt / docs\\deep.txt だけが消える)",
    "progress": "M05: analyze で Checking n / total、delete --yes で Processing n / total。2> $null では出ない",
}
commands = {
    "yn-n": ["analyze --fast", "delete --fast"],
    "yn-y": [],
    "progress": ["analyze", "delete --yes"],
}
for k, d in scenarios.items():
    z = os.path.join(d, "archive.zip")
    t = os.path.join(d, "target")
    print(f"[{k}] {tips[k]}")
    for c in commands[k]:
        sub, _, opt = c.partition(" ")
        opt = f" {opt}" if opt else ""
        print(f'  & "{EXE}" {sub} "{z}" --target "{t}"{opt}')
    if not commands[k]:
        print(f"  target: {t}")
    print()
print("注意: delete で y / --yes を実行すると fixture のファイルを実際に削除する (復旧不可)。"
      "再実行するときは、このスクリプトをもう一度実行して新しい fixture を作る。")
