## 1. Executive summary

- Phase 1開始前に、**子要素を保持ハンドルに結び付けて開く方式**と、ディレクトリ削除候補の同一性・寿命を決める必要があります。
- **ごみ箱への要求・事前拒否・事後検出を組み合わせても、完全削除の事前防止保証にはなっていません。** SPEC §10.1との整合を先に決める必要があります。
- **SPEC起因:** NTFSの名前付きデータストリームを「内容の同一性」に含めるかが未定義です。
- Phase 1では、ZIP構造検査、Deflateの正常終了判定、破損時の全体中止方針、メタデータの資源上限を確定する必要があります。
- Phase 2までに、ディレクトリ保持・case sensitivity・クラウド検出のWindows上の保証を再検証する必要があります。
- Phase 3までに、ごみ箱無効のfixed drive、容量超過、Shellの追加対象・中断・結果不明を含む確認が必要です。
- 最大のリスクは、**観測できた正常系の挙動を、対象固定・完全削除防止という一般的な安全保証へ拡張している点**です。

レビュー対象は指定されたSPEC・PLANと一次資料のみです。シェル実行、実機スパイク、ファイル変更・保存は行っていません。以下の「問題なし」は設計記述に対する評価であり、実装の検証済みを意味しません。

## 2. Critical

### C-1 — ハンドル保持だけでは「構造的な包含」の実装が定義されていない

- **重大度:** Critical
- **該当節:** SPEC §7.2–7.3／PLAN §2 B-2・B-7、§4.4–4.5、§6 R-3
- **区分:** `[Safety]`
- **根拠の種類:** `[一次資料で確認]`、`[PLANの記述から論理的に導出]`、一部 `[推測・要実機確認]`

**問題**

`ITargetRoot.OpenFile(components, …)`のコメントは「順にno-followで開いて保持」としていますが、**次の子をどのAPIで、何を起点として開くか**がありません。

`CreateFileW`で組み立てたフルパスを毎回開く場合、保持した親ハンドルはその名前解決の起点にはなりません。「開いた後の再検査では文字列パスを使わない」としても、**次のopenで経路を再解決する問題**は残ります。

一次資料で確認できる共有モードの契約は、対象ファイル／ディレクトリへのアクセス共有です。PLANの「子ディレクトリの保持で親のrenameも失敗した」という観測から、全祖先・全ファイルシステム・全経路変更に対する固定保証までは導けません。[Microsoft Learn「CreateFileW」― Parameters / dwShareMode](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)

特に不足するのは次の条件です。

- Q-4／Q-13で許可する、targetより上のjunctionを含む経路。
- targetの取得後、確認待ち中、子のopen直前の祖先変更。
- 空になった中間ディレクトリへのreparse設定。
- case-sensitiveな親の下にある`Foo`と`foo`。

R-3の「非空ならjunction化できない」は一次資料と整合します。しかし、**全検査時点で中間ディレクトリが非空であり続ける保証はありません。** 保持中・共有RWでのreparse変更可否は今回確認していません。[Microsoft Learn「Reparse points」― Reparse point restrictions](https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-points)

**起こりうる結果**

経路が再解決される実装では、保持したtargetとは別の場所を開く可能性を排除できません。開いた後にIDや最終パスの違いを検出しても、SPECの「target外へアクセスしない」は回復できません。

**修正案**

Phase 1開始前に、`ITargetRoot`の契約を次のいずれかとして具体化してください。

1. 保持した親ハンドル相対で**単一成分ずつ**開き、各成分でreparseを拒否する。
2. パスベース方式を採るなら、使用経路の全成分が固定される条件と、それが成立しない場合の拒否条件を明記する。

`NtCreateFile`の`ObjectAttributes.RootDirectory`には、ディレクトリハンドル相対の名前指定が文書化されています。ただし、その採用だけで全問題が解決するとはせず、case sensitivity、reparse、アクセス権、寿命を含めて設計する必要があります。[Microsoft Learn「NtCreateFile」― Parameters / ObjectAttributes、Remarks](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntcreatefile)

**SPEC起因:** §7.2の一律な大文字小文字無視も修正対象です。重複判定で多めに拒否することと、包含判定で別ディレクトリを同一視することは別です。

---

### C-2 — 完全削除の事後検出を、SPECの事前防止要件の代わりにしている

- **重大度:** Critical
- **該当節:** SPEC §10.1・§16／PLAN §2 A-2、§3 M-1–M-5、§4.5、§5 Q-7・Q-8、§6 R-4・README案
- **区分:** `[Safety]`
- **根拠の種類:** `[一次資料で確認]`、`[PLANの記述から論理的に導出]`

**問題**

一次資料から確認できた内容は次のとおりです。

| 要素 | 確認できる契約 | 確認できない保証 |
|---|---|---|
| `TSF_DELETE_RECYCLE_IF_POSSIBLE` | 可能ならごみ箱へ送る指定 | ごみ箱利用が既に確定したという証明 |
| `FOF_ALLOWUNDO` | 可能ならundo情報を保持 | 完全削除禁止 |
| `FOFX_RECYCLEONDELETE` | 完全削除ではなくごみ箱送りを要求 | 今回確認した記述だけによる、全失敗条件での振る舞い |
| `FOF_WANTNUKEWARNING` | 完全削除に対する警告 | 無人CLIでの強制拒否 |
| `PreDeleteItem`のエラー返却 | 当該削除と後続操作を中止 | 成功を返した後にfallbackしない保証 |
| `PostDeleteItem`のNULL | 完全削除された場合の結果情報 | 削除の取り消し |

出典: Microsoft Learn「[_TRANSFER_SOURCE_FLAGS ― Constants](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/ne-shobjidl_core-_transfer_source_flags)」、「[IFileOperation::SetOperationFlags ― 各フラグの見出し](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags)」、「[PreDeleteItem ― Return value](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-predeleteitem)」、「[PostDeleteItem ― Parameters / psiNewlyCreated](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-postdeleteitem)」。

