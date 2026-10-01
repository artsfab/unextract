# unextract 実装PLAN(SPEC v3 対応)

- 作成日: 2026-10-01
- 対象: `docs/SPEC.md` v3
- 実装言語: C# / .NET 10(現行LTS。調査環境の SDK は 10.0.401、ランタイムは 10.0.12)
- 本書はPLANのみ。リポジトリのコード・プロジェクト・CI設定は作成・変更していない

凡例

- **確認済み(実機)**: `%TEMP%\unextract-spike` 内のダミーファイルで実測した
- **確認済み(一次資料)**: Microsoft Learn / .NET runtime 公式ソースで確認した(実機では未確認)
- **未確認(要手動確認)**: 許可範囲外、または環境上できなかったもの。§3 に手順を記載した
- **二次資料(確度: 高/中/低)**: 一次資料で確認できず、補助的に参照したもの

---

## 1. スパイク環境の確認結果

| 項目 | 結果 |
|---|---|
| `%TEMP%` | `C:\Users\<user>\AppData\Local\Temp` |
| 最終パス(`GetFinalPathNameByHandleW`, `FILE_FLAG_BACKUP_SEMANTICS`) | `\\?\C:\Users\<user>\AppData\Local\Temp` |
| 経路上の reparse point | なし。`C:\`、`C:\Users`、`C:\Users\<user>`、`…\AppData`、`…\Local`、`…\Temp` の各成分の属性を確認し、どれにも `ReparsePoint` がなかった |
| 判定 | **Cドライブ上で、別ドライブへの reparse もない → 実機スパイク可** |
| 空き容量(開始時) | C: 約 487 GiB |

スパイクで守った隔離措置と、逸脱の報告(正直に記載する):

- 作業はすべて `%TEMP%\unextract-spike` の中で行った。.NET CLI の状態は `DOTNET_CLI_HOME`、NuGet は `NUGET_PACKAGES` / `NUGET_HTTP_CACHE_PATH` / `NUGET_PLUGINS_CACHE_PATH` と `RestoreConfigFile`、一時ファイルは `TEMP`/`TMP` を、いずれもスパイクディレクトリの中に向けた。ビルドサーバーは無効化した
- 次のアクセスは不可避、または調査上必要だったため実施した。いずれも読み取りのみ
  - .NET SDK の実行による `C:\Program Files\dotnet` とシステムDLLの暗黙の読み込み
  - `HKLM\…\FileSystem\LongPathsEnabled` の値の読み取り(B-6 の解釈に必要)
  - ごみ箱では自分が送ったダミーだけを操作した。その `$I` メタデータを読み、元パスがスパイクディレクトリ内であることを検証してから後始末した
- **逸脱1(ディスク使用量)**: ユーザーから「最小サイズで行い、GiB級の書き込みは先に理由を示すこと」と指示を受ける前に、理由を示さずに約 8.3 GiB を書き込んだ。内訳は、1 GiB の乱数ファイル、その ZIP 2種(Deflate/Stored)、Zip64 確認用の 5 GiB ゼロファイル(ZIP 本体は約 52 MB)、100万エントリ ZIP(109 MB)。上限の 12 GiB 内ではあるが、Zip64 の確認は 4 GiB 強で足り、スループットの傾向もより小さいサイズで出せた。指示を受けた時点で該当ファイルはすべて削除した。以後、GiB級の書き込みはしていない
- **逸脱2(取得物)**: 机上調査のため、.NET runtime(release/10.0)の `System.IO.Compression` と `Microsoft.VisualBasic.Core` の**ソースファイル(テキスト)**を `curl` でスパイクディレクトリに取得した。実行はしていない。NuGet 以外の取得にあたるため報告する
- `fsutil file setCaseSensitiveInfo` は、スパイクディレクトリ内に自分で作ったサブディレクトリに対してだけ実行した(B-7)

---

## 2. 調査結果

### A. ごみ箱 / IFileOperation

#### A-1 CsWin32 で `IFileOperation` を呼べるか — **確認済み(実機)**。ただし NativeAOT のリンクは**未確認**

- CsWin32 0.3.335 で `IFileOperation`、`FileOperation`(CLSID)、`IFileOperationProgressSink`、`SHCreateItemFromParsingName` を生成し、`CoCreateInstance` → `SetOperationFlags` → `Advise` → `DeleteItem` → `PerformOperations` が動作した
- `IFileOperationProgressSink` は C# のクラスで実装できる。HRESULT を明示的に返すには、`NativeMethods.json` の `comInterop.preserveSigMethods` に `IFileOperationProgressSink` などを指定する
- スレッド: `new Thread(...)` に `SetApartmentState(ApartmentState.STA)` を設定して実行し、成功した。COM 初期化は、.NET が STA スレッドの開始時に行う
  - 一次資料は「IFileOperation can only be applied in a single-threaded apartment (STA)… It cannot be used for a multithreaded apartment (MTA)」と明記している([IFileOperation](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileoperation))
  - 実機では MTA でも動いたが、**文書化された契約は STA のみなので、実装は専用の STA スレッドで行う**
- single-file publish(`-r win-x64 --self-contained -p:PublishSingleFile=true`): **確認済み(実機)**。ごみ箱送りまで成功した
- NativeAOT:
  - 既定設定(組み込み COM interop、`[ComImport]`)では、AOT publish 時に `IL2050`(COM marshalling は trimming 非対応)の警告が出た → **AOT 非互換**
  - `comInterop.useComSourceGenerators: true` と MSBuild プロパティ `CsWin32RunAsBuildTask=true` を使い、Sink に `[GeneratedComClass]` を付けた版を作った。`IsAotCompatible` / `EnableAotAnalyzer` 有効で**警告0**でビルドでき、JIT 実行でごみ箱送りとハンドル保持の検証まで成功した。生成物が `[GeneratedComInterface]` になっていることも確認した
  - AOT 実行ファイルのリンクは「Platform linker not found」で失敗した(VC++ ビルドツールが未導入で、導入は禁止)→ **AOT バイナリでの実行は未確認**
  - 出典: [CsWin32 settings.schema.json](https://raw.githubusercontent.com/microsoft/CsWin32/main/src/Microsoft.Windows.CsWin32/settings.schema.json)(`useComSourceGenerators` の説明)

#### A-2 完全削除へのフォールバックを抑止・検出できるか — 通常フォルダは**確認済み(実機)**。フォールバックが起きる環境は**未確認(要手動確認)**

`%TEMP%\unextract-spike\work` 内のダミーで確認した結果:

| # | フラグ | PreDeleteItem の dwFlags | PostDeleteItem | 結果 |
|---|---|---|---|---|
| 1 | `FOF_ALLOWUNDO\|FOF_NO_UI`(0x654) | 0x282(**`TSF_DELETE_RECYCLE_IF_POSSIBLE`=0x80 あり**) | hrDelete=0x00270008(成功コード)、`psiNewlyCreated`=`C:\$Recycle.Bin\<SID>\$Rxxxx.txt` | ごみ箱へ移動 |
| 2 | 上記 + `FOFX_RECYCLEONDELETE`(0x80654) | 0x282 | 同上 | ごみ箱へ移動(差は観測されず) |
| 3 | 上記 + `FOF_WANTNUKEWARNING`(0x4654) | 0x282 | 同上 | ごみ箱へ移動(フォールバックが起きない環境のため、警告動作は観測できない) |
| 4 | `FOF_NO_UI` のみ(0x614)+ Sink が「0x80 なしなら `E_ABORT`」 | 0x202(**0x80 なし**) | hrDelete=`E_ABORT`、`psiNewlyCreated`=NULL | **削除されずに残った**。PerformOperations も `E_ABORT` を返した |
| 5 | `FOF_NO_UI` のみ(0x614)、拒否なし | 0x202 | 成功コード、`psiNewlyCreated`=**NULL** | 完全削除された |
| 6 | 0x654 + 常に `E_ABORT` | 0x282 | `E_ABORT`、NULL | 削除されずに残った |

結論:

- **抑止(事前)**: `PreDeleteItem` で dwFlags に `TSF_DELETE_RECYCLE_IF_POSSIBLE` がなければ `E_ABORT` を返す。これで削除を止められることを実機で確認した(#4)。一次資料も「In the case of an error value, the delete operation and all subsequent operations … are canceled」としている([PreDeleteItem](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-predeleteitem)、[TRANSFER_SOURCE_FLAGS](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/ne-shobjidl_core-_transfer_source_flags))
  - **ただし**、確認できたのは「ALLOWUNDO を付けなかった場合に 0x80 が消える」ことだけである。ALLOWUNDO を付けたのに、ボリューム側の事情(USB、ネットワーク、容量超過)でフォールバックする場合にも 0x80 が消えるかは、**未確認(要手動確認)**。フラグ名が「recycle *if possible*」なので、フォールバック時も 0x80 が立ったまま完全削除される可能性を否定できない
- **検出(事後)**: `PostDeleteItem` の `psiNewlyCreated` は、一次資料で「If the item was fully deleted, this value is NULL」とされている([PostDeleteItem](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-postdeleteitem))。#5 で NULL を実機確認した。**事後検出は確実にできるが、その時点でファイルは既に消えている**
- `FOF_WANTNUKEWARNING` は「Send a warning if a file or folder is being destroyed … This flag partially overrides FOF_NOCONFIRMATION」([SetOperationFlags](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags))。これは UI ダイアログでの警告で、CLI では使いにくい(GUI のダイアログが出て止まる可能性がある)。フォールバック時の実挙動は**未確認(要手動確認)**
- `FOFX_RECYCLEONDELETE` は「send it to the Recycle Bin rather than permanently deleting it」(Windows 8+)。フォールバック時に完全削除を防ぐかは**未確認(要手動確認)**
- 机上の補助情報: ネットワーク共有と多くのリムーバブルメディアではローカルのごみ箱が使われない(Microsoft Q&A コミュニティ回答。**二次資料・確度 中**)。安全性の結論の根拠にはしない

→ 設計(§4)では、**事前の除外(ドライブ種別)**、**PreDeleteItem での拒否**、**PostDeleteItem での検出と以降の中止**の三重にする。フォールバックが起きる環境での確認は §3 の M-1〜M-4 に委ねる。

#### A-3 `IShellItem` 経由で渡せるか / パス文字列を回避できるか / ファイルIDとの組み合わせ — **確認済み(実機)**

- `IShellItem` は `SHCreateItemFromParsingName(path)` で作るため、**パス文字列を完全には回避できない**。Shell の名前空間はパス(PIDL)ベースで、ハンドルやファイルIDから IShellItem を作る手段は見つからなかった
- その代わり、次の2点で TOCTOU を**大幅に狭め、かつ事後に検出できる**ことを実機で確認した
  1. **ハンドル保持**: 比較に使ったハンドルを `FILE_SHARE_READ|FILE_SHARE_DELETE`(書き込み共有なし)で保持したままごみ箱に送ると、送れた。保持中は、他者による書き込みオープンが共有違反で失敗する。リネームは可能
     - 共有に `FILE_SHARE_DELETE` を含めない場合は、Shell の移動も共有違反(hrDelete=0x80270027)で失敗する
  2. **事後同一性確認**: 送った後に、保持ハンドルで `GetFinalPathNameByHandle` を呼ぶと `\\?\C:\$Recycle.Bin\<SID>\$Rxxxx.txt`、つまり `psiNewlyCreated` と同じパスを返した → 「検証したそのファイル」がごみ箱に入ったことを確認できる
  3. **差し替え攻撃のシミュレーション**: 検証(ハンドル保持)の後、PerformOperations の前に、元ファイルを `.orig` にリネームし、同じ名前で別ファイルを作った
     - ID 照合なし: 別ファイルがごみ箱に入った。保持ハンドルの最終パスは `…\sw1.txt.orig` を指したまま → **事後に検出できた**(入ったのはごみ箱なので復元できる)
     - `PreDeleteItem` 内で `psiItem` のパスを開き直し、`FILE_ID_INFO` を保持ハンドルの ID と比べた: ID の不一致を検出して `E_ABORT` → **削除を阻止できた**
- 残る窓: 「PreDeleteItem の ID 照合」から「Shell が実際に移動する」までの間の差し替え。ここはハンドル保持による事後検出で拾う(§6 R-1)

#### A-4 ファイル単位の削除結果 — **確認済み(実機)**

- `IFileOperationProgressSink::PostDeleteItem(dwFlags, psiItem, hrDelete, psiNewlyCreated)` で、ファイルごとの結果を取得できる
  - `hrDelete` が成功(0x00270008 のような成功コードも含むため、`SUCCEEDED()` で判定する)かつ `psiNewlyCreated` が非 NULL → ごみ箱へ移動した
  - 成功かつ NULL → 完全削除された(異常)
  - 失敗 HRESULT → 削除されていない(例: 共有違反 0x80270027)
- `PerformOperations` の戻り値は、ロック中で個別に失敗した場合でも 0 だった(`GetAnyOperationsAborted` は True)。**判定は必ず Sink の個別結果で行う**

#### A-5 `--delete-permanently` の実装方式 — **確認済み(実機)**

- `File.Delete` は、読み取り専用属性のファイルで `UnauthorizedAccessException`(Access denied)になった(実機)。そのうえパスベースである
- **推奨**: 検証に使ったハンドルを `DELETE|GENERIC_READ` 権限、共有 `FILE_SHARE_READ` のみ(書き込み・削除・リネームを他者に許さない)、`FILE_FLAG_OPEN_REPARSE_POINT` で開き、同じハンドルで再検証してから、`SetFileInformationByHandle(FileDispositionInfoEx, FILE_DISPOSITION_FLAG_DELETE|POSIX_SEMANTICS|IGNORE_READONLY_ATTRIBUTE)` を呼ぶ
  - 読み取り専用ファイルも消せた(実機)。**検証したハンドルそのものを消すので、ファイル単位の TOCTOU がない**
  - 出典: [FILE_DISPOSITION_INFORMATION_EX](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex)、[SetFileInformationByHandle](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle)(DELETE アクセスが必要)
- 読み取り専用属性のファイルを消すかどうかは方針判断(§5 Q-9)

#### A-6 `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile` では足りない理由 — **確認済み(一次資料: 公式ソース)**

- .NET runtime release/10.0 `src/libraries/Microsoft.VisualBasic.Core/src/Microsoft/VisualBasic/FileIO/FileSystem.vb`
  - `ShellDelete`(L1664–1677)は `FOF_ALLOWUNDO` を付けて `SHFileOperation` を呼ぶだけ
  - `ShellFileOperation`(L1693–1723)は `SHFILEOPSTRUCT.fAnyOperationsAborted` と戻り値しか見ない
- 進捗シンクがなく、**ファイル単位の「ごみ箱に入ったか / 完全削除されたか」を取得できない**。事前拒否もできない
- パスを渡すだけなので、ハンドル同一性の確認もできない → SPEC §10.1 の要件(フォールバック時は ERROR)を満たせない

### B. Windowsパス / reparse / hardlink

#### B-1 実パス解決と reparse 検査の併用 — **確認済み(実機)**

- `CreateFileW(path, FILE_READ_ATTRIBUTES|SYNCHRONIZE, share RWD, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_BACKUP_SEMANTICS)` で開き、同じハンドルで次をまとめて取得する: 属性と reparse タグ(`FileAttributeTagInfo`)、リンク数(`GetFileInformationByHandle`)、`FILE_ID_INFO`、`GetFileType`、最終パス
- `FILE_FLAG_OPEN_REPARSE_POINT` が効くのは**最後の成分だけ**。junction 経由のパス(`work\jct\sub\f.txt`)では、ファイル自体は通常ファイルに見え、最終パスは `…\real\sub\f.txt` になった(実機)。**中間成分の検査は成分ごとに別途必要**
- `GetFinalPathNameByHandle` の戻り値は `\\?\` 付き([GetFinalPathNameByHandleW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew)。実機でも確認)

#### B-2 target・中間ディレクトリ・対象ファイルの ReparsePoint 検査の方針 — **確認済み(実機)**

- junction: `attr=0x410 (Directory, ReparsePoint)`、`tag=0xA0000003`(MOUNT_POINT)。.NET の `FileInfo.Attributes` にも `ReparsePoint` が出た
- ハードリンク: `links=2`(`CreateHardLinkW` で作成)。属性は通常ファイルと区別できない → **リンク数の検査が必須**
- symlink: この環境では作れなかった(`File.CreateSymbolicLink` →「クライアントは要求された特権を保有していません」。開発者モードも管理者権限もないため)→ **symlink の実測は未確認**。タグは `IO_REPARSE_TAG_SYMLINK` で、属性に ReparsePoint が立つ(一次資料ベースの判断)
- **ディレクトリの保持による固定**(実機):
  - `GENERIC_READ`(`FILE_LIST_DIRECTORY` を含む)で、共有 `READ|WRITE`(DELETE なし)で開いて保持すると、そのディレクトリのリネームは共有違反で失敗し、**親ディレクトリのリネームもアクセス拒否で失敗**した。子ファイルの作成・削除はできた
  - **`FILE_READ_ATTRIBUTES` だけで開いた場合は共有モードが効かず、リネームが成功した** → 保持用ハンドルには必ずデータアクセス権を付ける
- 方針: target から各成分を順に開いて検査し、保持する。開いた後の再検査はハンドルで行い、文字列パスは使わない(§4)

#### B-3 クラウドプレースホルダ — **未確認(要手動確認)**(OneDrive などの同期ルート登録はOS状態の変更になるため、実機では再現しなかった)

- 一次資料どうしで**記述が食い違う**
  - [Build a Cloud Sync Engine](https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine): 「the cloud files API always hides its reparse points from all applications except for sync engines and processes whose main image resides under %systemroot%」
  - [RtlSetProcessPlaceholderCompatibilityMode](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-rtlsetprocessplaceholdercompatibilitymode): 「Most Windows applications see exposed placeholders by default. For compatibility reasons, Windows may decide that certain applications see disguised placeholders by default.」「When placeholders are disguised, these details are completely hidden, making the file look like a normal file.」
- 含意:
  - 既定のままでは、プレースホルダが**通常ファイルに見え、reparse 検査で検出できない可能性がある**。さらに読み取り比較をすると**ハイドレーション(ダウンロード)が起きうる**
  - 検出するには、プロセス起動時に `RtlSetProcessPlaceholderCompatibilityMode(PHCM_EXPOSE_PLACEHOLDERS)`(ntdll、Windows 10 1803+)を呼び、**明示的に公開モードにする**必要がある
- 公開モードで、ハイドレーション済み(「常にこのデバイスに保持」を含む)の OneDrive ファイルが reparse point のままかは**未確認**。そうであれば、OneDrive のバックアップ対象フォルダ(デスクトップ、ドキュメント、ピクチャ。設定によってはダウンロードも)にある target では、**全件 SKIPPED になる**
- 補足: [Cldflt は NTFS のみ対応](https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine)

#### B-4 ハードリンク数 — **確認済み(実機)**

- `GetFileInformationByHandle().nNumberOfLinks` で 2 を確認した。FAT は常に 1([BY_HANDLE_FILE_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/ns-fileapi-by_handle_file_information))

#### B-5 ファイルID(`FILE_ID_INFO`)と同一性確認 — **確認済み(実機)**

- `GetFileInformationByHandleEx(FileIdInfo)` で `VolumeSerialNumber`(64bit)と 128bit の `FileId` が取得できた
- 「ID とボリュームシリアル番号の組でファイルを一意に識別する」([FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info))。ReFS では 64bit の `nFileIndex` は一意性が保証されないため、**128bit 版を使う**
- 実機の結果
  - 同じパスへのインプレース書き換えでは **ID は変わらない** → 内容の再比較が必須
  - リネームによる置換(`File.Move(b, a, overwrite)`)では **ID が変わる** → 置換を検出できる
- NTFS では ID は削除まで不変だが、`ReplaceFile` では置換側の ID が残る(同資料)。いずれにせよ、ID が変われば ERROR とする安全側の設計で問題ない
- 比較時と削除直前の同一性確認は実現可能(A-3 のシミュレーションで実証した)

#### B-6 260文字超のパス(`\\?\` なし) — **確認済み(実機)**(`LongPathsEnabled=1` の環境)

| API | 325文字の通常パス | `\\?\` 付き |
|---|---|---|
| .NET `File.ReadAllText` | 成功(.NET が内部で処理する) | — |
| CsWin32 経由の `CreateFileW` | **失敗**(エラー3)。アプリ manifest に `longPathAware` がないため | 成功 |
| `SHCreateItemFromParsingName` | 成功。ただし FS パスは **8.3 短縮名**(`UNEXTR~1\…\FILE-I~1.TXT`)で返る | **失敗**(0x80070057 E_INVALIDARG) |
| IFileOperation によるごみ箱送り | 成功(`$I` の元パスは `\\?\` 付きで記録された) | — |

- 一次資料: opt-in には「レジストリ値と manifest の `longPathAware`」の両方が必要([Maximum Path Length Limitation](https://learn.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation))
- 方針: Win32 を直接呼ぶときは常に `\\?\` 形式を使う。Shell には通常パスを渡す。Shell が返すパスは 8.3 名のことがあるため、**同一性はパス文字列ではなくファイルIDで比較する**
- 8.3 名の生成が無効なボリュームで、長いパスのごみ箱送りがどうなるかは**未確認(要手動確認、M-6)**

#### B-7 大文字小文字 — **確認済み(実機 + 机上)**

- `fsutil file setCaseSensitiveInfo <dir> enable` をスパイクのサブディレクトリにだけ適用した。`A.txt` と `a.txt` が共存し、`A.TXT` では見つからなかった
- 結論:
  - **ZIP エントリ同士の重複判定**(SPEC §8)は `OrdinalIgnoreCase` で問題ない。区別のあるディレクトリでは、本来は別ファイルのものまで AMBIGUOUS になるが、安全側である
  - **target 配下かどうかの判定**(SPEC §7.2)を、大文字小文字を区別しない文字列比較にすると、区別のある親の下で `Foo` と `foo` を同一視しうる。設計では、文字列比較ではなく**ハンドル連鎖による構造的な包含**を使う(§4)
  - **拒否リスト**(§4 のシステムディレクトリなど)の比較は `OrdinalIgnoreCase` にする(取りこぼすより多めに拒否する方向で、安全側)。実際、`SHGetKnownFolderPath(FOLDERID_Windows)` は `C:\WINDOWS` と大文字で返した(実機)
  - NTFS の大文字化テーブルと .NET の `OrdinalIgnoreCase` は、まれな文字で一致しない可能性がある。ただし判定の根拠はバイト一致なので、誤削除にはつながらない

#### B-8 システムディレクトリ等の取得方法 — **確認済み(実機。パス文字列の取得のみ)**

| 対象 | `SHGetKnownFolderPath` | `Environment.GetFolderPath` | 環境変数 |
|---|---|---|---|
| ユーザープロファイル | `FOLDERID_Profile` → `C:\Users\<user>` | `UserProfile` | `%USERPROFILE%` |
| Windows | `FOLDERID_Windows` → `C:\WINDOWS` | `Windows` | `%SystemRoot%` / `%windir%` |
| Program Files | `FOLDERID_ProgramFiles` → `C:\Program Files` | `ProgramFiles` | `%ProgramFiles%` / `%ProgramW6432%` |
| Program Files (x86) | `FOLDERID_ProgramFilesX86` | `ProgramFilesX86` | `%ProgramFiles(x86)%` |
| ProgramData | `FOLDERID_ProgramData` | `CommonApplicationData` | `%ProgramData%` |

方針:

- 既知フォルダAPI(または同等の `Environment.GetFolderPath`)を正とする。環境変数はユーザーが書き換えられるため、**和集合で拒否する**(多めに拒否する方向)
- 32bit/64bit の両方の Program Files を含める
- 拒否判定は、target の最終パス(`GetFinalPathNameByHandle`、`\\?\` を除去)と各候補との比較で行う。候補が存在すれば候補も同じ方法で最終パスに解決する(junction 経由で指定された場合への対策。中身は読まない)
- 比較は成分単位の `OrdinalIgnoreCase`
- ドライブルート: 最終パスがボリュームルート(`X:\`、`\\?\Volume{…}\`)なら拒否
- UNC ルート: `\\server\share` そのものなら拒否
- 「配下も拒否するか」は方針判断(§5 Q-1)

### C. ZIP(System.IO.Compression, .NET 10.0.12)

検証用の ZIP は、スパイク内の自作の最小 ZIP ライタで生成した。ローカルヘッダー、セントラルディレクトリ、Zip64 EOCD を任意の値で書けるもので、§8 のテストヘルパー `RawZipBuilder` の原型である。

#### C-1 エントリ名のエンコーディング — **確認済み(実機 + 公式ソース)**。**SPEC §9 と既定動作は一致しない**

| 読み方 | フラグなし `0x81 .txt`(CP437 では ü) | フラグなし SJIS「あ」 | フラグ付き UTF-8「日本」 | フラグなしの UTF-8 バイト |
|---|---|---|---|---|
| `entryNameEncoding=null`(既定) | `U+FFFD .txt` | `U+FFFD U+FFFD .txt` | 日本.txt | 日本_noflag.txt(UTF-8 として解釈) |
| CP437 を明示 | ü.txt | éá.txt | 日本.txt(フラグ優先) | 文字化け |

- 既定(null)は、フラグなしのエントリを **UTF-8 で解釈する**。.NET ソース `ZipArchiveEntry.DecodeEntryString`(release/10.0 L374–383)の `_archive?.EntryNameAndCommentEncoding ?? Encoding.UTF8` による。Learn のドキュメントは「current system default code page」と書いているが、.NET Core の既定コードページは UTF-8 である
- `Encoding.GetEncoding(437)` は、そのままでは `NotSupportedException` になる。**`Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` が必要**(.NET 10 の共有フレームワークに含まれるので、追加パッケージは不要)
- CP437 は 256 バイト値すべてを可逆に往復できた(実機)→ 生バイトの区別が失われず、AMBIGUOUS の判定にも使える
- UTF-16 を指定すると `ArgumentException`(UTF-16 は名前のエンコーディングとして不可)
- UTF-8 の不正バイト列は U+FFFD になり、別名同士が同じ文字列に衝突しうる(既定の解釈を使った場合)→ 自前の読み取りでは**生バイトを保持して判定する**
- 出典: [ZipArchive ctor (Learn)](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchive.-ctor?view=net-10.0)、dotnet/runtime release/10.0 `ZipArchiveEntry.cs`

#### C-2 暗号化エントリの検出 — **確認済み(実機)**

- `ZipArchiveEntry.IsEncrypted`(汎用ビットフラグの bit0)で検出できた。bit6(strong encryption)を併用した場合も True
- **注意: `Open()` は暗号化フラグ付きのエントリを拒否しない**。中身が平文なら、そのまま読めてしまった(実機)。ソースの `IsOpenable` は圧縮方式しか見ていない → **アプリ側で `IsEncrypted` を必ず確認して ERROR にする**
- WinZip AES(圧縮方式 99)は `InvalidDataException`(未対応の圧縮方式)
- 汎用ビットフラグの生の値は公開 API にない(`IsEncrypted` のみ)

#### C-3 CRC-32・サイズの取得と、壊れた ZIP の挙動 — **確認済み(実機)**

- `Crc32`、`Length`、`CompressedLength` は取得できる(ヘッダーの宣言値)
- **.NET 10 は読み出し時に CRC を検証しない**
  - CRC 改ざん → 例外なしで全バイトを返す
  - Deflate データの中間を破損 → **例外なしで 15760/16000 バイトの短い出力**
  - ソースでも、読み出し経路(`OpenInReadMode` → `GetDataDecompressor`)に CRC 検証はない
- Stored で宣言 Uncompressed < Compressed → **宣言サイズを超える 16000 バイトを返した**(`SubReadStream` は CompressedSize で区切るため)
- 0 バイトのエントリで CRC を改ざん → 0 バイトを正常に返す。CRC の自前計算で不一致を検出できる
- → **CRC と展開バイト数は、必ず自前で計算・照合する**(SPEC §6-4 の実装で必須)

#### C-4 重複エントリ — **確認済み(実機)**

- 同一パス(`a.txt`×2)、大文字小文字違い(`A.TXT`)、ファイル `d` とディレクトリ `d/`、`d/x.txt`、`w\y.txt` のすべてが `Entries` に列挙された
- `GetEntry` は先頭の1件を返し、大文字小文字を区別する → **`GetEntry` は使わず、全件を自前でグループ化する**
- `\` は .NET 側では区切りとして扱われない(`FullName='w\y.txt'`)→ 自前で区切りとして正規化する(SPEC §7.1)

#### C-5 `ExternalAttributes` による symlink 判定 — **確認済み(実機)**

- 作成元 Unix・`0xA1FF0000` → `(attr>>16)&0xF000 == 0xA000` で判定できた。通常ファイル `0x81A4` とは区別できる
- **作成元の OS(version made by の上位バイト)は公開 API にない**。Windows で作られた ZIP は通常、上位16ビットが 0 なので誤判定しない。安全側に倒すため、OS を問わず `0xA000` を symlink とみなす
- Windows 側の `FILE_ATTRIBUTE_REPARSE_POINT`(0x400)が下位バイトに入っている ZIP も、SKIPPED 扱いを推奨する(判断事項 Q-10)

#### C-6 ストリーミングとメモリ — **確認済み(実機)**

- 1 GiB のエントリを比較しても、ピーク WS は 28〜36 MiB、GC の総割り当ては 55 KB〜8 MB(チャンク分のみ)で、**エントリ全体をバッファしない**
- .NET の読み出しは `SubReadStream` + `DeflateStream` / Inflater で、逐次処理である(ソースで確認)

#### C-7 宣言サイズが巨大・実データと食い違う ZIP — **確認済み(実機 + ソース)**

| ケース | `ZipArchive` 経由 | 自前読み取り(公開 `DeflateStream` を生データ範囲に適用) |
|---|---|---|
| 宣言 1 MiB / 実 100 MiB(Deflate) | **黙って 1 MiB で打ち切り**(Inflater が宣言サイズで止める: `Inflater.cs` L93–109)。超過を検出できない。CRC は不一致 | 宣言+1 バイトの時点で**超過を検出** |
| 宣言 2 MiB / 実 1 MiB | 1 MiB で EOF | 不足を検出 |
| 宣言 1 TiB(Zip64)/ 実 1 MiB | 開ける。`Length`=1 TiB、1 MiB で EOF。メモリ増加なし(約 82 KB) | 不足を検出 |

- SPEC §6-3「展開量が宣言サイズを超えた時点で打ち切り ERROR」は、**`ZipArchive` 経由では超過そのものを観測できない**。Deflate は黙って切り詰め、Stored は宣言を超えて返す
  - 結果として CRC 不一致などで ERROR にはなるが、攻撃者が「切り詰め後のデータの CRC」を宣言すれば MATCHED になりうる(その場合、比較対象は宣言内容と一致するので誤削除ではないが、SPEC の文言どおりではない)
  - → §4 で、**自前のセントラルディレクトリリーダー + 公開 `DeflateStream`** を推奨する(判断事項 Q-3)

#### C-8 `System.IO.Hashing.Crc32` — **確認済み(実機)**

- NuGet `System.IO.Hashing` 10.0.12 の `Crc32.HashToUInt32` / `Append` / `GetCurrentHashAsUInt32` は、ZIP の CRC と一致した
- 共有フレームワークには含まれないため、パッケージ参照が必要
- Microsoft 製・MIT・.NET と同じ版数系列・追加の推移的依存なし → 採用を推奨(§9)

### D. ハードリミット(実測)

注: スループットは OS のキャッシュが温まった状態の値で、ディスクから冷えた状態で読む場合は未測定。

| チャンク | Deflate MB/s | Stored MB/s | ピーク WS | 割り当て |
|---|---|---|---|---|
| 4 KiB | 256 | 765 | 33 MiB | 55 KB |
| 16 KiB | 303 | 1156 | 33 MiB | 79 KB |
| 64 KiB | 314 | 1336 | 34 MiB | 178 KB |
| **256 KiB** | **325** | **2120** | **28 MiB** | 571 KB |
| 1 MiB | 320 | 2373 | 30 MiB | 2.1 MB |
| 4 MiB | 315 | 1428 | 36 MiB | 8.4 MB |

- **推奨チャンク: 256 KiB**(2本のバッファで計 512 KiB。`ArrayPool` は使わず、1回だけ確保して再利用する)
- 5 GiB のゼロデータ(Zip64)も正しく比較できた(3.3 GB/s、ピーク WS 34 MiB)
- **1エントリ 16 GiB**: メモリは一定なので、上限は実質的に**処理時間の上限**になる(Deflate で約 55 秒/16 GiB)。妥当と判断し、**初期案の 16 GiB を維持**することを推奨する
- **エントリ総数 100,000**: `ZipArchive` で開いて列挙した場合
  - 10万件: 112 ms、ヒープ約 35 MB、ピーク WS 79 MiB
  - 100万件: 1.0 s、ヒープ約 344 MB、ピーク WS 401 MiB
  - → 1件あたり約 350 B。**100,000 は妥当**
- `ZipArchive` はセントラルディレクトリを全件読み込むので、途中で止められない。**EOCD / Zip64 EOCD の宣言件数とセントラルディレクトリのサイズを、構築前に自前で読んで拒否する**(自前リーダーなら自然に実現できる)
- 上限超過のテスト方法(GiB 級の書き込みを避ける設計)
  - 巨大な宣言サイズ: `RawZipBuilder` で Zip64 の宣言を 1 TiB にし、実データは数 KB(ZIP は KB 級)
  - 16 GiB 超の検出: 実データを作らず、**定数をテスト用に注入できるようにする**(例: `Limits.MaxEntryBytes` を 1 MiB にして 1 MiB+1 で検証)
  - 展開爆弾: ゼロ 64 MiB を Deflate すると約 64 KB の ZIP になる。ディスク上には 64 KiB の比較対象ファイルだけを置き、宣言サイズ = 64 KiB にして超過検出を確認する。ディスク書き込みは KB 級
  - エントリ数: EOCD の件数を詐称した KB 級の ZIP と、実際の 100,001 件の ZIP(約 11 MB)の2種。後者は CI でもその場で生成できる

---

## 3. 手動確認手順(許可範囲外のため実機確認しなかった項目)

共通の準備: Phase 3 完了後の `unextract` に、診断用の隠しオプション `--diag-recycle <file>` を入れておく。これは指定ファイル1件をごみ箱に送り、Sink のログ(PreDeleteItem の dwFlags、hrDelete、psiNewlyCreated、保持ハンドルの最終パス)を表示するもので、スパイクの `winspike recycle` に相当する。各手順では、**中身が無価値なダミーファイルだけ**を使う。

| ID | 対象 | 必要な環境 | 手順 | 期待される結果 | 想定と違った場合に影響を受ける SPEC |
|---|---|---|---|---|---|
| M-1 | USB メモリ(FAT32/exFAT) | 消えても困らない USB メモリ | 1) USB にダミーの `a.txt` と、それを含む ZIP を作る 2) `--diag-recycle E:\a.txt` を実行 3) `unextract a.zip --target E:\x` を実行 | 事前のドライブ種別判定(`DRIVE_REMOVABLE`)で全件 ERROR、ファイルは残る。診断モードでは、PreDeleteItem の dwFlags に 0x80 がない、または拒否され、ファイルが残る | §10.1(フォールバック検出)、§17-27。0x80 が立ったまま完全削除された場合は、**PreDeleteItem での拒否が効かない**ので、ドライブ種別の事前除外が唯一の防御になる |
| M-2 | 固定ディスクとして見える USB SSD/HDD | 外付け SSD(NTFS) | M-1 と同じ。`GetDriveType` が `DRIVE_FIXED` を返すかも記録する | ごみ箱が使えればごみ箱へ移動(psiNewlyCreated あり)。使えなければ拒否されて ERROR | §10.1。FIXED と判定されたのに完全削除された場合は、事前除外の根拠を見直す |
| M-3 | ネットワークドライブ / UNC | テスト用の共有(書き込み可・中身は無価値) | `\\server\share\t\a.txt` と、ドライブ文字に割り当てた `Z:\t\a.txt` の両方で、M-1 と同じ操作 | `DRIVE_REMOTE` または UNC で事前に ERROR。診断モードでは拒否されてファイルが残る | §4(UNC ルートの拒否)、§10.1 |
| M-4 | ごみ箱の容量超過 | テスト用の固定ドライブ(またはテスト用ユーザー)で、ごみ箱の最大サイズを最小(例: 1 MB)に設定できる環境 | 1) ごみ箱の設定で最大サイズを 1 MB にする 2) 10 MB のダミーで `--diag-recycle` 3) 設定を元に戻す | PreDeleteItem で 0x80 がないため拒否、またはファイルが残る。**完全削除されないこと** | §10.1。完全削除された場合は、容量超過に対する事前判定(`SHQueryRecycleBin` と最大サイズの比較など)の追加が必要 |
| M-5 | `FOF_WANTNUKEWARNING` / `FOFX_RECYCLEONDELETE` | M-1/M-3 の環境 | フラグを変えて M-1/M-3 を繰り返す | ダイアログの有無、0x80 の有無、結果を記録する | §10.1 のフラグ選択 |
| M-6 | 8.3 名の生成が無効なボリュームでの長いパス | テスト用 VHD(NTFS、`fsutil 8dot3name set <vol> 1`) | 300 文字超のダミーで `--diag-recycle` | ごみ箱へ移動、または失敗して ERROR(ファイルは残る) | §10.1(長いパス)、B-6 |
| M-7 | OneDrive のクラウドプレースホルダ | OneDrive にサインインしたテスト用アカウント | 1) OneDrive フォルダに、オンライン専用・ローカル・常に保持の3状態のダミーと、同じ内容の ZIP を置く 2) `--dry-run` | 3状態とも SKIPPED_SPECIAL_FILE。**オンライン専用のファイルがハイドレートされないこと**(状態アイコンが変わらない) | §7.3 の未確定事項。ハイドレート済みが通常ファイルに見える場合は扱いを再決定する |
| M-8 | symlink | 開発者モード、または管理者 | `mklink a.txt real.txt`(ファイル)、`mklink /D`(ディレクトリ) | SKIPPED_SPECIAL_FILE | §7.3、§17-20 |
| M-9 | NativeAOT の実行 | VC++ ビルドツールのある開発機または CI | `dotnet publish -p:PublishAot=true` で作ったバイナリで `--diag-recycle` | 実機の結果(A-1)と同じログ | 配布方式(§5 Q-6) |

---

## 4. 設計判断

### 4.1 プロジェクト構成

```
src/
  Unextract.Core/       判定ロジック・ZIPリーダー・パス検証・計画立案。Win32 非依存(net10.0)
  Unextract.Windows/    IFileSystemProbe / IDeleter の実装(net10.0-windows、CsWin32)
  Unextract.Cli/        引数解析・表示・確認・終了コード(net10.0-windows)
