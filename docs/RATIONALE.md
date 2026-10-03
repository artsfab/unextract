# 現行判断の理由

役割: 契約を支える非自明な理由・観測条件・避けたい失敗。通常は担当仕様とテスト節から始め、判断の前提を変えるときだけ該当節を読む。

[処理とFS](#filesystem) / [ZIP/runtime](#zip-runtime) / [CLI/モード](#cli-modes) / [構造](#structure)

V・PoC・E-2の原観測は `606d89c:docs/PLAN_VALIDATION.md` の同名見出しで参照できる。

<a id="filesystem"></a>
## 処理モデルとファイル安全性

<a id="current-state"></a>
### 現在状態と同じハンドル

[実行契約](SPEC.md#execution)は、analyzeやentriesを削除の許可証にしない。analyze後には内容だけでなく個体・名前・権限も変わり得るため、deleteが現在状態を検証する。確認待ちに数万件のハンドルを保持すると資源を消費し他アプリを長時間妨げる。1件の検査・比較・削除を同じハンドルへ限定して、検査した個体と削除する個体を一致させる。比較は各操作で1回であり、先行analyzeの観測を引き継がない。

OpenFileByIdではtarget外へ移した個体も開けるため、結局最終パス確認が要る。親を別のパスopenで確認すると対象openとの間に別の名前解決が入る。同じハンドルで得るUSN親IDを使う理由はこの隙間を避けるためである。USNジャーナル非アクティブのNTFSでも取得できたことは、機能のないFSの保証ではない。

Core S05/S06は呼び出し順・同じハンドル・ZIP Open回数、A08はメタデータを書き戻した内容変更と同名再作成を確認する。ハンドル構成・寿命・API変更時はWinの個体保証も再確認する。重要な原観測: PoC 4 (2026-10-02、Windows 11 10.0.26300、NTFS、自作USN非アクティブVHD、.NET 10.0.12)。

<a id="identity-path"></a>
### 親IDと最終パスを省かない理由

[削除順序](spec/filesystem.md#delete-flow)で両方を確認する。同名の別親へ同じ個体を移すとFile IDと最終パスは同じでも親IDが変わる。途中のjunction化・大小文字だけの改名では親IDが同じでも最終パスが変わる。片方だけの最適化はそれぞれを見落とす。Core/WinのS22・S23、最終確認S27が回帰を担う。経路解決や同一性のAPIを変えるときは両ケースを再確認する。

<a id="hardlink-parent-id"></a>
### hardlinkの親IDは開いた名前の親とは限らない

[非削除例外](spec/filesystem.md#hardlink-parent-id)は、USNがどのリンクの親を返すかを保証しない観測への対応である。2026-10-03のWindows 11実測では別ディレクトリの2名をそれぞれ削除用に開いても元の名前の親が返り、追加名では親が一致しなかった。CIのS20にも不一致がある。返すリンクの環境差の理由は未確認。

リンク数を確認した非dir・非削除保留の対象を非削除へ倒す場合にだけ例外を認める。一般の削除候補を親不一致で続行させる根拠にはならず、取得失敗も緩めない。Core/Win `S20_HandleChecksAreNotSkipped`、`S20_HardLinksInDifferentParents_AreBothSkipped`とCoreの`S20_ParentCheckIsRequiredExceptForConfirmedHardLinks`と[現在の実装](../src/Unextract.Core/Deletion/SequentialDeleter.cs)を確認し、OS/USN取得・検査順序変更時にリンク別の値と非削除を再確認する。

<a id="sharing-limits"></a>
### 共有モードで防げる変更と残る窓

[ハンドル構成](spec/filesystem.md#handles)はデータアクセスを伴う共有モードを使う。FILE_READ_ATTRIBUTESだけの保持では改名を防げず、root保持にLIST_DIRECTORYを含める。削除用openは既存writerやDELETE非共有readerと競合し、開いた後の書き込み・改名・削除openも妨げる。一方、属性・ADS・hardlink追加は排除できないため、比較後にM0全項目を再確認する。

Fastの最終確認はM0取得直後で冗長に見えるが、省略するとモード別の安全性分岐が増える。両モードで同じ最終確認を残す。最終確認から指示までの競合窓は消せず、メモリマップも未確認である。[限界](spec/filesystem.md#limitations)と[課題](OPEN_ISSUES.md#procedure-review)を越える保証にしない。

V5のroot改名は同一プロセス別ハンドル、PoC 5/6は別プロセス。2026-10-02、Windows 11 Home 10.0.26300、NTFS、SDK 10.0.401 / .NET 10.0.12、当時の削除用ハンドル構成での観測である。現行回帰はWin S24・S25・S27、root保持はHandleOpenerTests。構成・共有モード・Windows変更時は再確認する。

<a id="disposition"></a>
### 削除指示と成立を分ける理由

[指示・成立確認](spec/filesystem.md#delete-flow)はAPI成功だけでDELETEDにしない。DELETEなしのflagsでもAPI成功を返し何も削除しない観測があるため、同じハンドルのDeletePendingを確認する。指示後NumberOfLinksが0になる観測は公式の裏付けがないため、成立条件にしない。

POSIX semanticsを含める理由は名前消失の時点である。DELETEだけではDELETE共有の他ハンドルが閉じるまで名前が削除保留で残り、同名open/作成がアクセス拒否になる。採用flagsでは自分のcloseで名前が消え、他のreaderは読み続けられる。read-onlyを無視しないので最後の防壁が残る。指示後に成立が分からないSTOPは対象残存を保証できない。

2026-10-02のPoC 1〜3/5、Windows 11 10.0.26300、NTFS、.NET 10.0.12、他者の操作は別プロセスで観測。現行[Core逐次S28/S31/S32/S33](../tests/Unextract.Core.Tests/SequentialDeleteTests.cs)と[Win DeletionHandleTests](../tests/Unextract.Windows.Tests/DeletionHandleTests.cs)が回帰を担う。API/flags/成立判定変更時は名前消失とDeletePendingを再確認する。

<a id="open-failures"></a>
### 識別確認で分かることとS09

[失敗境界](spec/filesystem.md#failure-boundary)は、共有違反・アクセス拒否以外を推測で続行しない。アクセス拒否はACL、dir/junction差替え、削除保留で同じコードになり理由を区別できない。別openによる識別確認は拒否された個体や理由を証明しないが、一致しても非削除で続行するだけなので採用できる。失敗・不一致は停止する。analyzeは分類を確定できないためFATALである。

分類のために比較用ハンドルで開き直すと処理経路が増えるため行わず、削除用openに失敗した対象の内容一致・不一致は判定しない。

ZIP自身は保持中のDELETE非共有により削除用openが共有違反となり、識別確認一致でDELETE_FAILEDとなる。専用の事前SKIPPED分岐は一般則を重複させるため設けない。後続は独立に検証できるので続行する。2026-10-03に明示的に確定した扱いであり、判断待ちではない。回帰は[Core](../tests/Unextract.Core.Tests/SequentialDeleteTests.cs)/[Win](../tests/Unextract.Windows.Tests/Integration/SequentialDeleteIntegrationTests.cs)の `S09_ArchiveItselfIsNotDeleted`、S16〜S18。openエラー・識別確認の範囲を変えるときに再確認する。

<a id="acl"></a>
### ACLを比較対象にしない理由

[ハンドルの権限](spec/filesystem.md#handles)はWindowsの通常の権限判定を受け入れる。対象DELETE拒否だけでは親DELETE_CHILDによる削除を防げない。安全に検証した個体をWindowsが削除できるなら、ACL自体を追加の比較対象にはしない。指示時の拒否から原因を確定できず、検査後の前提も崩れ得るので、その失敗はSTOPを維持する。

ACL・権限判定・検査順序を変更するときは、[S34の観測範囲](OPEN_ISSUES.md#observations)を確認する。再現時は親DACLが子のChangeTimeを変える[fixture上の罠](TESTING.md#reproduction)を避ける。

<a id="real-names"></a>
### 実名解決と列挙結果の再利用

[実名確認](spec/filesystem.md#real-names)は検証済みdirの列挙をOrdinal照合する。8.3名を返さない列挙なら短名だけの一致を自然に避け、名前・属性・tag・128bit IDを同時に得られる。FindFirstFile検索は短名でも一致し、毎回パス解決し、見つからない/親消失/アクセス拒否の振り分けと追加実名比較が必要になる。巨大dirに少数の候補がある場合は列挙が遅くても、一貫性と安全性を優先した。UNCではサーバー側のFS名・IDの安定性・共有意味論をクライアントから保証できず対象外にする。

列挙中の改名には見落としや旧名と新名の両方があり得る。同名重複は最初を採用して決定的にし、後で開いたID/最終パスを確認する。見落としは非削除なので安全側。照合しなかった名前を保持・表示しないことで無関係ファイルに作用しない。

逐次削除でも照合結果を再利用する。事前ZIP構造検査により削除した最終名を後続が最終成分や親として再使用せず、dirは削除せず、dir内の削除開始前に列挙を終える。POSIX closeで自分の削除保留名も残らない。古い「存在」はopen/照合で検出し、古い「不存在」は非削除となる。再列挙は安全性を増やさず総量を二乗に増やし得る。

V7/PoC 8 (2026-10-02、同Windows・NTFS・runtime) の列挙観測を現行T06/T14/T15/S35/S36で回帰する。列挙直後改名の組合せは推定として[未確認](OPEN_ISSUES.md#observations)に残す。列挙API/キャッシュ/名前照合変更時に再確認する。

<a id="special-precheck"></a>
### 事前判定は非削除だけに使う

[特殊判定](spec/filesystem.md#special-files)を列挙属性で先に行うのは、dirへ削除用openを試みるとアクセス拒否→識別確認不一致で全体STOPになり、通常のdir不一致に過剰に停止するためである。削除用へBACKUP_SEMANTICSを足す案は検証済み構成を変え、dirをDELETEで開くことになる。別openの識別確認からSKIPPEDを追加する案も根拠が弱く分岐を増やす。

古い列挙でも先行判定は非削除側にだけ働く。通過した個体をハンドルで再検査するので、属性一致の一般保証に依存しない。許可リストは通常のローカル読み取りが可能な属性に限定し、未知ビットは保守的に非削除へ倒す。Core/Win S19/S20とT16、属性ビットはFileAttributeRulesTests。属性/API変更時はS38の限定付き観測も確認する。

<a id="zip-runtime"></a>
## ZIPとruntime

<a id="zip-decoding"></a>
### 公開APIで得られない情報への保守的対応

[復号規定](spec/zip.md#decoding)はCP437を明示する。未指定ではフラグなしもUTF-8になり、不正UTF-8は例外なしに置換される。生名が公開されないため正当な置換文字まで含めて拒否する。作成元OSも公開されないため、種別をExternalAttributesと末尾区切りから保守的に判定し、矛盾した種別を受け入れない。

2026-10-02、SDK 10.0.401 / runtime 10.0.12、Windows 11 Home 10.0.26300でのV1観測。Z06/Z07とEntryTypeTests、ZipArchiveSourceTestsが回帰を担う。SDK/runtimeや復号API変更時に再確認する。

<a id="zip-content"></a>
### CRC・長さ・暗号化をruntime任せにしない

[内容検証](spec/zip.md#verification)の補助検査は、runtimeがCRC不一致を検出しない、Deflate途中切れを例外なしに短く返す、暗号化フラグ付きでも平文fixtureを読める、宣言Lengthで出力が切られるという観測を補う。公開Crc32とIsEncrypted、実測バイト列で検査でき、独自構造パーサ/reflectionは不要。StoredのLength超過と不足、Deflateの不足も区別する。

CRCは作成者がデータと一緒に書き換えられるので敵対的ZIPへの防御ではない。Strictの根拠は全バイト一致である。内容不一致でもZIPを終端まで読むのは、不一致位置によってMODIFIED/FATALが入れ替わらないようにするため。Local HeaderとCD/DDの照合、圧縮領域の重なりなどの健全性証明は目的外である。ZIP64/DD/SFXも公開ライブラリへ委譲し、独自形式判定を足さない。

V2/V3は2026-10-02、同Windows・SDK・runtime、ヘッダーを変更したfixtureの観測。C01/C03/C07を単体ZipArchiveで読むC13、C/Z系列が継続確認する。パッチ更新でも結果が変わり得るため、runtime更新時は観測と製品の検出を分けて再確認する。

<a id="zip-limits"></a>
### 受理範囲と上限

[ZIP上限](spec/zip.md#limits)はMISSINGや選択外にも宣言側を適用する。target/操作/entriesから独立に受理を決め、作成者が書ける巨大宣言値の桁あふれを早期拒否し、受理範囲を固定するためで、読まないデータそのものが危険だからではない。実際に読む量も独立に検査する。Central Directory事前割当を独自パーサなしで制限できない限界は残る。R03/R06/R07/R08/R09を確認し、上限の対象・計上単位・時点を変えるときは選択外とMISSINGのfixtureも再確認する。

<a id="cli-modes"></a>
## CLIとモード

利用者の中止と検証・処理の失敗を呼び出し側が区別できるよう、[終了状態](spec/cli.md#arguments)を分ける。DELETE_FAILEDがあれば、内容は未確認でも要求された削除を完了できなかったため、完走しても成功とはしない。

<a id="confirmation"></a>
### 確認時点と旧CLI拒否

[確認](spec/cli.md#confirmation)はPrepare後・最初のエントリ前。最初の候補を見つけてから確認すると、そのハンドルを長時間保持するか1件だけ閉じて再比較する必要があり、逐次方式の目的を壊す。削除件数が結果的にゼロでも最大件数で確認する。

[旧CLIの拒否](spec/cli.md#arguments)は、旧形式を黙ってdeleteに割り当てて全件確認から部分削除へ意味を変えないため。dry-runだけaliasで残すとそれを外した意味が曖昧になり、旧全件方式を残すと二重比較が戻る。入力エラーと移行案内なら誤操作時も非削除となる。S03/P05/K02/K04/X20/X25、CLI形・確認時点を変えるときに再確認する。

<a id="entries-display"></a>
### exactなEntryと転記の例外

[entries](spec/cli.md#entries)は範囲を狭めるだけで、ID・ハッシュ・分類入りのplanは現在状態の検証に寄与せず許可証に見える。trim/glob/コメント/区切り補正を設けず規則を一つにする。BOM付きUTF-8は既存ツールとの実用上の互換性を保ち、UTF-16は明確に案内して拒否する。端末保存形式はM13未実測である。

[表示](spec/cli.md#result-lines)はEntryをそのまま転記できるようにする。バックスラッシュを二重化すると一致しない。危険な書式文字だけは安全にエスケープして転記不可を示す。状態列だけを固定幅にするのは長名と全角文字でTargetが遠くなるのを避けるため。Targetは確認用の対応位置で入力ではなく、MISSINGでも同じ列の意味を保つ。L14/L15/O09/O10/X23、表示・encoding変更時に再確認する。

<a id="fast"></a>
### Fastを単一の内容非読取差分にする

[モード契約](SPEC.md#modes)はSAME_SIZEという名前で内容一致を誤認させない。CRC中間モードやFast専用の復元検査は内容を読む経路と検証の組合せを増やし、Strictの保証にも届かないため採らない。[安全性共通の理由](#sharing-limits)により最終確認も残す。

[ヘッダーの警告](spec/cli.md#warning)を標準出力に書くのは、結果一覧だけを保存した場合にもFastの結果であることを残すためである。

C15/R09/T17/S06/S07/X26は非読取と同サイズ内容違いを回帰する。CRC計算自体の直接観測とbody非読取からの間接確認は[TESTING](TESTING.md#reproduction)で区別する。モード分岐・比較経路変更時に再確認する。

<a id="structure"></a>
## 実装構造

[コード配置](ARCHITECTURE.md#dependencies)はCoreをWin32から分離し、同じ解決・検査・比較を両入口で共有する。安全性の重複実装を避け、ハンドル種別と失敗の意味は呼び出し側で明示する。Fast専用の層・クラス・抽象・状態・API・ハンドル構成を作ると共通安全性を監査しにくくなるので追加しない。

global.jsonは観測基盤とビルドSDKを合わせるため固定し、同じ機能帯のパッチ更新を許す。設定の正本はglobal.jsonであり、変更時はC/Z回帰でruntime観測を再確認する。配置変更時は依存方向とanalyzeの型に削除能力がないことを確認する。