PLANはA-2でこの限界を認識していますが、§4.5では不確実なまま削除を実行します。**「最悪1件で停止」は被害の制限であり、SPECの「完全削除は明示指定時のみ」を満たしません。**

`DRIVE_FIXED`もごみ箱の利用可能性を表す値ではありません。[Microsoft Learn「GetDriveTypeW」― Return value](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdrivetypew)

**起こりうる結果**

ごみ箱を無効にしたfixed driveなどで、ユーザーが`--delete-permanently`を指定していないのに、最初のファイルが完全削除される可能性が残ります。これは攻撃を必要としない通常の設定・環境の問題です。

README案の「その時点で中止」だけでは、**既に1件が復元不能となりうる**ことが十分明瞭ではありません。「ファイルはごみ箱から復元できます」も、fallbackと対象差し替えが重なった場合まで適用できません。

**修正案**

- Phase 1開始前に、事後検出をSPEC適合の代替にしないと明記する。
- 防止根拠が得られない環境は、削除開始前に拒否する設計とする。
- この制約を緩めるなら、PLANだけで変更せずSPECの明示的な判断事項にする。
- M-1–M-5に**ごみ箱を無効化したfixed drive**を追加する。
- `SHQueryRecycleBin`による補強を確定策として扱わない。同APIの結果は現在のサイズ・件数であり、最大許容量や次の削除の成功保証ではありません。[Microsoft Learn「SHQUERYRBINFO」― Members](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-shqueryrbinfo)
- READMEに「事後検出時には既に完全削除されている可能性があり、復元を保証できない」と明記する。

---

### C-3 — ファイル削除後に、別の空ディレクトリを削除できる設計になっている

- **重大度:** Critical
- **該当節:** SPEC §10.2／PLAN §4.4、§4.5 手順3・5・6、§7 P3-5
- **区分:** `[Safety]`
- **根拠の種類:** `[PLANの記述から論理的に導出]`

**問題**

ファイル処理中の祖先ハンドルは保持しますが、処理後は閉じます。その後、祖先ディレクトリを名前から開き直して削除する際に、**そのディレクトリが実際に削除したファイルの祖先だった個体か**を照合する手順がありません。

例えば、`target\d\a.txt`を削除した後、別プロセスが空になった`d`を移動し、同名の別の空ディレクトリを置けば、手順6は新しい`d`を削除できます。これはtarget外問題とは独立した、target内の誤削除です。

`IOpenedDirectory.Identity`はありますが、比較相手となる削除候補のIdentityをいつ記録・保持するかが未定義です。

**起こりうる結果**

「今回削除したファイルの祖先」に当たらないディレクトリを削除し、SPEC §10.2に違反します。

**修正案**

- 祖先候補に相対パスだけでなく、削除時に確認したディレクトリIdentityを持たせる。
- 厳密な同一個体保証が必要な区間では、ハンドル保持を含む寿命管理を決める。
- 開き直した個体が違う、確認できない、reparseになった場合は保持する。
- 深い順の処理は妥当。ただし、子の削除マーク設定後にハンドルを閉じ、実際の名前空間からの除去を確認してから親へ進む順序を定義する。

削除マークと即時消滅は同義ではありません。[Microsoft Learn「FILE_DISPOSITION_INFORMATION_EX」― Remarks](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex)

---

### C-4 — **SPEC起因:** 比較しない名前付きストリームを含むファイル全体を削除しうる

- **重大度:** Critical
- **該当節:** SPEC §1–2・§6・§7.1・§10／PLAN §4.4–4.5
- **区分:** `[Safety]`
- **根拠の種類:** `[一次資料で確認]`、`[PLANの記述から論理的に導出]`

**問題**

SPECはZIP内のコロンを拒否しますが、**target側に既に存在するNTFSの名前付きデータストリーム**を扱っていません。