tests/
  Unextract.Core.Tests/       偽の FS・偽の Deleter で大量検証(OS 非依存)
  Unextract.Windows.Tests/    実ファイルシステムでの統合テスト(Windows 実機 / CI)
  Unextract.TestHelpers/      RawZipBuilder、FsFixture(junction/hardlink 作成)、Snapshot
```

### 4.2 ZIP の読み取り(Core)

推奨は **自前のセントラルディレクトリリーダー + 公開 `DeflateStream`**(判断事項 Q-3)。

- EOCD / Zip64 EOCD を読み、宣言件数 > 100,000 なら、エントリを読む前にアーカイブ全体を上限超過とする
- 各エントリについて、生の名前バイト、汎用ビットフラグ(bit0 / bit6 / bit3 / bit11)、圧縮方式、CRC、サイズ、version made by、external attributes、ローカルヘッダーのオフセットを保持する
- ローカルヘッダーを読み、名前の長さ・extra の長さからデータの開始位置を求める。ローカルヘッダーとセントラルの不一致(名前、方式、フラグ)は ERROR
- データ範囲は自前の有界ストリーム(`[offset, offset+CompressedSize)`)。Stored はそのまま、Deflate は公開 `DeflateStream`。それ以外の方式(Deflate64 を含む。.NET の Deflate64 実装は internal のため)は ERROR
- 展開しながら「宣言サイズ」「ディスクサイズ」「16 GiB」のいずれかを超えた時点で打ち切る。終了時に、展開バイト数と宣言値の一致、`System.IO.Hashing.Crc32` による CRC の一致を確認する
- 代替案: `ZipArchive` + 自前の CRC・バイト数照合。実装は小さいが、超過を検出できない(C-7)、件数の事前拒否ができない(D)、生の名前バイトと made-by を取れない

```csharp
namespace Unextract.Core.Archive;

