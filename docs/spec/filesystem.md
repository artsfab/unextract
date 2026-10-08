# ファイル安全性仕様

役割: target確認、分類・特殊性、全ハンドル構成、逐次削除と失敗境界、安全上の限界の正本。対象解決・API・検査順序を変更するときに該当節を読む。

[target](#target-root) / [分類](#classification) / [属性](#special-files) / [ハンドル](#handles) / [削除順序](#delete-flow) / [エラー](#open-errors) / [限界](#limitations)

<a id="target-root"></a>
## targetルートと拒否位置

存在しない、またはディレクトリでなければ入力エラーとし、推測・作成しない。対象ボリュームが NTFS でない場合も入力エラーとする。ドライブルート、UNC パス (共有ルートとその配下。最終パスが `\\?\UNC\` で始まるもの、[ハンドル構成](#handles))、ユーザープロファイルそのもの、Windows ディレクトリ、Program Files、Program Files (x86)、ProgramData とその配下は拒否する (実パスの求め方は本節の既知フォルダー解決)。target の最終成分が reparse point の場合も拒否する (確認の方法は本節の確認用・保持用ハンドル)。祖先の reparse point は許すが、target は解決後の最終パス ([ハンドル構成](#handles)) で固定し、以後の包含判定はその最終パスに対して行う。

拒否対象 (ユーザープロファイルそのもの、Windows ディレクトリ、Program Files、Program Files (x86)、ProgramData) のパスを .NET の `Environment.GetFolderPath` (または文書化された `SHGetKnownFolderPath`) で得て、読み取り専用のハンドル (`FILE_READ_ATTRIBUTES` のみ) で開き (このハンドルは `FILE_FLAG_OPEN_REPARSE_POINT` を付けず reparse をたどる。既知フォルダーが junction の場合に実体のパスへ直すため)、`GetFinalPathNameByHandleW` の最終パスに直す。取得・解決に失敗した場合は、安全を確認できないため入力エラーとする。

target ルートを確認して保持する。保持用ハンドルで開く**前に**、最終成分が reparse point でないことを確認する: 同じパスを `FILE_READ_ATTRIBUTES` のみ・`FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT` で開き (確認用ハンドル)、`FileAttributeTagInfo` で reparse point でないことと、`FileIdInfo` の File ID を確認する (reparse なら入力エラー)。確認用ハンドルを閉じた後に、target ルートを保持用ハンドル ([ハンドル構成](#handles)。`FILE_SHARE_DELETE` なし) で開いて**実行終了まで保持**し、その File ID が確認用ハンドルの File ID と一致すること、保持用ハンドルがディレクトリであることを確認する (不一致は入力エラー)。続けて存在・NTFS・最終パスを確認し、最終パスを上の既知フォルダー解決で得た拒否対象の最終パスと大小文字を区別せずに序数比較する。判定不能ならエラー終了する。

<a id="classification"></a>
## targetの分類と実名解決

`analyze` は全てのエントリに表示行を与える。`delete` は処理対象のファイルエントリに結果行を与える ([表示](cli.md#output))。

| 分類 | 条件 | アーカイブの内容を読むか | `delete` での扱い |
|---|---|---|---|
| `MATCHED` | 安全な通常ファイルで、[内容検証基準](zip.md#verification) をすべて満たし全バイト一致 (Strict) | 読む | 最終確認の後に削除する (結果は `DELETED`) |
| `SAME_SIZE` | 安全な通常ファイルで、サイズが `Length` と一致する (内容一致を意味しない。Fast。[モード契約](../SPEC.md#modes)) | 読まない | 最終確認の後に削除する (結果は `DELETED`) |
| `MODIFIED` | 安全な通常ファイルだが、サイズが `Length` と異なる (内容を読まない)、またはサイズ一致で内容が異なる (Strict) | サイズ一致時のみ | 削除しない |
| `MISSING` | 大小文字まで完全一致する名前のファイルがない。途中の親成分の扱いは [解決順序](#resolution) | 読まない | 削除しない |
| `SKIPPED_SPECIAL_FILE` | 特殊な対象であると**判定できた**もの ([解決順序](#resolution)、[特殊ファイルと属性](#special-files)) | 読まない | 削除しない |
| `DIRECTORY` | ZIP のディレクトリエントリ。ディレクトリは削除対象外 | — | 削除しない |
| FATAL / STOP | 特殊かどうか、存在するかどうかを**判定できなかった**もの。内容比較候補の [内容検証基準](zip.md#verification) の 1〜5 の違反 | — | `analyze` は FATAL で打ち切り、`delete` は STOP ([失敗の境界](#failure-boundary)) |

「特殊だと判定できた」(`SKIPPED_SPECIAL_FILE`) と「判定できなかった」(FATAL / STOP) を混同しない。

<a id="resolution"></a>
### 解決手順

1. 途中の各親成分を target ルートから1成分ずつ、[実名確認](#real-names) の実名確認で確認し、下表で分類する。最初に該当した成分で分類を確定し、その先を読まない。

   | 親成分の位置にある対象 | 分類 | 理由 |
   |---|---|---|
   | 存在しない | `MISSING` | 対応するファイルは存在し得ない |
   | 大小文字だけ違う名前のディレクトリ | `MISSING` | case strict。完全一致する名前が存在しない |
   | 通常ファイル (reparse でない) | `MISSING` | 種類は判定できており、その下にファイルは存在し得ず、削除も起こらない |
   | reparse point (ディレクトリ・ファイルを問わず。junction、symlink、placeholder を含む) | `SKIPPED_SPECIAL_FILE` | 種類は判定できている。reparse 先をたどらず、その下を読まない。全体を止める必要はない。reparse の判定は他の種類より優先する |
   | 完全一致する名前の通常ディレクトリ (reparse でない) | 次の成分へ進む | — |
   | 種類・属性を判定できない (列挙・検証 API の失敗、想定外の種類) | `analyze`: FATAL、`delete`: STOP | 判定不能。「想定外の種類」は、列挙で得た属性からディレクトリ・通常ファイル・reparse point のいずれとも分類できないもので、`FILE_ATTRIBUTE_DEVICE` を持つ項目を含む |

2. 最終成分も、手順1と同じ [実名確認](#real-names) の実名確認で、親ディレクトリの項目から実名が大小文字まで一致するものを探す。無ければ `MISSING`。大小文字だけ違うもの、8.3 名など別名だけで到達するものも `MISSING`。
3. **操作の分岐**: `delete`はここから[削除順序](#delete-flow)へ進む。以下の手順4〜9は`analyze`の分類である。
4. 比較用ハンドルを開き、File IDとボリュームシリアルを、手順2で見つけた項目のFile IDとtargetルートのボリュームシリアルに照合する。不一致はFATAL。`analyze`は親File IDを取得・照合しない。
   - 存在確認後のopen失敗は、共有違反・消失・アクセス拒否を含めFATAL。編集中の1ファイルの共有違反でもanalyze全体を打ち切る。
5. そのハンドルで最終パスが期待パス (target の最終パス + `\` + ZIP の成分を `\` で連結したもの) と序数比較で一致することを確かめる。不一致はFATAL。最終パスと期待パスの形式は [ハンドル構成](#handles) のとおり (`\\?\` 接頭辞を含むまま比較する)。
6. 同じハンドルで、[特殊ファイルと属性](#special-files) の順序で特殊判定を行う。該当すれば `SKIPPED_SPECIAL_FILE`。内容は読まない。
7. サイズ (`EndOfFile`) が `Length` と異なれば `MODIFIED`。ZIP 内容は読まない。
8. サイズ一致なら、モードによって次のとおり扱う。
   - Strict: [内容検証基準](zip.md#verification) の基準で、同じハンドルから target を読んで全バイト比較を行う。
   - Fast: [内容検証基準](zip.md#verification) の内容検証を行わず、ZIP と target のどちらの内容も読まずに `SAME_SIZE` と分類する ([モード契約](../SPEC.md#modes))。
9. 比較用ハンドルを閉じて次へ進む。delete側の最終確認・削除は[削除順序](#delete-flow)だけが定める。

名前が存在するのに種類・属性・読み取り可否を安全に判定できない場合は、`MISSING` にせず `analyze` では FATAL、`delete` では STOP とする。

<a id="real-names"></a>
### 実名確認と列挙

親成分 ([解決順序](#resolution) の手順1) と最終成分 (手順2) の実名は、**確認済みの親ディレクトリをハンドルで開き、そのハンドルから項目を列挙して、成分名と序数比較する**方式で確認する。親成分と最終成分で同じ手段を使う。

1. 列挙するディレクトリを、列挙用ハンドル ([ハンドル構成](#handles)) で開く。target ルートは保持しているハンドルをそのまま使う。それ以外のディレクトリは、1つ上の列挙で見つけた項目のパスで開き、そのハンドルで File ID が列挙で見つけた項目の File ID と一致すること、ディレクトリであること、reparse でないこと、最終パスが期待パスと序数一致することを確かめる。不一致・取得失敗は `analyze` で FATAL、`delete` で STOP。
2. 同じハンドルで `GetFileInformationByHandleEx(FileIdExtdDirectoryRestartInfo / FileIdExtdDirectoryInfo)` によって項目を列挙する。各項目の名前 (ロング名)、属性、reparse tag、128 ビット File ID が得られる。`.` と `..` は照合から除く。
3. 列挙した各項目の名前を、そのディレクトリ直下で探している ZIP エントリの成分名の集合と照合する。探す名前の集合は処理対象のファイルエントリ (`delete` で `--entries` を指定した場合は選んだエントリだけ) から作る。**ディスク上の名前と ZIP の成分名が序数比較で完全に一致した項目だけ**を対応させ、その属性・reparse tag・File ID を [解決順序](#resolution) の表、analyzeの手順4、deleteの[削除順序](#delete-flow)の手順2・4で使う。
4. 列挙は8.3 の短い名前を項目として返さないため、8.3 名だけで一致するものは対応せず `MISSING` になる。大小文字だけ違うものも序数比較で一致せず `MISSING` になる。ディレクトリごとに大文字小文字を区別する設定の NTFS ディレクトリでも同じ規則になる。
5. 1回の列挙で同じ名前の項目が複数回返った場合は、**最初の1件を採用し、2件目以降は無視する**。採用した項目も、analyzeの[解決順序](#resolution)とdeleteの[削除順序](#delete-flow)にある、開いたハンドルのFile ID・親File ID (`delete`)・最終パスの確認で保護される。
6. 照合しなかった項目は保持・表示・集計・分類しない ([対象と非目標](../SPEC.md#scope))。照合結果だけをそのディレクトリについて記録し、列挙を終えたら列挙用ハンドルを閉じる。**同じディレクトリは1回の実行で1回だけ列挙する** (`delete` でも同じ)。
7. 列挙用ハンドルのオープン、項目の列挙 (終端を示す `ERROR_NO_MORE_FILES` 以外のエラー)、ハンドルの検証のいずれかが失敗した場合は、`analyze` で FATAL、`delete` で STOP とする。

**`delete`での列挙結果の再利用**: 後続も同じ列挙結果を使う。安全性と性能上の理由は[列挙の理由](../RATIONALE.md#real-names)にある。

**列挙中の変更**: 見落とされた名前はMISSINGで非削除。同名重複は最初の1件を採用する。列挙直後の改名の組合せは[未確認範囲](../OPEN_ISSUES.md#observations)による。


ディレクトリの大きさに比例して列挙の時間がかかるが、処理対象が触れるディレクトリをそれぞれ1回ずつ列挙するだけなので、総量は「処理対象が触れるディレクトリの項目数の合計」に比例する。巨大なディレクトリに少数の ZIP エントリが対応する場合は、`FindFirstFile` による1件ずつの検索より遅くなり得るが、正確さと手段の一貫性を優先する。

<a id="special-files"></a>
## 特殊ファイルと属性

ハンドル (`analyze` は比較用、`delete` は削除用) から取得した情報で、次のいずれかに該当すれば `SKIPPED_SPECIAL_FILE` とする。

**判定は次の順に行う。** 最初に `FILE_STANDARD_INFO.Directory` を判定する。ディレクトリであれば、ストリーム一覧など他の情報を取得せず `SKIPPED_SPECIAL_FILE` とする (データストリームを持たないディレクトリでは `FileStreamInfo` が `ERROR_HANDLE_EOF` で失敗するため)。ディレクトリでない対象については、以降の取得 API が1つでも失敗した場合 (`ERROR_HANDLE_EOF` を含む) は `analyze` で FATAL、`delete` で STOP とする。`analyze` では `DeletePending` を判定項目にしない (削除保留中の対象は開けず、オープン失敗として FATAL になる)。`delete` では開いたハンドルの `DeletePending` が true なら STOP とする。

- ディレクトリである (`FILE_STANDARD_INFO.Directory`)。
- reparse point である (`FILE_ATTRIBUTE_REPARSE_POINT` または reparse tag が 0 でない)。symlink、junction、クラウド placeholder を含む。
- hardlink のリンク数が2以上 (`FILE_STANDARD_INFO.NumberOfLinks`)。
- 既定の `::$DATA` 以外の名前付きデータストリーム (ADS) がある (`FileStreamInfo`)。`Zone.Identifier` を含む。
- アーカイブ自身 (処理中の ZIP または RAR) と同じ個体 (ボリュームシリアルと 128 ビット File ID が一致)。
- 属性に次の**許可集合以外のビット**が1つでもある。

**`delete` の事前判定**: 削除用ハンドルは `FILE_FLAG_BACKUP_SEMANTICS` を持たずディレクトリを開けないため ([ハンドル構成](#handles))、`delete` では削除用ハンドルを開く前に、最終成分の列挙項目 ([実名確認](#real-names)) の属性と reparse tag で次を判定し、該当すれば削除用ハンドルを開かずに `SKIPPED_SPECIAL_FILE` とする: ディレクトリ属性 (0x10) がある、reparse point (属性 0x400 または reparse tag が 0 でない)、属性に許可集合以外のビットがある。この判定は「削除しない」側にだけ使う。該当しなかった対象は削除用ハンドルを開き、上の全ての判定 (ディレクトリ、reparse、リンク数、ADS、ZIP 自身、属性) をハンドル上で改めて行う。列挙とハンドルの値の一般的一致は保証しない。S38の限定付き観測と未確認範囲は[OPEN_ISSUES](../OPEN_ISSUES.md#observations)に置く。一致しない場合でも、事前判定は削除しない側にしか働かず、ハンドル上の判定は省略しないため、削除の安全性はこの一致に依存しない。

許可する属性 (これらだけを理由にスキップしない):

| 属性 | 値 | 許可の理由 |
|---|---|---|
| `ARCHIVE` | 0x20 | 通常の作成・更新で付く |
| `NORMAL` | 0x80 | 他の属性がない状態 |
| `HIDDEN` | 0x2 | 表示上の属性で、格納や読み取りの意味を変えない。ZIP 展開ツールが復元することがある。`analyze` で全パスを表示する |
| `NOT_CONTENT_INDEXED` | 0x2000 | 検索インデックスの指定だけ |
| `COMPRESSED` | 0x800 | NTFS 透過圧縮。データはローカルにあり通常の読み取りで比較できる |
| `SPARSE_FILE` | 0x200 | 未割り当て範囲は0として読める。データはローカル |
| `ENCRYPTED` | 0x4000 | EFS。所有者は透過的に読める。読めなければ読み取り失敗で FATAL / STOP |

スキップする属性の例: `READONLY` (0x1。削除からの明示的な保護)、`SYSTEM` (0x4。OS・アプリの管理対象)、`DIRECTORY` (0x10)、`DEVICE` (0x40)、`TEMPORARY` (0x100。アプリが使用中の一時ファイル)、`REPARSE_POINT` (0x400)、`OFFLINE` (0x1000)、`INTEGRITY_STREAM` (0x8000)、`VIRTUAL` (0x10000)、`NO_SCRUB_DATA` (0x20000)、`RECALL_ON_OPEN` (0x40000)、`PINNED` (0x80000)、`UNPINNED` (0x100000)、`RECALL_ON_DATA_ACCESS` (0x400000)、`STRICTLY_SEQUENTIAL` (0x20000000)、および**未定義・将来のビット**。許可リスト方式とし、知らない属性は削除しない。

target ファイルの内容は特殊判定の後でだけ読む。比較用・削除用ハンドルは `FILE_FLAG_OPEN_NO_RECALL` と `FILE_FLAG_OPEN_REPARSE_POINT` で開くため、オープンや特殊判定でクラウドのオンライン取得を起こさず、最終成分の reparse point をたどらない。

<a id="handles"></a>
## ハンドル・基準・削除

<a id="handle-config"></a>
### ハンドルの開き方

| 用途 | アクセス | 共有モード | フラグ | 保持期間 |
|---|---|---|---|---|
| アーカイブ (ZIP・RAR) | 読み取り | `FILE_SHARE_READ` | — | 実行終了まで |
| target ルート | `FILE_LIST_DIRECTORY \| FILE_READ_ATTRIBUTES` | `FILE_SHARE_READ \| FILE_SHARE_WRITE` (DELETE を共有しない) | `FILE_FLAG_BACKUP_SEMANTICS` | 実行終了まで |
| 列挙用 (target ルート以外のディレクトリ) | `FILE_LIST_DIRECTORY \| FILE_READ_ATTRIBUTES` | `FILE_SHARE_READ \| FILE_SHARE_WRITE` | `FILE_FLAG_BACKUP_SEMANTICS \| FILE_FLAG_OPEN_REPARSE_POINT` | そのディレクトリの検証と列挙の間だけ |
| 比較用 (`analyze`) | `GENERIC_READ` | `FILE_SHARE_READ` | `FILE_FLAG_BACKUP_SEMANTICS \| FILE_FLAG_OPEN_REPARSE_POINT \| FILE_FLAG_OPEN_NO_RECALL \| FILE_FLAG_SEQUENTIAL_SCAN` | `analyze` のそのエントリの判定中だけ |
| 削除用 (`delete`) | `GENERIC_READ \| DELETE \| FILE_READ_ATTRIBUTES \| SYNCHRONIZE` | `FILE_SHARE_READ` | `FILE_FLAG_OPEN_REPARSE_POINT \| FILE_FLAG_OPEN_NO_RECALL` | `delete` のそのエントリの検査・比較・最終確認・削除の間だけ |

- RAR では UnRAR.dll がパスでアーカイブを別に開く ([RARのDLL](rar.md#runtime))。これはアーカイブ側の読み取りであり、上表の target 側のハンドルには含めない。
- `analyze` は比較用ハンドルだけを使い、削除用ハンドルを開かない。`delete` は削除用ハンドルだけを使い、比較用ハンドルを開かない。同じ対象に比較用と削除用を同時に開くことはしない (同時に開けるかどうかは未実測。[手動確認M09](../OPEN_ISSUES.md#manual-status))。
- 比較用・削除用の共有モードは `FILE_SHARE_WRITE` と `FILE_SHARE_DELETE` を含めない。開いている間、他プロセスは書き込み・改名・削除のために開けず、書き込み・削除アクセスのハンドル (または `FILE_SHARE_DELETE` を許さないハンドル) が既にあれば、こちらのオープンが共有違反で失敗する。削除用ハンドルは自分自身の `DELETE` のために `FILE_SHARE_DELETE` を必要としない。読み取りだけで `FILE_SHARE_DELETE` を許すハンドル (インデクサ・バックアップ相当) や `FILE_READ_ATTRIBUTES` だけのハンドルとは共存し、その場合も削除は成立する ([共有モードの理由と観測条件](../RATIONALE.md#sharing-limits))。
- 削除用ハンドルは `DELETE` アクセスを要求するため、読み取りはできても削除の権限が無い対象 (対象の DELETE と親の DELETE_CHILD の両方が拒否されている) は開けない。この場合 `analyze` は分類できるが、`delete` は [失敗の境界](#failure-boundary) の識別確認で扱う。
- 共有モードの判定に参加するのは、読み取り・書き込み・削除のいずれかのデータアクセスを持つハンドルだけである。`FILE_READ_ATTRIBUTES` だけのハンドルを保持しても他者の改名を防げないため、target ルートは `FILE_LIST_DIRECTORY` を付けて開く。これで実行中の target ルートの改名・削除を防ぐ。途中のディレクトリは保持しないため改名され得るが、最終パスと親 File ID の比較で検出する。
- target ルートの保持用ハンドルには `FILE_FLAG_OPEN_REPARSE_POINT` を付けない (最終成分の reparse をたどる)。そのため、最終成分の reparse の確認は [Prepare](../SPEC.md#prepare) の手順5の確認用ハンドル (`FILE_READ_ATTRIBUTES` のみ、`FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT`) で行い、保持用ハンドルの File ID との一致で、確認した個体と保持した個体が同じであることを確かめる。
- 比較用ハンドルの `FILE_FLAG_BACKUP_SEMANTICS` はディレクトリも開けるようにしてハンドル上で `Directory` を判定するためのもので、特権の有効化はしない。削除用ハンドルには付けない (ディレクトリは開けずアクセス拒否になる。`delete` では [特殊ファイルと属性](#special-files) の事前判定と [失敗の境界](#failure-boundary) の識別確認で扱う)。
- 最終パスは `GetFinalPathNameByHandleW(FILE_NAME_NORMALIZED | VOLUME_NAME_DOS)` で取得する。ディスク上の実際の大小文字が返るため、case strict と、途中の reparse point による経路のすり替えをこの序数比較で確認できる。
- 最終パスは `GetFinalPathNameByHandleW` が返す形式 (`\\?\C:\...`) のまま保持・比較する。target の最終パスは保持用ハンドルから取得した値を使い、期待パスは「target の最終パス + `\` + ZIP の成分を `\` で連結したもの」で組み立てる。利用者が入力したパスの文字列を正規化して比較に使わない。`\\?\UNC\` で始まる最終パスの target は [targetルート](#target-root) の UNC 拒否として入力エラーとする。
- 情報取得は `GetFileInformationByHandleEx` の `FileIdInfo`、`FileStandardInfo`、`FileBasicInfo`、`FileAttributeTagInfo`、`FileStreamInfo` と、親ディレクトリの File ID を返す `DeviceIoControl(FSCTL_READ_FILE_USN_DATA)` (USN_RECORD_V3 の `ParentFileReferenceNumber`) で行う。`FSCTL_READ_FILE_USN_DATA` は USN ジャーナルが非アクティブなボリュームでも親 File ID を返す ([USN観測の条件](../RATIONALE.md#current-state))。
<a id="hardlink-parent-id"></a>

**hardlinkの親File ID**: USNが返す親IDを常に開いた名前の親と扱ってはならない。非dir・非削除保留・リンク数2以上を確認した対象だけ親照合前の非削除例外を許す。通常候補の親照合、取得失敗のSTOP、最終確認は緩めない。[理由](../RATIONALE.md#hardlink-parent-id)と[環境差の未確認](../OPEN_ISSUES.md#observations)を参照する。

- .NET からは `LibraryImport` による kernel32 の P/Invoke と `SafeFileHandle` で呼ぶ。ntdll の未文書 API、reflection は使わない。

<a id="baselines"></a>
### 列挙由来の基準とM0

`delete` は、1エントリの処理の間だけ次の2つの基準を持ち、処理が終われば捨てる。エントリをまたいで保持せず、ファイルにも保存しない。全件の削除候補とその状態を保持することはしない。

- **列挙由来の基準**: 最終成分の列挙項目の File ID、target ルートのボリュームシリアル、[解決順序](#resolution) の手順1でたどった親ディレクトリの File ID、期待パス。削除用ハンドルのオープン直後の照合 ([削除順序](#delete-flow)) と、オープン失敗時の識別確認 ([失敗の境界](#failure-boundary)) に使う。
- **M0**: 削除用ハンドルを開いた直後、内容比較の**前に**、同じハンドルから取得した次の値。ボリュームシリアル番号と 128 ビット File ID、親ディレクトリの 128 ビット File ID、`EndOfFile`、`LastWriteTime`、`ChangeTime`、属性、`NumberOfLinks` (=1)、ストリーム一覧 (`::$DATA` のみ)、reparse tag (reparse でないこと)、最終パス。最終確認 ([削除順序](#delete-flow) の手順7) の比較基準に使う。

`analyze` はこれらの基準を持たない (削除しないため)。

<a id="delete-flow"></a>
### deleteの順序

処理対象の各ファイルエントリについて、次を順に行う。パスを使うのは手順3のオープンだけで、手順4〜9は全て同じハンドルに対して行い、パスを再解決しない (手順3が失敗したときの [失敗の境界](#failure-boundary) の識別確認だけは例外で、削除は行わない)。

1. **解決**: [解決順序](#resolution) の手順1・2 (親成分と最終成分の実名確認)。
2. **事前判定**: [特殊ファイルと属性](#special-files) の事前判定 (列挙項目の属性による)。該当すれば `SKIPPED_SPECIAL_FILE` とし、手順3以降を行わない。
3. **オープン**: 期待パスを削除用ハンドル ([ハンドル構成](#handles)) で開く。
4. **照合**: 同じハンドルで、ボリュームシリアルと File ID、最終パスを列挙由来の基準 ([列挙基準とM0](#baselines)) と照合する。その後 `FILE_STANDARD_INFO` を取得する。非ディレクトリ、非削除保留、リンク数2以上を確認できた対象は、親 File ID を照合せず手順5の特殊判定へ進む (必ず非削除)。それ以外は親 File ID も列挙由来の基準と照合し、不一致・取得失敗は STOP とする。リンク数の取得失敗も STOP。手順5にはここで取得した `FILE_STANDARD_INFO` を渡し、同じ値で判定する。
5. **検査と M0**: 同じハンドルで [特殊ファイルと属性](#special-files) の特殊判定 (`DeletePending` を含む) とサイズの判定 ([解決順序](#resolution) の手順6・7) を行い、M0 ([列挙基準とM0](#baselines)) を記録する。`SKIPPED_SPECIAL_FILE` または `MODIFIED` ならハンドルを閉じて次へ進む。手順4で親 File ID を照合しなかった対象は、reparse または hardlink としてスキップするか、情報取得の失敗で STOP する。内容比較・最終確認・削除へは進めない。
6. **全バイト比較 (Strict)**: 同じハンドルから target の内容を先頭から読み、ZIP エントリの展開ストリームを開いて、[内容検証基準](zip.md#verification) の基準で比較する。全バイト比較はこのエントリについて1回だけ行う。不一致なら `MODIFIED` としてハンドルを閉じ、次へ進む。Fast はこの手順を行わない ([モード契約](../SPEC.md#modes))。
7. **最終確認**: 手順6の読み取りが長時間になり得るため、削除の直前に同じハンドルで M0 の**全項目** (File ID とボリュームシリアル、親 File ID、最終パス、`EndOfFile`、`LastWriteTime`、`ChangeTime`、属性、リンク数、ストリーム一覧、reparse 状態) を再取得して M0 と完全に一致すること、`Directory` と `DeletePending` が false であることを確認する。親File IDと最終パスは**どちらも省略しない**。片方だけでは検出できない差し替えの例は[同一性の理由](../RATIONALE.md#identity-path)に置く。
8. **削除**: 同じハンドルに対して `SetFileInformationByHandle(FileDispositionInfoEx)` で削除を指示する。flags は `FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS` (0x3) とする。`FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE` は指定しない。POSIX semantics により、`FILE_SHARE_DELETE` 付きで開いている他のハンドルがあっても、このハンドルを閉じた時点で名前が消える。
9. **成立確認**: 同じハンドルで `FILE_STANDARD_INFO.DeletePending` が true であることを確認し、ハンドルを閉じて `DELETED` とする。**`SetFileInformationByHandle` の成功だけでは成立としない** (`DELETE` ビットを含まない flags でも成功を返し、何も削除しないため)。削除の指示が失敗した場合 (read-only など) は `DeletePending` は false のままである。`NumberOfLinks` は指示後に 0 と報告されることを観測しているが、公式資料の裏付けがないため判定条件にせず、補助的な観測にとどめる。

全バイト比較した対象と削除する対象は、同じハンドルで固定された同じファイルオブジェクトである。削除はこのハンドルに対してだけ行う。**パスを再解決して別に削除する方式 (パスベースの `File.Delete` など) は使わない。**

<a id="failure-boundary"></a>
### DELETE_FAILEDとSTOPの境界

`delete` の各エントリの失敗は「その対象を残して次へ進む `DELETE_FAILED`」と「以後の処理を全て止めるSTOP (指示後は対象が削除された可能性を含む)」に分ける。**どちらか確定できないもの、未知・曖昧なエラーは STOP 側に倒す。**

| 段階 ([削除順序](#delete-flow)) | 状況 | 扱い |
|---|---|---|
| 1 (解決) | 列挙用ハンドルのオープン失敗・検証不一致・列挙エラー、親成分が想定外の種類 | STOP |
| 3 (オープン) | 共有違反、アクセス拒否で、**識別確認**の時点で対象が列挙由来の基準と一致する通常ファイルに見える | `DELETE_FAILED`、続行 (削除しない。Strict でも内容比較は行っていない) |
| 3 (オープン) | 共有違反、アクセス拒否で、識別確認が失敗・不一致・判定不能 (別の個体、ディレクトリ、reparse、削除保留中に見えるなど) | STOP |
| 3 (オープン) | 対象が見つからない、途中のパスが見つからない (対象消失、親の差し替え) | STOP |
| 3 (オープン) | reparse・クラウド関連のエラー、名前解決のエラー、その他の未知・曖昧なエラー | STOP |
| 4 (照合) | File ID・ボリューム・親 File ID・最終パスの不一致、情報取得の失敗 | STOP |
| 5 (検査) | 情報取得の失敗、`DeletePending` が true | STOP |
| 6 (比較) | [内容検証基準](zip.md#verification) の 1〜5 の違反 (ZIP 側の異常)、target の読み取り失敗、実測展開量の合計の超過 ([ZIP上限](zip.md#limits)) | STOP |
| 6 (比較) | [内容検証基準](zip.md#verification) の 6 だけが不成立 (内容が異なる) | `MODIFIED`、続行 (削除しない) |
| 7 (最終確認) | M0 との不一致、取得失敗 | STOP |
| 8 (削除) | 失敗 (エラーの種類を問わない。最終確認の後に read-only が付いた場合などを含む) | STOP (`DeletePending` が true か確認できない場合は「削除された可能性あり」と報告) |
| 9 (成立確認) | 削除の成立を確認できない | STOP (その対象は「削除された可能性あり」と報告) |
| 任意 | 想定外の例外 | STOP (削除の指示の後なら「削除された可能性あり」と報告) |

**識別確認**: 手順3が共有違反またはアクセス拒否で失敗したときだけ、同じ期待パスを `FILE_READ_ATTRIBUTES` のみ (共有モードの判定に参加しない) と `FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OPEN_NO_RECALL` で開き、File ID とボリュームシリアル、親 File ID、最終パスを列挙由来の基準 ([列挙基準とM0](#baselines)) と比較し、ディレクトリでないこと、reparse でないこと、`DeletePending` が false であることを確かめる。全て一致すれば「識別確認の時点で、列挙由来の基準と一致する通常ファイルに見える」とする。このハンドルでは削除しない。

識別確認の限界:

- 識別確認は、拒否された削除用オープンとは別のオープンである。2回のオープンの間に対象が差し替えられる可能性を排除できないため、**拒否された削除用オープンが見た個体と識別確認が見た個体が同じであることは保証しない。** また、識別確認は削除用オープンが拒否された理由 (使用中、権限不足など) を確定するものでもない。
- 識別確認が一致した場合の結果は「そのファイルを削除せずに残して次へ進む (`DELETE_FAILED`)」だけであり、削除は行わない。したがって、この限界が誤削除につながることはない。
- 識別確認が不一致・失敗・判定不能の場合は STOP する。

`analyze` では、オープンの失敗 (共有違反を含む) は FATAL とする ([解決順序](#resolution) の手順4)。`analyze` と `delete` で扱いが異なるのは意図した仕様である。

エラーコード対応は[次の表](#open-errors)で定める。既に削除したファイルは自動復元しない。

<a id="open-errors"></a>
## 削除用openのエラーコード

| 段階 | Win32エラー・状況 | 扱い |
| --- | --- | --- |
| オープン  |  `ERROR_SHARING_VIOLATION` 32  |  識別確認が列挙由来の基準と「一致に見える」なら `DELETE_FAILED` (内容比較なし)、不一致・失敗・判定不能なら STOP |
| オープン  |  `ERROR_ACCESS_DENIED` 5  |  同上 |
| オープン  |  `ERROR_FILE_NOT_FOUND` 2、`ERROR_PATH_NOT_FOUND` 3  |  STOP |
| オープン  |  reparse・名前解決関連 (`ERROR_CANT_ACCESS_FILE` 1920、`ERROR_CANT_RESOLVE_FILENAME` 1921、`ERROR_NOT_A_REPARSE_POINT` 4390 など)、クラウド関連 (`ERROR_CLOUD_FILE_*`)  |  STOP |
| オープン  |  その他全て  |  STOP |
| オープン成功  |  —  |  [削除順序](#delete-flow)の照合・検査へ |

<a id="limitations"></a>
## 既知の限界

- **`analyze` から `delete` までの変更**: `delete` は `analyze` の結果を使わず、実行時点の状態を検証する。間に内容・名前・属性などが変わった場合、`delete` はその時点の状態で判定する (Strict では内容が違えば `MODIFIED`)。`analyze` と `delete` の分類は一致するとは限らない ([引数と終了状態](cli.md#arguments))。
- **途中の STOP**: `delete` が途中で STOP した場合、それまでに削除したファイルは戻らない ([削除0件の範囲](../SPEC.md#zero-deletions))。target 側の判定不能は、そのエントリの処理の時点で初めて検出されるため、検出までに他のファイルが削除されていることがある。
- **長時間の `delete`**: 列挙からそのエントリの処理までの時間が長くなり得るため、その間の外部の変更により STOP が増え得る (誤削除にはならない。[実名確認](#real-names))。
- **削除用ハンドルを開いている間の内容の書き換え**: 共有モード (`FILE_SHARE_READ` のみ) により、他者は書き込み・改名・削除のために開けない。既存の書き込みハンドルがあればこちらのオープンが失敗する。書き込み可能なメモリマップなど、共有モードの判定に参加しない経路による書き込みが可能かは未実測である ([手順保留M11](../OPEN_ISSUES.md#procedure-review))。
- **削除用ハンドルを開いている間の属性変更・ADS 作成・hardlink 追加**: これらは共有モードでは拒否されない。unextract は削除の直前に最終確認 ([削除順序](#delete-flow) の手順7) を行うため、これらが問題になるのは最終確認から削除の指示までの短い間に限られる。その間に追加された ADS は、ファイルと一緒に削除される。追加された hardlink では、削除されるのは unextract が開いた名前だけで、データは他の名前に残る。read-only の付与は削除の失敗となり STOP する。
- **祖先ディレクトリの改名**: 全祖先・別プロセス・全モードの改名可否は未確認である。限定付き観測は[OPEN_ISSUES](../OPEN_ISSUES.md#observations)による。改名できた場合でも、最終確認で最終パスの不一致として検出し STOP する。最終確認から削除の指示までの間の改名は検出できない。
- **削除権限の差**: 読み取りはできるが削除の権限が無いファイルは、`analyze` では分類されるが、`delete` では削除用ハンドルを開けず `DELETE_FAILED` (内容比較なし) または STOP になる。
- **性能**: Strict の `delete` は、削除するファイルについて ZIP エントリの展開と target の読み取りを1回行う。`analyze` の後に `delete` を実行すると、そのファイルの I/O は合計で約2倍になる。巨大なファイルでは削除用ハンドルを開いている時間が長くなり、その間そのファイルは他プロセスから書き込めない。

Fastは内容比較だけを省き、その他の解決・照合・M0・最終確認・削除・成立確認を同じ順で実施する。同一性/API/検査順序の変更時は[理由](../RATIONALE.md#filesystem)と[未確認範囲](../OPEN_ISSUES.md#observations)も読む。