通常のファイル読み取りが比較する無名ストリームと、ファイル全体は同義ではありません。無名ストリームがZIPと一致しても、別ストリームにユーザーやアプリが追加した情報が存在しえます。共有モードもストリーム単位です。[Microsoft Learn「File streams」― 本文、Naming Conventions for Streams](https://learn.microsoft.com/en-us/windows/win32/fileio/file-streams)

**起こりうる結果**

未比較の追加データを持つファイルがMATCHEDとなり、完全削除モードではその追加データも失われます。「全バイト一致」「他者はファイルを書き換えられない」という説明も、対象ストリームの限定が必要です。

**修正案**

Phase 1開始前に、内容同一性の範囲を決めてください。安全側のMVP案は、**無名ストリーム以外のデータストリームがある場合はスキップ**することです。`Zone.Identifier`などの例外を設けるなら、その情報を削除してよいという別の仕様判断になります。

同一ハンドルでのストリーム情報取得を抽象に含めることも検討してください。[Microsoft Learn「GetFileInformationByHandleEx」― Remarks / FileStreamInfo](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex)

これはPLANがSPECに反したという指摘ではなく、SPECの安全目標と比較対象の定義の不足です。

## 3. High

### H-1 — 自前ZIP parserの受理条件と範囲検査が不足している

- **重大度:** High
- **該当節:** SPEC §6・§12／PLAN §4.2–4.3、§7 P1-2・P1-3、§8、§9
- **区分:** `[Robustness]` `[Correctness]`
- **根拠の種類:** `[一次資料で確認]`、`[PLANの記述から論理的に導出]`

**問題**

`[offset, offset+CompressedSize)`という記載だけでは、その範囲が安全に計算され、正しいエントリに属することを保証できません。

以下をPhase 1の受理条件・拒否条件として追加する必要があります。

| 構造 | 不足する検査・方針 |
|---|---|
| EOCD | comment長と終端位置、偽シグネチャ、複数候補、宣言件数と実件数の一致 |
| Zip64 EOCD／locator | locatorの位置・参照先・レコード長、通常EOCDとの整合、単一ディスク制約 |
| Central Directory | 宣言領域内での完全なレコード読み取り、件数詐称、切断、余剰領域の扱い |
| Local Header | 固定部・名前・extraの範囲、CRC／サイズの整合をbit3に応じて判定 |
| extra field | TLVの途中切れ、長さ超過、Zip64必須値の欠落・重複・順序・矛盾 |
| compressed data | `ulong`→`long`変換、加算overflow、ファイル終端超過、短い読み取り |
| 領域関係 | header／CD／data／descriptorの交差、entry間のdata共有・overlap |
| Data Descriptor | bit3時の存在、署名あり／なし、32／64bit、CRCが署名値と同じ場合の曖昧性 |
| フラグ・方式 | bit0・6だけでなく、未対応の意味を持つフラグ、version-needed、暗号化CDの拒否 |
| 外側の形式 | SFX、trailing data、split／multi-diskの対応範囲を明示 |

Data DescriptorやZip64の条件は、[PKWARE「APPNOTE.TXT」§4.3.7–4.3.16、§4.4.4、§4.5.1・§4.5.3](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT)で確認しました。**overlapを一律拒否することは、ここで提案する安全側の受理方針であり、APPNOTEの全形式に対する禁止規則として述べているわけではありません。**

**起こりうる結果**

範囲外読み取り、別領域の誤展開、壊れたZIPの受理、過大割り当てが起こりえます。全バイト比較があるため、これらを直ちに任意ファイル削除へ結び付ける評価はしません。

**修正案**

- ファイル長を基準に、`size <= fileLength - offset`などoverflowしない形で検査する。
- 構造検査を通過した内部レコードだけが`OpenData`を呼べるようにする。
- 有界ストリームの同期／非同期Read・Seek・早期EOFを同じ規則で制限する。
- Data Descriptorはシグネチャ走査で探さず、検査済み圧縮範囲の終端を起点に解析する。
- 正常系・異常系の具体的fixtureをP1-3の完了条件にする。

**属性の補足:** C-5／Q-10の「下位バイトの`0x400`」は誤りです。`0x400`は1バイトに入りません。またexternal attributesは作成OS依存です。made-byを保存するなら実際の解釈にも使用し、未知形式は安全側に扱ってください。[APPNOTE.TXT §4.4.2・§4.4.15](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT)

**自前化の評価:** 公開`ZipArchive`経由で宣言サイズ超過を観測できないという動機は公式ソースと整合します。しかし現PLANの「数百行で足りる」という見積りから、増える攻撃面を上回る利益はまだ示されていません。採用は上記受理範囲・テスト・保守費用を具体化して判断すべきです。

---

### H-2 — 公開DeflateStreamのEOFは、正常なDeflate終端の保証にならない

- **重大度:** High
- **該当節:** SPEC §6／PLAN §2 C-7、§4.2、§5 X-2
- **区分:** `[Correctness]` `[Robustness]`
- **根拠の種類:** `[一次資料で確認]`

**問題**

確認したdotnet/runtime `release/10.0`の`DeflateStream.ReadCore`には、入力が尽きた際、厳格検証が無効なら正常終端に達していなくても読み取りを終了する経路があります。`s_useStrictValidation`は既定falseです。[公式ソース「DeflateStream.cs」― ReadCore、s_useStrictValidation](https://raw.githubusercontent.com/dotnet/runtime/release/10.0/src/libraries/System.IO.Compression/src/System/IO/Compression/DeflateZLib/DeflateStream.cs)

したがって、**出力サイズ・CRC・バイト比較がすべて一致しても、Deflateとしての終端が欠けた入力を拒否できるとは限りません。** 出力を生む部分が残り、終端部分が切れたケースが該当します。

また、展開器の先読みがあるので、有界ストリームの読み取り位置だけで「圧縮データを意味的にちょうど消費した」と判断するのも不十分です。

**起こりうる結果**

SPECが要求する「展開が正常終了した場合のみMATCHED」を満たせず、壊れたZIPから削除判断を行います。

**修正案**

- 使用する正確なランタイム版で、正常終端確認の方法を決める。
- strict validationを採用するなら起動前設定・対象Read経路・例外を固定して検証する。
- 空入力、終端欠落、途中切断、正常Deflateの後ろの余剰データを別々にテストする。
- 出力上限の確認には、上限到達時点で終了せず、追加出力の有無を確認する経路を必須とする。

なお、`ZipArchiveEntry.GetDataDecompressor`が内部コンストラクタへ宣言サイズを渡し、`Inflater.InflateVerified`がそのサイズで終了する点は確認できました。PLANの自前化動機には根拠がありますが、それで上記問題まで解決するわけではありません。[ZipArchiveEntry.cs ― GetDataDecompressor](https://raw.githubusercontent.com/dotnet/runtime/release/10.0/src/libraries/System.IO.Compression/src/System/IO/Compression/ZipArchiveEntry.cs)、[Inflater.cs ― InflateVerified](https://raw.githubusercontent.com/dotnet/runtime/release/10.0/src/libraries/System.IO.Compression/src/System/IO/Compression/DeflateZLib/Inflater.cs)

ここで確認したのは公式ブランチのソースであり、PLAN記載の実行バイナリを検証した事実ではありません。

---

### H-3 — **SPEC起因を含む:** 破損ZIPの全体中止と、エントリ単位の早期終了が整合していない

- **重大度:** High
- **該当節:** SPEC §5–6・§12・§14・§17-7／PLAN §4.2・§4.5、§8-7
- **区分:** `[Correctness]`
- **根拠の種類:** `[PLANの記述から論理的に導出]`

**問題**

SPEC §12は「ZIP自体が破損なら何も削除しない」と要求します。一方、§6はサイズ不一致・バイト不一致で早期終了し、他のエントリを処理できます。

PLANにも、次を区別する規則がありません。

- 構造破損を発見したらアーカイブ全体を中止する。
- CRC・展開異常は当該エントリだけERRORにする。
- MISSING／MODIFIEDなどで展開しなかったエントリの破損をどう扱うか。

**起こりうる結果**

先頭が正常なMATCHED、後続がCRC不整合のZIPで、正常エントリが削除されるかどうかが実装者の解釈に依存します。遅延解析したLocal Headerの破損を削除開始後に発見すれば、「何も削除しない」を満たせません。

**修正案**

Phase 1中に、**アーカイブ全体を不適格にする異常**と**エントリ局所の不適格**を定義し、削除許可の全体ゲートを作ってください。

全エントリの完全な整合性を削除前に要求するなら、MISSING等も展開する費用とリソース制限との関係をSPECで決める必要があります。現状の文言のまま、両方を満たすと扱ってはいけません。

---

### H-4 — 件数と展開サイズだけでは、100,000件の資源使用を制限できない

- **重大度:** High
- **該当節:** SPEC §6.1／PLAN §2 D、§4.2・§4.5、§8-9・10
- **区分:** `[Robustness]`
- **根拠の種類:** `[PLANの記述から論理的に導出]`

**問題**

100,000件の短い名前で得た「約35 MB」は、生の名前・デコード名・成分列・祖先候補を持つ製品設計の上限にはなりません。

- 長い名前を100,000件保持すると、生バイトだけでGiB単位になります。
- §2 DにはCDサイズの事前拒否がありますが、§4.2の`Limits`には値も規則もありません。
- 深さ、成分数、名前の総量、祖先候補総数に上限がありません。
- 16 GiBは出力量の上限であり、処理時間の上限ではありません。
- 多数のエントリを分類・削除時に二度展開する総時間と、1件ごとのShell呼び出し時間が未評価です。

**起こりうる結果**

有効な件数上限内でもメモリ不足・長時間停止が起きます。深い経路では1件処理中のハンドル数も増えます。

**修正案**

Phase 1で、メタデータ総量、名前長／深さ、実解析件数、CDサイズに上限を設けてください。総処理量・キャンセル方針も明示します。

処理は原則1件ずつとし、ハンドル数を件数ではなく深さに比例させる設計は妥当です。ただしC-3の祖先保持と両立する上限・処理順序が必要です。

---

### H-5 — Phase 1の抽象には、dry-runと安全な削除を表す契約が不足している

- **重大度:** High
- **該当節:** SPEC §11・§18／PLAN §4.2・§4.4–4.5、§7 P1-6・P1-8
- **区分:** `[Correctness]` `[Safety]`
- **根拠の種類:** `[PLANの記述から論理的に導出]`

**問題**

| 抽象 | 不足する契約 |
|---|---|
| `IFileSystemProbe`／`ITargetRoot` | C-1のopen起点、targetハンドルの全処理中の寿命、祖先変更時の扱い |
| `IOpenedFile` | 属性・種別・リンク数等を再検証する契約。`RefreshAndCompare`はIDと長さだけに見える |
| `IOpenedDirectory` | **削除予定ファイルを除いた空判定**、列挙失敗の扱い、削除用途の権限・共有モード |
| `IDeleter` | 保持ハンドルとの関連が保証されたShell対象、結果不明、中断、削除完了と削除マークの区別 |
| `ZipEntryRecord`／`IZipSource` | 構造検証済みであること、アーカイブ所属、正常終了確認、全体不適格状態 |

特に`IsEmpty()`だけでは、「現在は非空だが、予定ファイルを取り除けば空になる」ディレクトリのdry-run表示を作れません。Core側に直接`Directory.Enumerate…`を追加すると、抽象化とno-followの保証を迂回します。

**起こりうる結果**

Phase 2–3でインターフェース変更、Windows型への依存、パスによる再openが追加され、安全設計が後付けになります。

**修正案**

Phase 1の抽象確定前に、次を契約化してください。

- ハンドル基準の非再帰列挙、または除外予定集合を受け取る削除後空判定。
- 検証済み対象と削除対象を結び付ける内部能力。
- 明示的な検査失敗・結果不明。
- 同じハンドルをDeleterが扱うためのWindows内部契約。公開APIに生ハンドルを露出する必要はありません。
- ZIP・targetを確認プロンプト中も保持する寿命。

**SPEC起因:** §11の削除予定ディレクトリ表示には、候補ディレクトリ内の無関係な子の存在確認が必要です。§15の「無関係ファイルの走査をしない」は、無関係ファイルの内容走査と、この必要な列挙を区別すべきです。

---

### H-6 — クラウド検出失敗時の続行と、READMEの保証が一致していない

- **重大度:** High
- **該当節:** SPEC §7.3・§11／PLAN §2 B-3、§4.5、§6 R-5・README案、§7 P2-4
- **区分:** `[Correctness]` `[Safety]`
- **根拠の種類:** `[一次資料で確認]`、`[PLANの記述から論理的に導出]`

**問題**

P2-4は公開モード設定失敗時の「ERRORか続行か」を未決定にしていますが、README案は「同期状態にかかわらず削除対象にならない」と断定しています。

公開モードの文書は、placeholderのreparse等の特徴を可視化することを説明します。OneDrive配下の全ファイル・全同期状態を必ず検出できるという証明ではありません。[Microsoft Learn「RtlSetProcessPlaceholderCompatibilityMode」― Return value、Remarks](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-rtlsetprocessplaceholdercompatibilitymode)

R-5の「漏れてもバイト一致なので誤削除ではない」も不適切です。スキップ対象と定めたファイルの削除や、dry-runでのハイドレーションは、内容一致では正当化されません。

**起こりうる結果**

検出失敗時にクラウド対象を通常ファイルとして読み、ダウンロードや削除を行う可能性を残します。

**修正案**

- 公開モードは最初のtarget／ZIPアクセス前に設定する。
- 必須の検出能力を確立できなければ処理を中止する。
- M-7の結果が出るまでREADMEを保証表現にしない。
- 「オンライン専用・ローカル・常に保持」に加え、公開モード失敗・列挙経路・同期状態変化を確認する。

Phase 2完了前の解消事項です。

---

### H-7 — 30項目の対応はあるが、保証のテストとフェーズ完了条件が不足している

- **重大度:** High
- **該当節:** SPEC §17／PLAN §6 R-8、§7、§8
- **区分:** `[Correctness]` `[Robustness]`
- **根拠の種類:** `[PLANの記述から論理的に導出]`

以下が1対1の照合結果です。CoreはFakeを含む計画、Winは実Windowsで実施する計画です。

| SPEC §17 | PLAN §8 | 評価 |
|---:|---|---|
| 1 | Core＋Win | 問題なし。完全一致の基本ケース |
| 2 | Core | 問題なし。同サイズ・内容違い |
| 3 | Core | 問題なし。展開しないことも検査 |
| 4 | Core | 問題なし |
| 5 | Core＋Win | ZIPにないファイルについて問題なし。同内容ファイルはSPEC §16の例外を明記 |
| 6 | Core | CRCとサイズ詐称は対応。正常終端欠落はH-2 |
| 7 | Core | H-3の全体中止／局所ERRORの区別が必要 |
| 8 | Core | 方針は対応。上限到達後の追加出力確認を必須化 |
| 9 | Core | **SPEC内の矛盾あり。** 宣言1 TiB・targetがKBなら§6-1でMODIFIEDとなり、展開しない |
| 10 | Core | 宣言詐称と実100,001件は良い。実件数の逐次上限も必須 |
| 11 | Core＋Win | 静的ケースは対応。祖先の同一性と削除完了順はC-3 |
| 12 | Core＋Win | 静的ケースは対応。空判定後に子を作る競合を追加 |
| 13 | Core＋Win | 問題なし。属性で示されたdirectoryも含める |
| 14 | Core＋Win | 問題なし。targetを候補から除外 |
| 15 | Core | Probe呼び出しゼロの検査は適切。Windows経路競合は別試験 |
| 16 | Core | 問題なし |
| 17 | Core | 基本対応。制御文字、末尾空白、両区切り等の全拒否規則をfixture化 |
| 18 | Core＋Win | `x`／`x2`は対応。`Foo`／`foo`は未対応 |
| 19 | Win | 静的junctionは対応。検査後差し替え・reparse化は別試験 |
| 20 | Win | hardlink／junctionは対応。symlinkのSkipは保証成立を意味しない |
| 21 | Core | 問題なし。祖先ファイル衝突まで含める方針は適切 |
| 22 | Win | 基本対応。クラウド非ハイドレーションは別途必要 |
| 23 | Win | 両削除モードは適切。確認待ち中もZIPハンドルを保持する試験を追加 |
| 24 | Core＋Win | 再検証前の変更には対応。再検証後の書き込み拒否も必要 |
| 25 | Core＋Win | 置換フックは対応。PreDelete前と後を分け、期待結果を変える必要あり |
| 26 | Win | 問題なし。アクセス拒否を保持へ倒す |
| 27 | Core＋Win＋手動 | **不十分。** ALLOWUNDOなしは本物のfallback環境の代替にならない |
| 28 | Win | 問題なし |
| 29 | Core＋Win | 基本対応。case-sensitive・別名・実パス解決との組合せを追加 |
| 30 | Win | 問題なし。ローカルのopt-inも妥当 |

**#9の修正案**

「宣言サイズ巨大でサイズ不一致なら展開しない」と、「宣言サイズ＝targetサイズだが実際の出力が超過する」を分離してください。後者はPLAN §2 Dの小さな注入上限・展開爆弾で確認できます。

**PLANが追加した主張のテスト**

| 主張 | 現在の計画 | 必要な追加 |
|---|---|---|
| directory保持によるrename防止 | 過去観測のみ | target・各中間・祖先を区別したWin試験 |
| 構造的包含 | 静的junction中心 | 子open直前の競合、target祖先junction、case-sensitiveな兄弟 |
| File IDで差し替え拒否 | #25あり | 確認待ち中、PreDelete前、PreDelete後を別ケース化 |
| `WrongItemRecycled` | P3-2に検出の記述あり | 別ファイルの状態、即時中止、後続非実行まで検査 |
| 完全削除検出 | Fake・過去観測 | Winでの結果分類と残存ファイル確認 |
| parser異常入力 | 一部RawZipBuilder | H-1・H-2の構造別ケース |
| differential test | R-8に言及のみ | P1-3とCIの必須テストへ昇格 |

正常ZIPの`ZipArchive`とのdifferential testは**適切**です。ただし、対応方式・CP437指定・正常入力の範囲を揃え、名前・順序・サイズ・CRC・展開結果を比較してください。外部の複数実装が生成した正常fixtureも必要です。両方が同じ`DeflateStream`を使う部分では、同じ欠陥を共有しうるため、完全な独立oracleにはなりません。

**起こりうる結果と修正**

Fake上で満たした契約がWindowsでも成立すると誤認されます。各追加試験を該当フェーズの必須ゲートにし、実機テストをSkipした場合は、その保証を未確認として残してください。

---

### H-8 — Shell結果の状態機械と「指定した1件だけ」の条件が未定義

- **重大度:** High
- **該当節:** SPEC §2・§10／PLAN §2 A-4、§4.4–4.5、§7 P3-2
- **区分:** `[Correctness]` `[Safety]`
- **根拠の種類:** `[一次資料で確認]`、`[PLANの記述から論理的に導出]`

**問題**

A-4には`GetAnyOperationsAborted`が登場しますが、§4.5には必須呼び出しとして入っていません。公式文書は`PerformOperations`の成否にかかわらず呼ぶよう求めています。[Microsoft Learn「GetAnyOperationsAborted」― Remarks](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-getanyoperationsaborted)

さらに、次の状態が未定義です。

- Postコールバックがない。
- 結果取得や事後ID取得が失敗する。
- callbackの対象が期待したものと違う。
- 一部のAPIだけ成功する。
- Shellが関連項目を操作対象に追加する。

`FOF_NO_CONNECTED_ELEMENTS`が未指定です。公式文書には、HTMLと関連フォルダをまとめて扱う仕組みと、それを抑止するフラグがあります。**現Sinkが関連対象をすべて拒否できるかは未検証であり、関連ファイルが必ず削除されると断定はしません。** ただし「DeleteItemを1回呼ぶ＝対象は必ず1個」という前提は避けるべきです。[Microsoft Learn「Managing the File System」― Connected Files](https://learn.microsoft.com/en-us/windows/win32/shell/manage)、[「SetOperationFlags」― FOF_NO_CONNECTED_ELEMENTS](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags)

**起こりうる結果**

実際の完了を確認せずRemovedに計上する、結果不明のまま次へ進む、想定外の関連対象をShellに処理させる可能性が残ります。

**修正案**

Phase 3までに、成功を積極的に確認できた場合だけ`Recycled`とする状態機械を定義してください。結果不明は停止・報告とし、`FOF_NO_CONNECTED_ELEMENTS`を指定したうえで、HTML＋未検証の関連フォルダを試験してください。

## 4. Medium / Low

### M-1 — FileIdentityと「再検証」の保証範囲を狭く正確に書く

- **重大度:** Medium
- **該当節:** SPEC §10.1・§17-25／PLAN §2 B-5、§4.5、§6 R-1
- **区分:** `[Correctness]`
- **根拠の種類:** `[一次資料で確認]`

File IDは内容ハッシュではありません。また、閉じたハンドルのIDを記録しておくだけでは、時間をまたいだ永続的な個体証明にはなりません。公式文書はファイルシステムによるID再利用を認めています。[Microsoft Learn「BY_HANDLE_FILE_INFORMATION」― Remarks](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/ns-fileapi-by_handle_file_information)

PLANの二段階設計は、**現在の内容を再度全バイト検証する点で妥当**です。分類時IDは追加の差し替え検出であり、次は保証しません。

- 確認待ち中に一度も変更されなかったこと。
- 同じパスが同じディレクトリ階層を通ったこと。
- ID一致だけによる内容一致。
- ハンドルを閉じた後も同じ個体が存在したこと。

修正案は、この範囲を明記することです。「数msだから残存リスク小」も、実測条件のない確率評価なので削除してください。

---

### M-2 — 小さな仕様・計画上の曖昧さを整理する

- **重大度:** Medium
- **該当節:** SPEC §5・§10.2・§11・§17／PLAN §4.2–4.3、§7
- **区分:** `[Correctness]` `[Preference]`
- **根拠の種類:** `[PLANの記述から論理的に導出]`

次を明文化してください。

- **SPEC起因:** 「ユーザー作成ファイルは残す」は、§16の「同内容なら削除対象」と併記しないと過大な保証になります。
- **SPEC起因:** 「ZIP由来ディレクトリ」は由来を判定できません。§10.2の「今回削除したファイルの祖先」で統一すべきです。
- §4.3の空成分拒否は、明示directoryの末尾`/`の取り扱いを別に定義する必要があります。
- `Limits`を`const`として提示しながら「テストでは注入」は、そのままでは契約になっていません。
- §3の「Phase 3完了後」と、P3-6が手動確認に依存する順序を、「P3-4完成後、Phase 3完了前」に直すべきです。
- AOT解析警告ゼロはAOT実行成功の証明ではありません。PLANはリンク未確認を明記しており、ここは**問題なし**です。CsWin32設定の説明も公式schemaと整合します。[CsWin32「settings.schema.json」― comInterop.useComSourceGenerators / preserveSigMethods](https://raw.githubusercontent.com/microsoft/CsWin32/main/src/Microsoft.Windows.CsWin32/settings.schema.json)

## 5. 再検証が必要な主張

以下はすべて、**PLAN記載の実機結果（レビュアーは再現不可）**を検証対象にしたものです。Windowsのビルド番号・ファイルシステム・完全な呼び出し条件が不足する観測は、一般保証には使えません。

| 対象 | 確認すべき条件と結果 |
|---|---|
| directory保持 | OSビルド、FS、desired access、share modeを記録。保持対象自身・親・さらに上の祖先のrenameを別々に確認 |
| 子のopen | 実装するAPIを固定し、検査と次のopenの間に経路を変更。target外を**開く前に**防げるか確認 |
| 空directoryのreparse化 | 共有RWの保持中、検査済みの空directoryへのjunction設定・子移動が可能か。R-3の適用限界を確認 |
| target祖先のjunction | 入力経路を解決した後、junctionを変更しても保持targetから逸脱しないか |
| case-sensitive directory | `Foo`と`foo`を同時に作り、包含、拒否リスト、再open、Shell対象解決を別々に確認 |
| File ID | 確認待ち中の置換、同一IDの内容変更、取得失敗、ボリューム差を確認。ID単独を内容証明にしない |
| ファイル共有 | 分類用READ共有、Recycle用READ＋DELETE共有、完全削除用READ共有の各条件で、書き込み・rename・Shell操作の成功／拒否を確認 |
| ごみ箱環境 | USB FAT32/exFAT、fixed扱いの外付け、ネットワーク文字ドライブ、UNC、容量超過、**無効化したfixed drive** |
| フラグ組合せ | 製品と同じ`FOF_NO_UI`・`FOFX_EARLYFAILURE`等で確認。`RECYCLEONDELETE`・`WANTNUKEWARNING`の有無も比較 |
| fallback | Preの0x80、Post結果、実ファイルの残存を照合。0x80の有無だけで結果を推定しない |
| 長いパス | 8.3有効／無効、通常名／短縮名、manifest・OS設定を記録。事前失敗と実削除後の結果取得失敗を区別 |
| Shellの追加対象 | HTML＋`*_files`、予期しないcallback、中断、事後照合失敗。後続を開始しないことも確認 |
| `WrongItemRecycled` | Pre前の差し替えは拒否、Pre後は残る競合として検出試験。復元可能性を保証と混同しない |
| ディレクトリ削除 | 空判定後の子作成、候補の差し替え、reparse化、削除マーク後のclose、子→親の順序 |
| クラウド | M-7の3状態、公開モード失敗、読み取り前の検出、非ハイドレーション。アイコンだけでなく状態・属性の記録も必要 |
| 自前ZIP | H-1の構造fixture、H-2の終端欠落、有界ストリームの全Read経路、使用ランタイムでのstrict validation |
| 性能 | 100,000件で長い名前・深い階層・多数の共通祖先を含め、分類＋再検証＋Shell操作全体を測る |
| 名前付きストリーム | 同一無名内容＋追加ストリームを保持できるか。検査・削除間の変更条件も確認 |
| NativeAOT | 採用時のみ、リンク済み成果物でSTA・Sink・ごみ箱操作を確認 |

M-1／M-3の事前ドライブ拒否は有益です。M-2、M-4、M-6も必要な観点を含んでいます。ただし、その結果を待つだけでC-2の仕様上の問題が自動的に解決するわけではありません。

## 6. Q-1〜Q-13への独立した見解

以下の「自分の方針」は、推奨列を読む前に書き出した内容を維持しています。比較・理由を後から加えています。

| Q | 自分なら選ぶ方針 | PLANとの比較 | 理由・位置づけ |
|---|---|---|---|
| Q-1 | システムディレクトリ配下も拒否 | 一致 | 安全側の方針選択。**Preference寄りのSafety強化**。決定後の正確な拒否は必須 |
| Q-2 | クラウドplaceholderをスキップ | 一致 | **Safety／Correctness必須**。公開モード失敗時の続行は認めない |
| Q-3 | APIの不足を確定してから自前化を選ぶ | 条件付き不一致 | 不足は確認できたが、H-1・H-2の費用が未算定。**実装方式はPreference、正常性検証は必須** |
| Q-4 | 入力target自身のreparseを先に検査し、拒否 | 動作は概ね一致 | 全件SKIPPED／終了3は分類上の選択。祖先reparseを「不問」にする部分はC-1解消が条件。**Safety必須＋終了コードはPreference** |
| Q-5 | 全体上限超過は削除前に終了3 | 一致 | **Correctness**。ヘッダー検査は必要なので「何も読まず」ではなく「エントリ展開・削除前」 |
| Q-6 | 配布容易性を優先し、安全性と分離 | 概ね一致 | **Preference**。NativeAOTを先に必須化する理由はない |
| Q-7 | 異常検出時は即停止し、損失を明示 | 一致 | **Safety必須**。事後停止で事前防止を代替できない |
| Q-8 | ドライブ種別だけでは保証せず、根拠不足なら削除しない | 不一致 | **Safety必須**。fixed＋非UNCは十分条件ではない |
| Q-9 | 読み取り専用を自動解除せず、不能なら保持 | 不一致 | **Preference**。SPECの内容一致を基準に削除対象とするPLAN案にも合理性はあるが、`IGNORE_READONLY`は明示的に決めるべき |
| Q-10 | 作成OS・属性定義に従って通常以外を除外 | 部分一致 | **Correctness必須**。未知属性を他OSのUnix modeとして無条件解釈しない。0x400の「下位バイト」は訂正 |
| Q-11 | 不正UTF-8を置換復号せず拒否 | 一致 | **Correctness／Safety必須**。UNSAFE_PATHかERRORかは分類上の選択 |
| Q-12 | 最終書き込み日時を比較し、「一切変更しない」の範囲を明記 | 一致 | **Correctness**。アクセス日時を外すだけでハイドレーションまで許可したことにはしない |
| Q-13 | target自身と祖先を区別し、固定できない経路は拒否 | 条件付き不一致 | **Safety必須**。祖先junction経由という理由だけで全面禁止する必要はないが、実パス文字列の取得だけでは足りない |

## 7. PLANの安全性主張の棚卸し

この表の一次資料欄は、**レビュアー自身が今回読んで確認した内容**です。上の指摘に付した文書名・節・リンクを参照しています。

| 主張 | PLANの該当節 | 根拠 | PLAN記載の実機結果（レビュアーは再現不可） | 一次資料で確認（レビュアー自身が確認したもの） | 未検証／推論のみ | 過大評価の可能性 |
|---|---|---|---|---|---|---|
| 0x80がなければPreで削除を止める | A-2、4.5 | API契約＋観測 | ALLOWUNDOなしで拒否 | Preのエラー返却による中止 | 実fallback時に0x80が消えるか | **あり**。0x80ありは許可証ではない |
| PostのNULLで完全削除を検出 | A-2、A-4 | API契約＋観測 | 完全削除後NULL | Postの`psiNewlyCreated`の意味 | callback欠落・結果取得失敗 | 「常に確実」は強すぎる |
| fixed・非UNCなら候補にできる | 4.5、Q-8 | ドライブ分類 | fallback環境未確認 | GetDriveTypeは媒体種別 | ごみ箱無効・容量・設定変更 | **あり** |
| 1件ずつなので最悪1件で停止 | R-4 | 制御フロー | 一般条件の記録なし | Shellには関連項目の概念 | 関連対象、結果不明時の制御 | 条件未定義 |
| 保持中は書き込み不能 | A-3、4.5、README | 共有モード | 特定条件で共有違反 | CreateFileの共有契約 | 名前付きストリーム等の範囲 | 対象を限定すべき |
| PreのID照合で差し替え拒否 | A-3、4.5 | 同一性比較 | Preより前の置換を拒否 | File IDによる同一性識別 | Pre後の置換 | 防止範囲を限定すべき |
| 別ファイルのごみ箱送りを事後検出 | A-3、R-1 | 最終パス／ID比較 | 元ハンドルが`.orig`に残った | APIでパス・IDを取得可能 | 全エラー経路での検出・復元 | 「必ず検出・復元」は過大 |
| 同じハンドルで完全削除すればファイル単位の再解決がない | A-5、R-2 | ハンドル指定 | 成功の記載あり | Dispositionの対象はハンドル | 適格性・完了時点・C-4 | 対象再解決の排除として妥当 |
| 中間directory保持で経路が固定される | B-2、R-3 | 共有モード＋観測 | 自身と親のrename失敗 | 対象の共有制約 | 全祖先、空directoryのreparse化 | **あり：C-1** |
| ハンドル連鎖でcase-sensitive包含も安全 | B-7、X-4 | 設計上の主張 | `A.txt`／`a.txt`共存 | case sensitivityの存在 | 子openの具体方式 | **未成立** |
| reparseをタグ不問でスキップ | B-2、R-6 | 属性検査 | junctionを検出 | reparseの仕組み | cloud非公開状態、検査後変更 | 静的・可視状態に限定 |
| リンク数>1ならhardlinkを除外 | B-4、4.5 | ハンドル情報 | links=2を取得 | リンク数を取得できる | 再検証タイミング | 基本方針は妥当 |
| cloud公開モードで全同期状態を除外 | B-3、R-5、README | API説明＋推論 | 未確認 | 特徴の公開モード | OneDrive全状態・失敗時 | **あり：H-6** |
| ZIPをFileShare.Readで保持すれば書き換えを抑止 | 4.2、4.5 | 共有モード | 寿命全体の試験記載なし | 共有モードの契約 | プロンプト中の保持、対象範囲 | 契約明記で改善可能 |
| バイト数・CRC・比較でMATCHEDを確定 | C-3、4.2 | 比較アルゴリズム | CRC改ざん等を観測 | runtimeの読み取り経路 | 正常Deflate終端 | **不足：H-2** |
| 有界ストリームで圧縮範囲を限定 | 4.2 | 設計上の主張 | 自前展開の一例 | ZIPの構造定義 | overflow、overlap、truncation | **不足：H-1** |
| 件数・16 GiB上限で資源を制限 | D、4.2 | ベンチマーク | 短名・温キャッシュ等 | — | メタデータ総量・総時間 | **あり：H-4** |
| 危険名はFSに触れる前に拒否 | 4.3、8-15 | 純粋な検証処理 | 一部名前ケース | — | 全拒否条件の実装 | 設計として問題なし |
| 不正UTF-8・曖昧名は削除しない | 4.3、Q-11 | 厳格復号・グループ化 | 重複列挙等 | — | UnicodeとFSの全別名関係 | 記載範囲では妥当 |
| 空directoryの非再帰削除なら無関係ファイルは残る | 4.5、P3-5 | 空判定＋OS削除 | 非空拒否の記載 | 空directory削除の契約 | directory自体の差し替え | **不足：C-3** |
| 再検証で確認待ち中の変更を拒否 | 4.5、P3-1 | 二段階比較 | 一部置換観測 | IDは内容証明ではない | root・ZIP寿命と候補適格性 | 方針は妥当、契約不足 |
| 長いパスは失敗すればERRORで安全 | B-6、R-9 | fail-safe | 8.3ありで成功 | APIのパス要件 | 削除後の照合だけ失敗する場合 | 失敗時点の区別が必要 |
| 正常ZIPとの差分試験で自前parserを補強 | R-8 | テスト方針 | 実施記載なし | — | CI・fixture・比較項目 | 補強として妥当、保証の代替不可 |

補足として、case sensitivity自体は[Microsoft Learn「Case Sensitivity」― Differences between Windows and Linux case sensitivity、Change the case sensitivity of files and directories](https://learn.microsoft.com/en-us/windows/wsl/case-sensitivity)で確認しました。PLANの独自の包含方式が正しいことまで確認したものではありません。

## 8. Phase 1開始前チェック

### Phase 1開始前に直す必要があるもの

- **C-1:** ハンドル相対openか、それに代わる経路固定方式かを決め、「構造的包含」の契約を具体化する。
- **C-2:** 完全削除の事後検出をSPEC適合と扱わない。保証不足時の拒否方針を決める。
- **C-3:** ディレクトリ候補の同一性・ハンドル寿命を設計に入れる。
- **C-4／SPEC起因:** 同一性判定に含めるデータストリームの範囲を決める。
- ZIP自前化を既定の正解とせず、H-1・H-2を含む採用条件を置く。
- SPECのcase-insensitive包含規則を、重複・拒否リストの比較と分離する。

### Phase 1中に決めればよいもの

- H-1のZIP受理範囲と異常入力一覧。
- H-2の正常終端確認方法。
- H-3の全体不適格／エントリ局所エラーと削除開始ゲート。
- H-4のメタデータ・深さ・総処理量の制限。
- H-5の抽象詳細。**P1-6の確定前**に列挙・結果不明・権限・寿命を反映する。
- H-7の#9修正、正常ZIPのdifferential testをP1-3の必須条件にする。
- directory表現、上限注入、分類・終了コードの細部。

### Phase 2まで保留できるもの

- C-1の具体的Windows実装の再検証。ただし方式決定自体は保留しない。
- directory共有モード、祖先rename、reparse変更、case-sensitiveの実Windows試験。
- H-6のplaceholder公開モードと失敗時中止。
- File ID・リンク数・属性・ストリーム情報取得失敗の扱い。
- 長いパス、実パス解決、拒否リスト、dry-runの実FS検証。

### Phase 3まで保留できるもの

- C-2で決めた安全条件を満たす、ごみ箱実装の検証。
- 無効化したfixed driveを加えたM-1–M-6。
- H-8のShell結果状態機械、関連項目抑止、callback欠落・中断・結果不明。
- Pre前／後の差し替え、`WrongItemRecycled`、完全削除事後検出後の全停止。
- directoryの差し替え拒否、非空競合、close順序。
- readonly方針と完全削除の実挙動。
- READMEの「復元できる」「クラウドは全件対象外」などを、実際に確立した保証の範囲へ修正する。