public sealed record ZipEntryRecord(
    int Index, ReadOnlyMemory<byte> RawName, bool Utf8Flag, ushort GeneralPurposeFlags,
    ushort Method, uint Crc32, ulong CompressedSize, ulong UncompressedSize,
    ushort VersionMadeBy, uint ExternalAttributes, long LocalHeaderOffset);

public interface IZipSource : IDisposable            // ZIPは FileShare.Read で読み取り専用に開く
{
    IReadOnlyList<ZipEntryRecord> Entries { get; }
    Stream OpenData(ZipEntryRecord e);               // 有界ストリーム + 展開器(未対応方式は例外)
}
public static class Limits                            // テストでは注入して小さくする
{
    public const int MaxEntries = 100_000;
    public const long MaxEntryBytes = 16L << 30;
    public const int ChunkSize = 256 * 1024;
}
```

### 4.3 エントリ名とパス検証(Core)

- 名前のデコード: UTF-8 フラグあり → 厳格な UTF-8(不正なバイト列は UNSAFE_PATH を推奨。Q-11)。フラグなし → CP437(`CodePagesEncodingProvider` を登録)
- 正規化前の拒否(§7.1): 生の文字列に対して、`\` と `/` の両方で分割してから成分ごとに判定する。予約名には `COM¹²³`/`LPT¹²³` も含める([Naming Files](https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file))。空の成分(`a//b`)と `.` の成分も拒否する(推奨)
- 曖昧性(§8): 正規化した成分列をキーに、大文字小文字を区別せずにグループ化する。ファイル `d` と、`d/…` を祖先に持つエントリの衝突も検出する。ディレクトリエントリ(末尾 `/`)はファイルとの衝突判定にだけ使う
- Unicode 正規化(NFC/NFD)はしない。macOS 由来の NFD 名は MISSING になる(安全側。README に記載)

