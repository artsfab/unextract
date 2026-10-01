"""unextract 手動確認 (docs/MANUAL_TESTS.md の M01〜M07) 用 fixture 生成スクリプト。

使い方 (リポジトリ直下で):
    py scripts/make_manual_fixtures.py "<unextract.exe のパス>"

- %TEMP%\\unextract-manual\\<日時>\\ の下に、シナリオ別の ZIP と target を新規作成する。
- 既存のファイルは削除も上書きもしない (毎回新しい日時フォルダを作る)。
- 実在データは使わない。作るのは小さなテスト用ファイルだけ。exe は実行しない。
- 最後に、各シナリオの項目 ID・期待と実行コマンド (PowerShell 用) を表示する。
- 表示が文字化けする・UnicodeEncodeError になる場合は、PowerShell で $env:PYTHONIOENCODING = 'utf-8' を設定してから実行する。
"""
import os
import struct
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


def stop_scenario():
    """確認待ち中に f10.txt を別ウィンドウで書き換える用 (f01..f09 は先に削除される)。"""
    d = os.path.join(BASE, "stop")
    entries = [(f"f{i:02d}.txt", f"data{i}".encode()) for i in range(1, 11)]
    make_zip(os.path.join(d, "archive.zip"), entries)
    for n, data in entries:
        write(os.path.join(d, "target", n), data)
    return d


def progress_scenario():
    d = os.path.join(BASE, "progress")
    entries = [(f"dir{i % 10}/file{i:04d}.txt", f"content {i}".encode())
               for i in range(500)]
    make_zip(os.path.join(d, "archive.zip"), entries)
    for n, data in entries:
        write(os.path.join(d, "target", n.replace("/", os.sep)), data)
    return d


def japanese_scenario():
    d = os.path.join(BASE, "ja")
    entries = [("資料/報告書.txt", "内容".encode("utf-8")),
               ("資料/写真一覧.csv", b"a,b"),
               ("ファイル名.txt", b"x"),
               ("caf\u00e9\u2591.txt", b"c")]
    make_zip(os.path.join(d, "archive.zip"), entries)
    write(os.path.join(d, "target", "資料", "報告書.txt"), "内容".encode("utf-8"))
    write(os.path.join(d, "target", "caf\u00e9\u2591.txt"), b"c")
    return d


def crc_scenario():
    """bad.txt の CRC だけ壊す。a,b は先に判定済み、c,d は未判定になる想定。"""
    d = os.path.join(BASE, "crc")
    zp = os.path.join(d, "archive.zip")
    make_zip(zp, [("a.txt", b"hello"), ("b.txt", b"hello"),
                  ("bad.txt", b"payload"), ("c.txt", b"hello"), ("d.txt", b"hello")])
    raw = bytearray(open(zp, "rb").read())
    pos = 0
    while True:
        pos = raw.index(b"PK\x01\x02", pos)
        nl = struct.unpack_from("<H", raw, pos + 28)[0]
        if raw[pos + 46:pos + 46 + nl] == b"bad.txt":
            crc = struct.unpack_from("<I", raw, pos + 16)[0]
            struct.pack_into("<I", raw, pos + 16, crc ^ 0xFFFFFFFF)
            break
        pos += 4
    open(zp, "wb").write(raw)
    for n, data in [("a.txt", b"hello"), ("b.txt", b"hello"),
                    ("bad.txt", b"payload"), ("c.txt", b"hello")]:
        write(os.path.join(d, "target", n), data)
    return d


scenarios = {
    "yn-n": basic("yn-n"),
    "yn-enter": basic("yn-enter"),
    "yn-y": basic("yn-y"),
    "yn-ctrlc": basic("yn-ctrlc"),
    "progress": progress_scenario(),
    "ja": japanese_scenario(),
    "stop": stop_scenario(),
    "crc": crc_scenario(),
}

print(f"作成先: {BASE}\n")
tips = {
    "yn-n": "M01: [y/N] に n + Enter → 中止、削除 0 件、終了コード 2",
    "yn-enter": "M02: [y/N] に空 Enter → 中止、削除 0 件、終了コード 2",
    "yn-y": "M03: [y/N] に y + Enter → same1.txt / same2.txt / docs\\deep.txt だけ削除、終了コード 0",
    "yn-ctrlc": "M04: [y/N] で Ctrl+C → 何も削除されない (終了コードは事実として記録)",
    "progress": "M05: --dry-run で Checking n / total、--yes で Deleting n / total が1行で更新される。2> $null では出ない",
    "ja": "M06: --dry-run を chcp 932 と chcp 65001 の両方で実行し、日本語名の表示を見る",
    "stop": "M07: [y/N] で止めて、別ウィンドウで target\\f10.txt に追記 → y。f01〜f09 は削除済み、f10 で停止、終了コード 1",
    "crc": "参考 (E2E の X04 で自動化済み): --dry-run で FATAL (判定済み a.txt, b.txt / 原因 bad.txt / 未判定 2 件)、終了コード 1",
}
for k, d in scenarios.items():
    z = os.path.join(d, "archive.zip")
    t = os.path.join(d, "target")
    opt = " --dry-run" if k in ("progress", "ja", "crc") else ""
    print(f"[{k}] {tips[k]}")
    print(f'  & "{EXE}" "{z}" --target "{t}"{opt}\n')
print("注意: y / --yes の実行は fixture の MATCHED を実際に削除する (復旧不可)。"
      "再実行するときは、このスクリプトをもう一度実行して新しい fixture を作る。")
