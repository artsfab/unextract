# RAR仕様

役割: RAR の受理範囲、UnRAR.dll の使用条件、RAR の名前・種別・上限、内容検証の正本。RAR の形式判定・DLL・受理範囲・内容の読み取り経路を変更するときに読む。名前の検査・構造・上限・内容検証の基準のうち ZIP と共通のものは [ZIP](zip.md) の該当節を適用し、本書は RAR での入力と差分だけを定める。target 側の処理 ([ファイル安全性](filesystem.md)) は ZIP と同じで、本書は変更しない。

[対象](#scope) / [DLL](#library) / [判定](#format) / [列挙](#listing) / [名前](#names) / [種別](#types) / [上限](#limits) / [内容](#content) / [Fast](#fast) / [限界](#limitations)

<a id="scope"></a>
## 対象範囲

- 対象は、単一ファイルで非 Solid の RAR4 形式 (先頭の署名 `52 61 72 21 1A 07 00`) と RAR5 形式 (先頭の署名 `52 61 72 21 1A 07 01 00`) のアーカイブである。`analyze`・`delete`、Strict・Fast のすべてを ZIP と同じ契約で提供する ([SPEC](../SPEC.md))。
- 次は非対応とし、[Prepare](../SPEC.md#prepare) で FATAL (削除0件) にする。操作・モード・`--entries` に関係なく、アーカイブ全体を拒否する。
  - Solid (アーカイブの Solid フラグ `ARCHIVE_SOLID`、または Solid フラグを持つエントリが1件でもある `ENTRY_SOLID`)。
  - 分割 (ボリュームのフラグ `ARCHIVE_MULTI_VOLUME`、または前後の巻へ続くエントリが1件でもある `ENTRY_SPLIT`。第1巻・途中の巻・最終巻を問わない)。ボリュームのフラグは一覧用に開いた直後、列挙の前に拒否する ([列挙](#listing))。
  - 暗号化 (ヘッダーの暗号化、またはパスワードを要求されて開けない `ARCHIVE_ENCRYPTED`。暗号化フラグを持つエントリが1件でもある `ENTRY_ENCRYPTED`)。
  - SFX (先頭0バイト目に署名が無い `ARCHIVE_NOT_RAR`。[形式の判定](#format))。
- 暗号化は、ZIP と異なり内容比較候補だけでなく全エントリを Prepare で確認する。そのため Fast でも暗号化エントリを含む RAR は拒否される ([Fast](#fast))。

<a id="library"></a>
## UnRAR.dll

<a id="runtime"></a>
### 使用する実装と操作

- RAR の読み取りには rarlab 公式の UnRAR.dll だけを使う。独自の RAR 構造パーサは作らない。例外は[形式の判定](#format)の先頭署名の確認だけで、構造の解析はしない。
- DLL は CLI のプロセス内で使う。GUI は DLL を読み込まず、RAR も ZIP と同じく CLI の子プロセスと[機械可読出力](machine-output.md)で扱う ([GUI](gui.md#scope))。
- 使う操作は、アーカイブの open (一覧用と内容読み取り用)、ヘッダーの読み取り、エントリの `RAR_SKIP` と `RAR_TEST`、close、版の取得、コールバックの登録だけである。**`RAR_EXTRACT` と展開先の指定は使わない** (DLL にディスクへ書かせない)。
- コールバック: データの受け渡しは[内容検証](#verification)に使う。パスワードの要求、巻の変更、大きな辞書の確認には常に拒否 (中止) を返す。
- DLL はパスを受け取って自分でアーカイブを開く (ハンドルを渡す API は無い)。unextract は [Prepare](../SPEC.md#prepare) の手順2で保持したアーカイブのハンドルの最終パスを渡す。保持中のアーカイブは他者による書き込み・削除・改名ができないため、DLL がパスで開くのは保持中と同じ個体である。これは Prepare の手順7 (アーカイブ自身の個体の取得) と同じ推論で、DLL のハンドルを直接確かめる手段は無い。DLL が開くハンドルは target 側の[ハンドル構成](filesystem.md#handles)に含めない。

<a id="pinning"></a>
### 版の固定と利用できない場合

- 使う DLL の版を固定する。採用版は UnRAR.dll 7.23 (ファイルの版 7.23.0、`RARGetDllVersion` が 10) の x64 版 (`UnRAR64.dll`) で、そのファイルの SHA-256 を実装の定数として持つ。版を変えるときは、この定数と[理由](../RATIONALE.md#rar)の観測を回帰試験で確かめ直してから更新する。
- ロードの前に、ロードするファイルの SHA-256 が定数と一致することを確かめ、確かめたファイルだけを絶対パスでロードする。DLL の検索順序 (PATH、作業ディレクトリなど) に頼らない。ロード後に `RARGetDllVersion` が採用版の値であることも確かめる。
- この照合の目的は、検証した版と同じ挙動を保つことである。インストール先に書き込める攻撃者への防御ではない。
- **読み込み元は、実行中の `unextract.exe` と同じフォルダーの `UnRAR64.dll` だけ**とする (GUI の配布物では `cli\UnRAR64.dll`)。引数・環境変数・設定による指定は設けない。
- **DLL はリポジトリにも製品の配布物にも含めない。** 利用者が rarlab から採用版を入手し、読み込み元に置く (別の版は SHA-256 が一致せず使えない)。導入手順は[ルートREADME](../../README.md#rar-dll)にある。理由は[理由](../RATIONALE.md#rar-pinning)。
- DLL が見つからない、SHA-256 が一致しない、ロードできない、`RARGetDllVersion` が一致しない場合は、**RAR の実行だけ**を Prepare の FATAL (`RAR_LIBRARY_UNAVAILABLE`、削除0件) にする。DLL の確認は Prepare の手順2で、target・entries に触れる前に行う。ほかの RAR 処理方式へ自動で切り替えない。
- この FATAL の説明には、利用者が原因と対処を判断できるように、4つの原因のどれか、読み込み元の絶対パス、必要な DLL (UnRAR.dll 7.23 の x64 版 `UnRAR64.dll`)、そのパスに採用版を置けば使えること、ZIP の処理には影響しないことを含める。人間向けの文言は [CLI](cli.md#input-errors)、機械可読出力では同じ説明を `result.error.message` に入れ (`stage=prepare`、`code=RAR_LIBRARY_UNAVAILABLE`)、GUI はそれを解析・削除の失敗の詳細として示す ([GUI](gui.md#cli-integration))。
- ZIP の実行では DLL を探さず、読み込まない。DLL の有無は ZIP の動作に影響しない。

<a id="format"></a>
## 形式の判定

- **ファイル名の拡張子が `.rar` (大小文字を区別しない) の入力だけを RAR として扱う。** それ以外の入力は従来どおり ZIP として扱い、処理を変えない (中身が RAR でも ZIP として開けず FATAL になる。`.cbr` など他の拡張子の RAR は対象外)。拡張子は指定されたパスの最終成分で判断する。
- RAR として扱う入力は、[Prepare](../SPEC.md#prepare) の手順2で保持したハンドルから先頭の8バイトだけを読み、0バイト目から RAR4 または RAR5 の署名が始まることを確かめる。無ければ FATAL (`ARCHIVE_NOT_RAR`、削除0件)。DLL は SFX も開けるため、SFX はこの確認で拒否する。開いた後の署名の読み取りが失敗した場合は、ZIP の読み取り失敗と同じく FATAL (`ARCHIVE_UNREADABLE`、削除0件) とし、DLL をロードしない。

<a id="listing"></a>
## 列挙とPrepareの検査

- Prepare では、DLL でアーカイブを一覧用に開き、ヘッダーを先頭から順に読み (各エントリは `RAR_SKIP`)、終端まで列挙する ([上限](#limits)の超過を検出した場合はそこでやめる)。一覧用の open は、前の巻から続くエントリも列挙する開き方 (DLL の `RAR_OM_LIST_INCSPLIT`) とする (`RAR_OM_LIST` はそのエントリを列挙から黙って除くため、`ENTRY_SPLIT` を検出できず、内容読み取り用の open の並びとも食い違う)。**DLL が列挙したエントリを、Prepare の手順6の「全エントリ」とする。** エントリの順 (アーカイブの順) は DLL が返した順で、`index` と人間向け表示の `#n` はこの順の1始まりの番号である。
- **ボリューム**: 開いた直後、ヘッダーを読む前に、アーカイブのボリュームのフラグを確かめる。あれば列挙せずに [Prepare](../SPEC.md#prepare) の手順2の FATAL (`ARCHIVE_MULTI_VOLUME`、削除0件) とする。手順2の FATAL なので、entries・target の入力エラーより先に報告され、`analyze` はヘッダー・見出し・件数を表示しない ([analyzeの表示](cli.md#analyze-output))。DLL は分割の第1巻・途中の巻を列挙し終えられない (観測。[理由](../RATIONALE.md#rar-scope)) ため、列挙の結果によらずここで拒否する。
- 列挙が終端以外の理由で失敗した場合 (ヘッダーの破損など。ヘッダーの読み取りが成功・終端以外を返した場合と、`RAR_SKIP` が成功以外を返した場合を含む) は FATAL (`ARCHIVE_UNREADABLE`)。開けない場合は FATAL (`ARCHIVE_OPEN_FAILED`)。
- **切り詰められた RAR**: DLL が正常に列挙できた範囲を全エントリとして処理する。DLL は末尾の切り詰めを多くの位置でエラーにせず列挙を終えるため、欠けたエントリは存在しないものとして扱われる ([限界](#limitations))。
- 手順6では、開いた時点のアーカイブの性質 (Solid、ヘッダーの暗号化) と、各エントリの次の性質を検査し、該当すれば FATAL (削除0件) とする。コードは[機械可読出力](machine-output.md#codes)の対応による。
  - Solid、前後の巻へ続く、暗号化のフラグ ([対象範囲](#scope))。
  - リンク・参照 (DLL が報告するリダイレクトの種類が 0 以外。symlink、junction、hardlink、ファイルの参照コピー。`ENTRY_REDIRECTION`)。
  - [種別と属性](#types)の違反。
  - [上限](#limits)の超過 (辞書サイズを含む)。
  - ハッシュ (CRC-32 または BLAKE2) を持たないファイルエントリ (`ENTRY_WITHOUT_HASH`)。内容検証の補助検査を常に行えるようにするためで、内容を読まない Fast にも適用する。
- 続けて、ZIP と共通の[名前](#names)・[構造](zip.md#structure)・[上限](#limits)の検査を全エントリに行う。`--entries` の有無、操作、モードに関係なく同じである。

<a id="names"></a>
## 名前

- **エントリの名前 (FullName) は、DLL が返す Windows 用に変換した後の名前とする。** 変換前の名前は DLL の公開 API で得られず、unextract は使わない。区切りは `\` である。
  - 観測した変換: 区切りの `/` は `\` になる。Unix で作られた名前に含まれる `\` と `:` は `_` になる。これは同じ版の WinRAR (UnRAR) が Windows で展開するときの名前と一致する ([理由](../RATIONALE.md#rar-names))。ほかの文字の変換の有無は未確認である ([OPEN_ISSUES](../OPEN_ISSUES.md#rar-observations))。
  - ディレクトリエントリの FullName は、DLL の名前の末尾に `\` を1個付けたものとする (ZIP のディレクトリエントリと同じく、名前が区切りで終わる)。ファイルエントリの DLL の名前が区切りで終わる場合は FATAL (`FILE_ENTRY_NAME_ENDS_WITH_SEPARATOR`)。
- **RAR4 の Unicode でない名前**は、DLL が実行環境の設定に従って復号した結果をそのまま使う。同じアーカイブでも実行環境によって名前が変わり得ることを許容する。別の環境で作った `--entries` は一致せず入力エラーになり得る (削除0件)。
- 変換後の FullName に、ZIP と同じ検査を適用する: [パスの検査](zip.md#paths) (危険なパス、Windows で使えない文字・名前、予約名、末尾のドット・空白、名前長と深さ)、U+FFFD を含む名前の拒否 ([名前の復号](zip.md#decoding)の2項目め)、[内部構造](zip.md#structure)の重複・大小文字の衝突・ファイルとディレクトリの衝突。変換前に異なっていた2つの名前が変換後に同じ (または大小文字だけ違う) 名前になれば、重複・衝突として FATAL になる。
- FullName の用途は ZIP と同じで、区切りで分割した成分を target 側の解決に、FullName を表示・`--entries` の照合・機械可読出力の `name` に使う。ZIP の[名前の復号](zip.md#decoding)の CP437 の規定は RAR に適用しない。

<a id="types"></a>
## 種別と属性

DLL が報告するディレクトリのフラグ、作成元 OS (Windows または Unix)、属性の生の値で判定する。判定できないエントリを通常ファイルとみなさない。

- ディレクトリエントリは、DLL のディレクトリのフラグを持つもの。宣言展開サイズが 0 でないディレクトリエントリは FATAL (`DIRECTORY_ENTRY_WITH_DATA`)。
- 作成元 OS が Windows のエントリ: 属性を DOS 属性として、ZIP の[特殊エントリ](zip.md#types)の DOS 属性の規則と同じに扱う (ファイルエントリに `0x10` があれば `DOS_DIRECTORY_ATTRIBUTE_ON_FILE_ENTRY`、どちらのエントリでも `0x400` があれば `DOS_REPARSE_POINT_ATTRIBUTE` の FATAL。ほかの属性は unextract が復元しないため無視する)。
- 作成元 OS が Unix のエントリ: 属性を Unix の mode として、種別 (`mode & 0xF000`) がファイルエントリでは `0x8000`、ディレクトリエントリでは `0x4000` の場合だけを許す。ファイルエントリの `0x4000` は `FILE_ENTRY_WITH_DIRECTORY_TYPE`、ディレクトリエントリの `0x8000` は `DIRECTORY_ENTRY_WITH_FILE_TYPE`、0 を含むそれ以外は `UNSUPPORTED_ENTRY_TYPE` の FATAL (ZIP と異なり 0 を許さない)。
- DLL が Windows・Unix 以外の作成元 OS を報告した場合は FATAL (`UNSUPPORTED_HOST_OS`)。
- リダイレクト (リンク・参照) は [列挙](#listing) のとおり FATAL。

<a id="limits"></a>
## 上限

- [ZIPのリソース制限](zip.md#limits)の表と規定を、次の読み替えで適用する: エントリ総数は列挙したエントリ数、名前長とメタデータ総量は[名前](#names)の FullName (ディレクトリの末尾の `\` を含む)、宣言展開量は DLL が報告する展開サイズ (64 ビット)、実測展開量は[内容検証](#verification)で DLL から受け取ったバイト数。宣言側の上限は全エントリ (`MISSING` と `--entries` の選択外を含む) に、両モードで適用する。
- 展開サイズが不明と記録されたエントリは、DLL が非常に大きな値として報告し、1エントリの宣言展開量の上限で FATAL になる (観測)。
- **辞書サイズ**: ファイルエントリの辞書サイズが 1 GiB (1,073,741,824 バイト) を超える場合は Prepare の FATAL (`ENTRY_DICTIONARY_TOO_LARGE`、削除0件) とする。この値は暫定である。内容を読まない Fast と、内容比較候補でないエントリにも適用する (受理範囲を操作・モード・target から独立に決めるため。[理由](../RATIONALE.md#rar))。
- **列挙中の上限**: エントリ総数・名前長・メタデータ総量は、列挙中に各エントリを保持する前に検査し、超過を検出した時点で列挙をやめて [Prepare](../SPEC.md#prepare) の手順2の FATAL (`TOO_MANY_ENTRIES`・`NAME_TOO_LONG`・`METADATA_TOO_LARGE`、削除0件) とする。超過を検出したエントリを原因エントリとする。総件数を得るために列挙を続けない。
  - 手順2の FATAL なので、entries・target の入力エラーより先に報告され、それより前のエントリに手順6で検出される違反があってもこの FATAL を報告する。`analyze` はほかの手順2の FATAL と同じく、ヘッダー・見出し・件数を表示しない ([analyzeの表示](cli.md#analyze-output))。
  - ほかの上限 (パス深さ、宣言展開量、辞書サイズ) は手順6で検査する。ZIP はすべての上限を従来どおり手順6で検査し、検査の時点・優先順位・表示は変わらない ([ZIP上限](zip.md#limits)。ZIP の Central Directory の事前割当の限界は RAR には当てはまらない)。

<a id="content"></a>
## 内容の読み取りと検証

<a id="session"></a>
### 読む範囲と順序

- 内容を読む範囲は ZIP と同じで、Strict の内容比較候補だけである ([読む範囲](zip.md#read-scope))。それ以外のエントリは `RAR_SKIP` で飛ばす。非 Solid の RAR では `RAR_SKIP` はデータを展開しない (観測)。そのため、`MISSING`・サイズ不一致の `MODIFIED`・`SKIPPED_SPECIAL_FILE` のエントリのデータだけが壊れていても FATAL・STOP にならない。
- Strict では、Prepare の最後 (手順10) に DLL でアーカイブを内容読み取り用に開き、実行終了まで保持する。**Prepare で行うのはこの open (読み取りセッションの準備) だけ**で、ヘッダーの読み取り・`RAR_TEST`・`RAR_SKIP`・データの受け取りは行わない。open の失敗は Prepare の FATAL (`ARCHIVE_OPEN_FAILED`、削除0件)。
- 内容の読み取り・展開・全バイト検証は、エントリの処理の段階 ([エントリの処理](../SPEC.md#execution)) でだけ行う。DLL はアーカイブの順にしか進めないため、エントリの処理もアーカイブの順に1件ずつ進む。
- **セッションの前進**: 処理するエントリ (`analyze` では各エントリ、`delete` では処理対象の各ファイルエントリ。以下、前進先) の処理の最初 (target の解決より前) に、このセッションを前進先のヘッダーまで進める。前進では、アーカイブの順に次を行う。
  1. 直前に読んだヘッダーのエントリで `RAR_TEST` を行っていなければ、`RAR_SKIP` する (内容比較候補でなかったエントリは、ここで `RAR_SKIP` される)。
  2. 前進先より前のまだ読んでいないエントリ (`delete` の処理対象外のエントリとディレクトリエントリ) を、1件ずつヘッダーを読んで照合し、`RAR_SKIP` で通過する。
  3. 前進先のヘッダーを読んで照合する。前進先が内容比較候補なら、その後 `RAR_TEST` で内容を検証する ([内容検証](#verification))。
- 照合: 読んだ各ヘッダーは、Prepare の列挙と同じでなければならない (名前、ディレクトリのフラグ、展開サイズ、作成元 OS、属性、フラグ、ハッシュの種類と値、辞書サイズ、リダイレクトの種類)。エントリの対応が崩れたまま処理しないためである。最後の前進先より後ろのヘッダーは読まない。
- **前進の失敗**: 次のいずれかが前進中に起きた場合は、**前進先の** `analyze` の FATAL、`delete` の STOP とする。失敗を検出した位置が通過中のエントリ (処理対象外・ディレクトリ) や直前のエントリであっても、それらを FATAL・STOP の原因エントリにしない。
  - ヘッダーが Prepare の列挙と異なる、またはヘッダーが足りない (終端に達した): `ARCHIVE_CHANGED`。
  - ヘッダーの読み取りが成功・終端以外を返した、または `RAR_SKIP` が成功以外を返した: `ARCHIVE_UNREADABLE`。
- 前進の失敗は前進先の target の解決より前に起きるため、前進先の target には触れていない (`delete` では削除用ハンドルを開いておらず、削除された可能性は無い。`possibly_deleted` は false、`step` は付けない)。機械可読出力の `entry_index`・`entry_name` と人間向けの原因エントリは前進先とし、`delete` では前進先を `STOPPED` として出す。原因の説明 (人間向けの <理由>、機械可読出力の `message`) には、**実際に失敗を検出した位置** (エントリ番号 `#n` と名前) を含める。名前は原因の表示と同じくエスケープする ([表示](cli.md#output))。
- 前進の失敗より前に確定した結果 (出力済みの結果行・`entry`、削除) は変わらない。前進先は `analyze` では FATAL の原因エントリ、`delete` では `STOPPED` で、それより後ろの処理対象は `analyze` では未判定、`delete` では未処理である。前進中に通過するエントリは `delete` で次のとおり数える。
  - 処理対象外のエントリは、失敗の有無にかかわらず処理対象外 (選択で決まる)。
  - ディレクトリエントリは、ヘッダーの照合が成功して分類が確定したものだけを `DIRECTORY` とする。照合が成功した後の `RAR_SKIP` の失敗では、そのディレクトリは分類済み (`DIRECTORY`) である。照合に失敗したディレクトリと、失敗により照合に至らなかったディレクトリは未処理とする ([要約](cli.md#delete-output) の未処理)。
- `delete` で STOP せず到達した末尾の処理対象ディレクトリは、Prepare で確認済みの情報に基づき、実行段階で再照合せず `DIRECTORY` に計上する。処理対象ファイルがなく、ディレクトリのみを処理する場合も同じで、追加の前進・ヘッダー読み取りは行わない。途中 STOP で照合に失敗した、または照合に至らなかった処理対象ディレクトリと、STOP より後ろの処理対象は、上記のとおり未処理とする。選択外のディレクトリは常に処理対象外とする。
- Fast は内容を読まないため、内容読み取り用に開かない (セッションの前進も無い)。

<a id="verification"></a>
### 内容検証の基準

ZIP の[内容検証の6基準](zip.md#verification)を、DLL がコールバックで渡すデータに対して次のとおり適用する。違反時の扱い (`analyze` の FATAL・`MODIFIED`・`MATCHED`、`delete` の STOP・`MODIFIED`・最終確認への進行) は ZIP と同じである。

1. 暗号化: 暗号化されたエントリは Prepare で拒否済み ([対象範囲](#scope))。読み取り中にパスワードを要求されたら拒否し、2 の違反とする。
2. 読み取りの失敗: そのエントリの `RAR_TEST` が成功以外を返した場合 (データの破損、DLL によるハッシュの不一致、未対応の方式、中止を含む) は異常とする。以後そのアーカイブの読み取りは続けない (`analyze` は FATAL、`delete` は STOP。`CONTENT_READ_FAILED`)。
3. 受け取ったバイト数が宣言展開サイズを**超えた時点で直ちに**異常とし (`CONTENT_TOO_LONG`)、コールバックで中止する。NTFS ストリームなど本体に続くデータが同じコールバックに届いた場合もこれに当たる ([限界](#limitations))。
4. `RAR_TEST` の終了までに受け取ったバイト数が宣言展開サイズと等しい (不足は `CONTENT_TOO_SHORT`)。
5. ハッシュ: ハッシュの種類が CRC-32 なら、受け取った全バイトの CRC-32 を unextract が計算し、ヘッダーの値と照合する (不一致は `CONTENT_CRC_MISMATCH`。ZIP の [CRC-32](zip.md#crc) と同じ位置づけ)。BLAKE2 の照合は DLL に委ね、DLL が不一致を返せば 2 の違反とする。
6. 固定サイズのバッファで target と比較し、全バイトが一致する。

- **判定は `RAR_TEST` が成功を返した後で確定する。** DLL のハッシュの照合は全データを渡し終えた後に結果が出るため、全バイトが一致していても、それより前に `MATCHED` とせず、最終確認 ([削除順序](filesystem.md#delete-flow)の手順7) へ進まない。
- 一致・不一致にかかわらず、3 で中止する場合を除き DLL のデータを終端まで受け取る (ZIP の「終端まで読み切る」と同じ)。target 側は不一致の確定後に読み続けなくてよい。
- 削除の根拠は ZIP と同じく全バイト一致であり ([対象](../SPEC.md#scope))、CRC-32・BLAKE2 は補助検査である。敵対的に作られた RAR への防御ではない。

<a id="fast"></a>
## Fast

Fast では内容を読まず、DLL は Prepare の列挙だけに使う。宣言側の検査 ([列挙](#listing)・[名前](#names)・[種別](#types)・[上限](#limits)) は Strict と共通である。[モード契約](../SPEC.md#modes)の非保証はそのまま適用され、ZIP と比べて次の差がある。いずれもファイル安全性 ([FS](filesystem.md)) は共通である。

| 状況 | ZIP の Fast | RAR の Fast |
|---|---|---|
| 暗号化されたエントリ | `SAME_SIZE` になり得る | アーカイブ全体を Prepare で拒否 |
| 辞書サイズの上限超過 | 該当なし | Prepare で拒否 |
| 末尾の切り詰め | 多くは開けずに FATAL | 列挙できた範囲を処理する。データの途中で切れたエントリも `SAME_SIZE` になり得る |
| NTFS ストリームを含むエントリ | 該当なし | 内容を読まないため影響しない |

<a id="limitations"></a>
## 既知の限界

- **切り詰めを検出できない**: 切り詰められた RAR は、エントリの少ない正常な RAR に見えることがある。全件 `MATCHED` でも RAR が完全とは限らない。列挙されなかったエントリは処理されず、削除もされない (`--entries` で指定すれば一致しない行として入力エラー)。データの途中で切れたエントリは、Strict では内容検証の FATAL・STOP になり、Fast では `SAME_SIZE` として削除され得る。
- **NTFS ストリームを保存した RAR**: 本体に続くストリームのデータが同じ経路で届くため、Strict ではそのエントリが内容検証の 3 で FATAL・STOP になる。Prepare では事前に分からない。target がストリーム (ADS) を持てば内容を読む前に `SKIPPED_SPECIAL_FILE` になる ([特殊ファイル](filesystem.md#special-files)) ため、起きるのはストリームが復元されていない target が内容比較候補になった場合である。
- **プロセス内のネイティブコード**: 信頼できない入力を、削除用ハンドルを扱うのと同じプロセス内の DLL (ネイティブコード) が解析する。DLL のメモリ安全性の不具合は、そのプロセスの判定に影響し得る。MVP ではこのリスクを受け入れ、版の固定、`RAR_TEST`・`RAR_SKIP` 以外を使わないこと、コールバックの拒否で範囲を狭める。別プロセスやサンドボックスによる隔離は行わない ([理由](../RATIONALE.md#rar-in-process))。
- **名前**: 変換後の名前は変換前の名前に戻せない。WinRAR 以外の展開ツール (7-Zip など) で展開した名前とは一致しないことがあり、その場合は `MISSING` (非削除) になる。RAR4 の Unicode でない名前は実行環境に依存する。
- **健全性**: unextract は RAR の完全な健全性を証明しない。内容を読まないエントリの破損を検出しない。BLAKE2 の照合は DLL の実装に依存する。