### 4.4 Windows 抽象(Core で定義し、Windows で実装)

```csharp
namespace Unextract.Core.FileSystem;

public readonly record struct FileIdentity(ulong VolumeSerial, UInt128 FileId);

public enum ProbeStatus { Ok, Missing, ReparsePoint, HardLink, NotRegularFile, CloudPlaceholder, AccessDenied, Locked, PathTooLong, Error }

public interface IFileSystemProbe
{
    // target を開き(reparse の検査・拒否リストの判定・実パスの解決)、ハンドルを保持した ITargetRoot を返す
    TargetResolution ResolveTarget(string userSuppliedPath);
}

public interface ITargetRoot : IDisposable
{
    string FinalPath { get; }                         // 表示用。判定には使わない
    // 成分列で、中間ディレクトリを1つずつ no-follow で開いて検査・保持し、最後にファイルを開く
    ProbeResult<IOpenedFile> OpenFile(IReadOnlyList<string> components, OpenPurpose purpose);
    ProbeResult<IOpenedDirectory> OpenDirectory(IReadOnlyList<string> components);
}

public enum OpenPurpose { Verify, RecycleAfterVerify, DeletePermanentlyAfterVerify }

public interface IOpenedFile : IDisposable           // 中間ディレクトリのハンドルも、Dispose まで保持する
{
    long Length { get; }
    FileIdentity Identity { get; }
    int LinkCount { get; }
    Stream OpenRead();                                // 同じハンドルから読む(パスを開き直さない)
    bool RefreshAndCompare(FileIdentity expected, long expectedLength); // ハンドルで再取得して比較
}

public interface IOpenedDirectory : IDisposable { FileIdentity Identity { get; } bool IsEmpty(); }

public enum DeleteOutcomeKind { Recycled, DeletedPermanently, Refused, Failed, IdentityChanged, UnexpectedPermanentDeletion, WrongItemRecycled }
public sealed record DeleteOutcome(DeleteOutcomeKind Kind, string? Detail);

public interface IDeleter
{
    DeleteOutcome RecycleVerified(IOpenedFile verifiedFile, string shellPath);   // 内部で STA スレッドを使う
    DeleteOutcome DeletePermanentlyVerified(IOpenedFile verifiedFile);          // ハンドルの Disposition で削除
    DeleteOutcome RemoveEmptyDirectory(IOpenedDirectory dir);                   // 非再帰。ハンドルの Disposition で削除
}
```

テスト用の偽実装(Core.Tests):

- `FakeFileSystem`: メモリ上のツリーで、ID、reparse、リンク数、ロックを表現する。`OnBeforeDelete` フックで、検証と削除の間の書き換え・置換を注入できる
- `RecordingDeleter`: 何も消さずに呼び出しを記録する。「削除しない」ことの検証用
- `ScriptedDeleter`: 任意の `DeleteOutcome` を返し、異常系の伝播を検証する

### 4.5 処理フロー

1. **target の解決**
   - `CreateFileW(\\?\…, GENERIC_READ, share RW, FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT)` で開く
   - 起動直後に `RtlSetProcessPlaceholderCompatibilityMode(PHCM_EXPOSE_PLACEHOLDERS)` を呼ぶ(B-3)
   - target 自体が reparse point の場合は SPEC に従う(Q-4)
   - 最終パスと拒否リストを `OrdinalIgnoreCase` の成分単位で比較し、該当すれば終了コード 3
2. **ZIP を開く**: `FileShare.Read` で開く。他者は書き込めず、ZIP 自体も変更しない。件数の事前検査、全エントリの読み込み、名前のデコード、§7.1 の検証、§8 のグループ化を行う
3. **分類**(dry-run と同じ処理。各エントリについて)
   1. 中間ディレクトリを1つずつ開く(no-follow、`GENERIC_READ`、共有 RW で DELETE は共有しない)。reparse 属性があれば SKIPPED。ハンドルはそのファイルの処理が終わるまで保持する
   2. ファイルを no-follow、`GENERIC_READ`、共有 `READ` のみで開く
      - 他者が書き込みハンドルを持っていれば共有違反 → ERROR(ロック中)
      - 開けた後は、他者は書き込みもリネームもできない
   3. 次の順で判定する: reparse → SKIPPED / ディスクファイル以外 → SKIPPED / リンク数 > 1 → SKIPPED / サイズ不一致 → MODIFIED / ストリーム比較
   4. 各ファイルの FileIdentity を記録する
   5. ハンドルは閉じる(10万件のハンドルを確認プロンプトの間ずっと保持しないため)
4. **表示と確認**: 非 TTY かつ `--yes` なし → 終了コード 2
5. **削除**(MATCHED の各ファイルについて、1件ずつ)
   1. 3-1 と同様に中間ディレクトリを保持する。ファイルを開く共有モードは、ごみ箱の場合 `READ|DELETE`(Shell の移動を許すため)、完全削除の場合 `READ` のみ
   2. **再検証**: サイズ、全バイト、Identity が分類時と一致するか。不一致なら MODIFIED / ERROR
   3. ごみ箱の場合
      - 事前に除外する: ドライブ種別が `DRIVE_FIXED` 以外、UNC/リモートは ERROR
      - 1件ごとに `IFileOperation` を使う。フラグは `FOF_ALLOWUNDO|FOF_NO_UI|FOFX_EARLYFAILURE`(`FOFX_RECYCLEONDELETE` は M-5 の結果で決める)
      - `PreDeleteItem`: 0x80 がなければ `E_ABORT`。`psiItem` を開き直し、Identity が違えば `E_ABORT`
      - `PostDeleteItem`: 成功かつ `psiNewlyCreated` が NULL → `UnexpectedPermanentDeletion`
      - 完了後、保持ハンドルの `GetFinalPathNameByHandle` が `$Recycle.Bin` 配下で、かつ `psiNewlyCreated` と同じファイル(ID で比較)であることを確認する。違えば `WrongItemRecycled`
   4. 完全削除の場合: 同じハンドルで `FileDispositionInfoEx(DELETE|POSIX_SEMANTICS|IGNORE_READONLY_ATTRIBUTE)`
   5. **異常(`UnexpectedPermanentDeletion` / `WrongItemRecycled`)を1件でも検出したら、残りの削除を中止する**(推奨。Q-7)。結果は ERROR、終了コード 1。詳細をはっきり表示する
6. **ディレクトリ**: 今回実際に消したファイルの祖先ディレクトリを、深い順に処理する
   - no-follow で開く(DELETE 権限)。reparse でないこと、空であることをハンドルで確認し、`FileDispositionInfoEx(DELETE)` で削除する
   - 空でなければ `ERROR_DIR_NOT_EMPTY`(145)で失敗するので、残す(実機確認済み)。target は対象外
7. **終了サマリー**と終了コード

### 4.6 CLI

- 引数解析は自前の小さなパーサーにする(オプションは4つで、未知のオプションはエラー)。System.CommandLine は不採用(§9)
- TTY 判定は `Console.IsInputRedirected`
- 出力は SPEC §13 の形式。ログ出力のエンコーディングは UTF-8

---

## 5. 仕様との矛盾・要確認事項(SPEC は変更していない)

### 5.1 仕様と矛盾した点

| # | SPEC | 調査結果 | 提案 |
|---|---|---|---|
| X-1 | §9「UTF-8 フラグなしは CP437」 | .NET の既定はフラグなしを **UTF-8** で解釈する(C-1) | CP437 を明示して provider を登録する(自前リーダーなら自前でデコード)。SPEC の文言は変えなくてよいが、「実装で明示指定が必要」と注記を推奨 |
| X-2 | §6-3「宣言サイズを超えた時点で打ち切り ERROR」 | `ZipArchive` では超過を観測できない。Deflate は黙って切り詰め、Stored は宣言を超えて返す(C-3、C-7) | 自前リーダー(Q-3)。`ZipArchive` を使う場合は、SPEC 側を「CRC・サイズの不整合として ERROR」に緩める必要がある |
| X-3 | §6-4「CRC-32 が一致することを確認」 | ライブラリは CRC を検証しない(C-3) | 自前で計算する(`System.IO.Hashing`)。矛盾ではないが、前提(ライブラリが検証する)は成り立たない |
| X-4 | §7.2「比較は大文字小文字を区別しない」 | ディレクトリ単位で区別する設定がある(B-7)。包含判定を、区別しない文字列比較にすると誤る余地がある | 包含はハンドル連鎖による構造で保証する。拒否リストは区別しない。重複判定は区別しない。用途別に書き分けることを推奨 |
| X-5 | §16「パスベースのごみ箱 API を使う限り、競合窓を完全には排除できない」 | 窓は残るが、ハンドル保持と ID 照合で**大幅に縮小でき、事後検出もできる**(A-3) | README の文言を §6 の案に更新することを推奨 |

### 5.2 曖昧・要確認

| # | 内容 | 推奨 |
|---|---|---|
| Q-1 | システムディレクトリの**配下**も拒否するか(SPEC で未確定) | **拒否する**(Windows、Program Files 2種、ProgramData の配下)。トレードオフ: ProgramData 配下にわざと展開した ZIP を片付けられない。頻度は低く、誤削除時の被害(アプリの破損)が大きい |
| Q-2 | クラウドプレースホルダをスキップするか(SPEC で未確定) | **スキップする**。さらに `PHCM_EXPOSE_PLACEHOLDERS` で明示的に公開する。トレードオフ: OneDrive 配下では、事実上すべて SKIPPED になる可能性がある(未確認、M-7)。README に明記する |
| Q-3 | ZIP の読み取りを自前リーダーにするか | **自前リーダーを推奨**(X-2、件数の事前拒否、生の名前バイト)。トレードオフ: 実装・テスト量が増える。Deflate64 は ERROR になる |
| Q-4 | target 自体が reparse point の場合(§4「解決後の実パスで判定」と §7.3「target 自体が該当すれば SKIPPED」の関係) | 推奨: target 自体が reparse なら全エントリ SKIPPED(終了コード 1)。代替: 終了コード 3 で中止。target の祖先(`C:\Users\x\OneDrive` など)は SPEC の検査対象外とし、実パスの解決だけを行う |
| Q-5 | アーカイブ全体のリソース上限超過時の終了コード(§6.1「アーカイブは ERROR」と §14) | 推奨: 何も読まずに終了コード 3(ZIP 不正と同じ扱い)。代替: 全件 ERROR で終了コード 1 |
| Q-6 | 配布形態 | MVP は **framework-dependent、または self-contained single-file**(実機確認済み)。NativeAOT は COM ソースジェネレータ構成で準備だけしておき、M-9 の後に判断する |
| Q-7 | 完全削除の事後検出 / 別ファイルのごみ箱送りを検出したときの動作 | **以降の削除をすべて中止**し、ERROR として詳細を表示する(別ファイルがごみ箱にある場合は、復元を促す) |
| Q-8 | ごみ箱が使えるかの事前判定基準 | `GetDriveType == DRIVE_FIXED` かつ UNC でないことを必須とする。容量超過は PreDeleteItem / PostDeleteItem に依存する(M-4 の結果次第で、事前判定を追加) |
| Q-9 | 読み取り専用属性のファイル | ごみ箱では Shell が確認なしで移動した(実機)。完全削除も `IGNORE_READONLY` で可能。推奨: **一致していれば削除対象**(属性は内容ではない)。代替: ERROR にする |
| Q-10 | ZIP 側の Windows 属性(下位バイトの `FILE_ATTRIBUTE_REPARSE_POINT`)や、Unix のディレクトリ・デバイス種別 | Unix モードの種別が通常ファイル(0x8000)でも 0(未設定)でもないエントリは、すべて SKIPPED を推奨 |
| Q-11 | UTF-8 フラグ付きの不正 UTF-8 名 | UNSAFE_PATH を推奨(ファイルシステムにアクセスしない) |
| Q-12 | テスト22「更新日時」 | 最終アクセス日時は、読み取りで更新されうる(NTFS の設定による)。スナップショットの比較は、**パス・サイズ・ハッシュ・最終更新日時・属性**に限定することを推奨 |
| Q-13 | `%TEMP%` など、日常的に junction 配下にある target | SPEC どおり実パスで判定する。祖先の reparse は不問(Q-4 と同じ) |

---

## 6. リスク一覧

| # | リスク | 残存度 | 対策 / 検出 |
|---|---|---|---|
| R-1 | **TOCTOU(ごみ箱)**: PreDeleteItem の ID 照合から Shell が移動するまでの間に、対象がリネームされて別ファイルに差し替えられる | 小(数 ms の窓。リネームと作成が両方必要) | 書き込みは共有違反で阻止する。差し替えは事後の保持ハンドルの最終パスで**検出**し、以降を中止して報告する。入ったのはごみ箱なので復元できる |
| R-2 | **TOCTOU(完全削除)** | ほぼなし | 検証したハンドルで削除する。他者にリネームと書き込みを許さない |
| R-3 | **TOCTOU(中間ディレクトリ)**: 中間ディレクトリが junction に置き換えられる | 小 | `GENERIC_READ` で保持し、DELETE を共有しない(リネーム不可を実機確認)。非空ディレクトリは、その場で junction に変換できない。削除直前に、保持ハンドルで属性を再取得する |
| R-4 | **ごみ箱のフォールバックで完全削除される** | 中(フォールバック環境は未確認) | ドライブ種別の事前除外、PreDeleteItem の 0x80 検査、PostDeleteItem の NULL 検出と中止。**M-1〜M-5 の結果が出るまで残存リスクとして扱う**。最悪の場合でも1件で止まる |
| R-5 | **reparse 検査の漏れ: クラウドプレースホルダ** | 中 | 公開モードを明示する。M-7 で確認する。漏れた場合の帰結は、ハイドレーション後の比較になり、一致したときだけ削除される(バイト一致が前提なので、誤削除にはならない) |
| R-6 | reparse 検査の漏れ: 未知のタグ、ファイルシステムフィルタによる偽装 | 小 | 属性ビットで判定する(タグの種類を問わず SKIPPED) |
| R-7 | ハードリンクの検出漏れ(FAT は常に 1) | 小 | FAT ではハードリンクを作れない |
| R-8 | ZIP 解析の不備(自前リーダーのバグ) | 中 | `RawZipBuilder` による異常系テスト。`ZipArchive` との差分テスト(正常系で同じ結果になること) |
| R-9 | 長いパスと 8.3 名の無効化 | 小 | 失敗すれば ERROR。M-6 |
| R-10 | STA/COM の初期化失敗、Shell 拡張の干渉(コピーフック等) | 小 | 失敗すれば ERROR。必要なら `FOFX_NOCOPYHOOKS` を検討する |

**README への記載案(§16 の該当部分の置き換え)**

> unextract は、比較に使ったファイルハンドルを削除まで保持します。その間、他のプロセスはファイルを書き換えられません。また、削除直前にファイルID(ボリュームシリアル番号 + 128bit ファイルID)を照合し、差し替えを検出した場合は削除しません。ただし、Windows のごみ箱 API はパスで対象を受け取るため、照合と移動の間のごく短い時間に、ファイルの名前変更と差し替えが行われると、別のファイルがごみ箱に送られる可能性があります。unextract はこれを事後に検出し、残りの処理を中止して報告します(ファイルはごみ箱から復元できます)。unextract の実行中は、target ディレクトリを他のプロセスで変更しないでください。
>
> ごみ箱が使えない場所(リムーバブルドライブ、ネットワークドライブ、UNC パス)では、既定では削除しません。Windows がごみ箱を使わずに完全削除したことを検出した場合は、その時点で処理を中止し、エラーとして報告します。
>
> OneDrive などのクラウド同期フォルダのファイルは、同期状態にかかわらず削除対象になりません(SKIPPED)。
>
> UTF-8 フラグのない ZIP のファイル名は CP437 として解釈します。日本語(CP932)や、macOS で作成された ZIP(NFD 形式のファイル名)は、Missing と判定されることがあります。

---

## 7. フェーズ別タスク一覧

| ID | タスク | 完了条件 | 依存 |
|---|---|---|---|
| **Phase 0: 調査スパイク** | | | |
| P0-1 | 本 PLAN のレビューと、§5 の判断事項の決定 | ユーザーが Q-1〜Q-13 を決定する | — |
| P0-2 | 手動確認 M-1〜M-9 の実施(Phase 3 の診断オプションが必要なものは後で) | 結果を PLAN に追記する | P3-4 |
| **Phase 1: Core + dry-run** | | | |
| P1-1 | ソリューション雛形: `global.json`(10.0.x 固定)、`Directory.Build.props`(Nullable、警告をエラー扱い、AOT 解析)、LICENSE、`.editorconfig` | `dotnet build` / `dotnet test` が空で通る | P0-1 |
| P1-2 | `RawZipBuilder` テストヘルパー(スパイクの原型を製品品質にする) | 任意のフラグ、サイズ、CRC、名前のバイト列、made-by、Zip64、EOCD の件数詐称を出力できる | P1-1 |
| P1-3 | ZIP リーダー(`IZipSource`、Q-3 に従う) | C-1〜C-7 の全ケースが期待どおり(単体テスト) | P1-2 |
| P1-4 | エントリ名のデコードとパス検証(§7.1) | テスト 15〜17 が通る | P1-3 |
| P1-5 | 曖昧性の判定(§8) | テスト 21 が通る | P1-4 |
| P1-6 | `IFileSystemProbe` などの抽象と `FakeFileSystem` / `RecordingDeleter` | 抽象のレビューが完了している | P1-1 |
| P1-7 | 同一性判定エンジン(ストリーム比較、上限、CRC) | テスト 1〜10 が Fake 上で通る。テスト 9 の割り当て量の検査を含む | P1-3, P1-6 |
| P1-8 | 計画立案(削除予定ディレクトリのシミュレーション) | テスト 11〜14 の dry-run 部分が通る | P1-7 |
| P1-9 | dry-run 出力(§13 形式)と終了コード | 出力のスナップショットテスト | P1-8 |
| **Phase 2: Windows 実装** | | | |
| P2-1 | CsWin32 導入(`NativeMethods.txt/json`、COM ソースジェネレータ構成) | AOT アナライザの警告 0 | P1-1 |
| P2-2 | target の解決と拒否リスト(§4、Q-1) | テスト 29、30 が通る | P2-1 |
| P2-3 | ハンドル連鎖による Probe(reparse、リンク数、ID、ファイル種別、ロック、`\\?\`) | テスト 19、20、26 が通る(実 FS) | P2-1 |
| P2-4 | プレースホルダの公開モード設定 | 起動時に呼ぶ。失敗時の扱い(ERROR か続行か)を決定する | P2-1 |
| P2-5 | 実 FS での dry-run 結合 | テスト 22、23 が通る | P1-9, P2-3 |
| **Phase 3: 削除** | | | |
| P3-1 | 再検証(削除直前のバイト比較 + ID 照合) | テスト 24、25 が通る(Fake と実 FS) | P2-3 |
| P3-2 | `IDeleter.RecycleVerified`(STA スレッド、Sink、事前事後の検査、中止) | 通常フォルダでごみ箱へ移動、ALLOWUNDO なしの模擬で拒否、差し替えの模擬で検出(スパイク A-2/A-3 の再現) | P3-1 |
| P3-3 | `IDeleter.DeletePermanentlyVerified` | 読み取り専用を含む完全削除。テスト 27 の後半 | P3-1 |
| P3-4 | 診断用の隠しオプション `--diag-recycle` | M-1〜M-7 を実施できる | P3-2 |
| P3-5 | ディレクトリ削除(ハンドルによる非再帰削除) | テスト 11〜14 が通る(実 FS) | P3-2 |
| P3-6 | ごみ箱が使えない場所の扱い(Q-8) | テスト 27 が通る | P3-2, P0-2 |
| **Phase 4: 仕上げ** | | | |
| P4-1 | CLI(確認プロンプト、非 TTY、`--yes`、終了サマリー) | テスト 28 が通る | P3-* |
| P4-2 | README(§16 + §6 の案)、CONTRIBUTING、セキュリティポリシー | レビュー済み | P4-1 |
| P4-3 | CI(GitHub Actions `windows-latest`: build、test、publish single-file) | 全テストが緑。実機専用テストはスキップ理由を出力する | P4-1 |
| P4-4 | コード規約チェック(再帰削除 API の禁止) | `Directory.Delete(_, true)` / `FileSystem.DeleteDirectory` 等を BannedApiAnalyzers で禁止 | P1-1 |
| P4-5 | MVP 完成条件(SPEC §19)の手動確認 | example.zip で手順どおり動く | P4-1 |

---

## 8. テスト計画(SPEC §17 の30件と1対1)

種別: **Core** = Core 単体(Fake の FS / Deleter、OS 非依存)。**Win** = Windows 実機(実 FS)。

| # | 対象 | 種別 | 必要なヘルパー | CI | 備考 |
|---|---|---|---|---|---|
| 1 | 完全一致 → MATCHED | Core + Win | RawZipBuilder、FakeFileSystem / TempDir | 可 | |
| 2 | 1 バイト変更(同じサイズ)→ MODIFIED | Core | RawZipBuilder、FakeFS | 可 | |
| 3 | サイズ違い → MODIFIED | Core | 同上 | 可 | 展開しないことを、`OpenData` が呼ばれないことで確認する |
| 4 | ZIP にあるが target にない → MISSING | Core | 同上 | 可 | |
| 5 | ユーザー作成ファイルを削除しない | Core + Win | RecordingDeleter、TempDir | 可 | ZIP にないファイルが Deleter に渡らないこと |
| 6 | 0 バイト(正常 → MATCHED、CRC 不正 → ERROR) | Core | RawZipBuilder(CRC 改ざん、宣言 0 の詐称) | 可 | C-3 の zero-badcrc / zero-lie |
| 7 | 暗号化、破損 ZIP、CRC 不整合 → 削除しない | Core | RawZipBuilder(bit0 / bit6 / 方式 99、Deflate の破損、EOCD の破損) | 可 | 破損 ZIP は終了コード 3 |
| 8 | 宣言サイズを超えて展開されるエントリ → ERROR | Core | RawZipBuilder(Deflate / Stored の詐称) | 可 | |
| 9 | 宣言サイズが巨大 → ディスクサイズ超過で停止し、メモリが増えない | Core | RawZipBuilder(Zip64 で宣言 1 TiB、実データは KB 級)、ゼロの展開爆弾(64 MiB → 約 64 KB) | 可 | `GC.GetTotalAllocatedBytes` の差分 < 2 MiB を検査する |
| 10 | エントリ総数の上限超過 → ERROR | Core | RawZipBuilder(EOCD の件数詐称)+ 実際の 100,001 件(約 11 MB をその場で生成) | 可 | Q-5 で終了コードを決める |
| 11 | 空になった ZIP 由来のディレクトリ → 削除 | Core + Win | TempDir | 可(Win: ごみ箱不要の完全削除モード。ごみ箱モードは下記) | |
| 12 | ユーザーファイルが残るディレクトリ → 削除しない | Core + Win | TempDir | 可 | ERROR_DIR_NOT_EMPTY を実機で確認済み |
| 13 | 事前からある空ディレクトリ・明示的なディレクトリエントリ → 削除しない | Core + Win | RawZipBuilder(`d/` エントリ)、TempDir | 可 | |
| 14 | target を削除しない | Core + Win | TempDir | 可 | |
| 15 | `../` → target 外にアクセスしない | Core | FakeFS(アクセスログ) | 可 | Probe が一度も呼ばれないこと |
| 16 | `a/../b` → UNSAFE_PATH | Core | RawZipBuilder | 可 | |
| 17 | 絶対パス、`C:foo`、UNC、`\\?\`、ADS、予約名、末尾ドット → UNSAFE_PATH | Core | RawZipBuilder(生の名前バイト) | 可 | `COM¹` と `\` 区切りを含む |
| 18 | 兄弟ディレクトリの接頭辞一致 → target 外 | Core + Win | TempDir(`x` と `x2`) | 可 | |
| 19 | 親ディレクトリが junction → SKIPPED | Win | FsFixture.CreateJunction(`mklink /J` 相当。管理者不要) | 可 | 実機で作成を確認済み |
| 20 | symlink / reparse / ハードリンク → スキップ | Win | FsFixture.CreateHardLink、CreateJunction、CreateSymlink | 一部可 | ハードリンクと junction は CI で可。symlink は権限が必要(この環境では作成できなかった)。GitHub ホストランナーは管理者権限で動くため作成できる見込みだが**要確認**。権限がなければ Skip し、理由を出力する |
| 21 | 同一パス、大文字小文字違い、ファイル/ディレクトリ衝突 → AMBIGUOUS | Core | RawZipBuilder | 可 | C-4 のケース一式 |
| 22 | dry-run: target のスナップショットが一致する | Win | Snapshot(パス、サイズ、SHA-256、最終更新日時、属性) | 可 | Q-12(最終アクセス日時は除外) |
| 23 | 元の ZIP が残り、ハッシュと更新日時が変わらない | Win | Snapshot | 可 | ごみ箱モードと完全削除モードの両方 |
| 24 | 検証後・削除前の書き換え → 削除しない | Core + Win | FakeFS.OnBeforeDelete / Win: 削除フェーズの前に呼ばれるテスト用フック | 可 | |
| 25 | 検証後・削除前の別ファイルへの置換 → ID 照合で削除しない | Core + Win | 同上(リネームで置換) | 可 | 実現できることはスパイクで確認済み(A-3)。PreDeleteItem 内での差し替えは、Win テストではテスト用フックを Sink に注入して再現する |
| 26 | 読み取り不能・ロック中 → 削除しない | Win | FsFixture.LockExclusive(`FileShare.None` で保持)、ACL で読み取り拒否(自分で作ったファイルに限る) | 可 | |
| 27 | ごみ箱が使えない場所 → 既定では削除せず、`--delete-permanently` のときだけ削除 | Core + Win(一部手動) | Core: ScriptedDeleter(Refused / UnexpectedPermanentDeletion)。Win: ALLOWUNDO なしの模擬経路(拒否が効くことは実機確認済み) | 一部可 | **本物の「ごみ箱が使えない場所」は CI では再現が難しい**。リムーバブルドライブ、ネットワーク、容量超過は M-1〜M-4。管理者ランナーでの `\\localhost\share` 経由のテストは**要確認** |
| 28 | 非 TTY で `--yes` なし → 終了コード 2 | Win(プロセス起動) | Process ランチャー(stdin をリダイレクト) | 可 | |
| 29 | `--target` がドライブルート、`%USERPROFILE%` 自体、システムディレクトリ → 拒否 | Core + Win | 拒否判定は Core(パスの注入)、解決は Win。システムディレクトリは**読み取りなしで解決だけ**行う | 可 | Q-1 の結果で、配下のケースを追加する |
| 30 | `%USERPROFILE%` 配下の通常ディレクトリ → 許可 | Win | `%USERPROFILE%` 配下にテスト用のディレクトリを作る | 可(CI)/ ローカルは注意 | 開発機のホームに書き込むため、ローカルでは環境変数でオプトインした場合だけ実行する。Core 側では注入したプロファイルパスで検証する |

追加の推奨テスト(30件の外): ごみ箱送りの実機テスト(STA、PreDelete / PostDelete、`$Recycle.Bin` の後始末は自分のアイテムに限定)は `UNEXTRACT_RECYCLE_TESTS=1` のときだけ実行する。CI のサービスアカウントでごみ箱が動くかは**要確認**。

---

## 9. 使用パッケージ一覧

| パッケージ | 版(調査時) | 用途 | 採用理由 |
|---|---|---|---|
| Microsoft.Windows.CsWin32 | 0.3.335 | Win32 / COM の宣言生成(Windows プロジェクトのみ、`PrivateAssets=all`) | SPEC §18 の指定。COM ソースジェネレータに対応し、AOT への道がある(A-1) |
| System.IO.Hashing | 10.0.12 | CRC-32 | ライブラリは CRC を検証しない(C-3)。Microsoft 製・MIT・依存なし。自前実装より信頼できる |
| (共有フレームワーク)System.Text.Encoding.CodePages の `CodePagesEncodingProvider` | .NET 10 同梱 | CP437 | 追加パッケージ不要(C-1) |
| (テスト)xunit.v3 または MSTest、Microsoft.NET.Test.Sdk | 最新安定版 | テスト | 一般的。Phase 1 で選ぶ |
| (アナライザ)Microsoft.CodeAnalysis.BannedApiAnalyzers | 最新安定版 | 再帰削除 API の禁止(コード規約) | SPEC §2「再帰削除 API は使用しない」を機械的に担保する |

不採用:

- System.CommandLine: オプションは4つで、依存とバイナリサイズを増やす価値がない
- SharpZipLib などのサードパーティ ZIP: 依存を増やさず、自前リーダー(数百行)で足りる
- Microsoft.VisualBasic の `FileSystem.DeleteFile`: 理由は A-6

---

## 付録: 再現用コード断片(スパイクより抜粋)

```csharp
// PreDeleteItem: ごみ箱に送れない場合と、同一性の不一致を拒否する
public HRESULT PreDeleteItem(uint dwFlags, IShellItem psiItem)
{
    if ((dwFlags & 0x80 /* TSF_DELETE_RECYCLE_IF_POSSIBLE */) == 0) return HRESULT.E_ABORT;
    if (IdOf(DisplayName(psiItem, SIGDN.SIGDN_FILESYSPATH)) != expectedId) return HRESULT.E_ABORT;
    return HRESULT.S_OK;
}
// PostDeleteItem: psiNewlyCreated == null かつ SUCCEEDED(hrDelete) なら完全削除された
```

```csharp
// 完全削除: 検証したハンドルそのものを削除する(読み取り専用も可)
var info = new FILE_DISPOSITION_INFO_EX { Flags = FILE_DISPOSITION_FLAG_DELETE
    | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE };
PInvoke.SetFileInformationByHandle(h, FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfoEx,
    new ReadOnlySpan<byte>(&info, sizeof(FILE_DISPOSITION_INFO_EX)));
```

```jsonc
// NativeMethods.json(AOT を見据えた構成。csproj に <CsWin32RunAsBuildTask>true</CsWin32RunAsBuildTask>)
{ "comInterop": { "useComSourceGenerators": true,
  "preserveSigMethods": ["IFileOperationProgressSink", "IFileOperation.PerformOperations", "IFileOperation.DeleteItem"] } }
```
