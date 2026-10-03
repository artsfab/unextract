# ZIP仕様

役割: ZIP名・種別・構造、内容検証、ZIP上限とライブラリ委譲の正本。復号、CRC、受理範囲を変更するときに読む。

[名前](#names) / [内容](#content) / [上限](#limits)

<a id="names"></a>
## 名前・パス・構造・種別

<a id="decoding"></a>
### 名前の復号

- `ZipArchive` に `entryNameEncoding` として CP437 (`CodePagesEncodingProvider` 経由の code page 437) を渡す。採用版では UTF-8 フラグ (general purpose bit 11) 付きのエントリは UTF-8、フラグなしは CP437 で復号される。`entryNameEncoding` を渡さないとフラグなしも UTF-8 として扱われるため、必ず渡す。CP932 等の推測はしない。
- `ZipArchive` は不正な UTF-8 を例外にせず U+FFFD に置換する。生の名前バイトは公開 API で得られないため、**復号後の名前に U+FFFD を含むエントリは FATAL** とする。正当に U+FFFD を含む名前も拒否されるが、安全側の制限として許容する。

<a id="paths"></a>
### パスの検査

- `/` と `\` を区切りとして扱い、正規化前に絶対パス、ドライブ指定、UNC・デバイスパス、`..`、`.`、空成分、コロン、NUL・制御文字 (U+0000〜U+001F、U+007F)、Windows 予約名 (下記)、末尾ドット・空白、`<>"|?*` を含む名前を拒否する。ディレクトリエントリの末尾区切り1個だけは許す。target の外へ出得る名前は拒否する。
- Windows 予約名は `CON`、`PRN`、`AUX`、`NUL`、`COM0`〜`COM9`、`COM¹`、`COM²`、`COM³`、`LPT0`〜`LPT9`、`LPT¹`、`LPT²`、`LPT³`、`CONIN$`、`CONOUT$` とする (`¹` `²` `³` は上付き数字 U+00B9、U+00B2、U+00B3)。各成分について、**最初のドット (`.`) より前の部分 (ドットが無ければ成分全体) から末尾の空白 (U+0020) を除いたもの**が、予約名のいずれかと大小文字を区別せずに (`OrdinalIgnoreCase`) 一致すれば拒否する。したがって拡張子付き (`CON.txt`、`Prn.tar.gz`)、拡張子の前の空白 (`NUL .txt`)、ディレクトリ成分 (`CON/a.txt`、`aux/`) も拒否し、`CONSOLE.txt`、`COM10`、`LPT`、`xCON` は予約名ではない。末尾が空白の成分 (`CON `) は、予約名の判定より先に末尾の空白として拒否される。
- 名前長と深さは [ZIP上限](#limits) の上限に従う。
- FullName 自体は変換しない。区切りで分割した成分は target 側の解決 ([target分類](filesystem.md#classification)) に使い、FullName は表示 ([表示](cli.md#output)) と `--entries` の照合 ([entries](cli.md#entries)) に使う。

<a id="structure"></a>
### ZIP内部の構造

- 同一パスの重複、大文字小文字だけが違う重複 (`OrdinalIgnoreCase` での衝突)、ファイルとディレクトリの同名衝突、ファイルを親とする子エントリは FATAL。明示ディレクトリと暗黙ディレクトリも含め、Windows 上で一意に対応付けられない構造は FATAL。
- **ZIP 内部構造の衝突と target の実在状態は別概念である。** ZIP 内で `a` がファイルなのに `a/b.txt` もある場合は ZIP 内部構造衝突で FATAL。ZIP に `a/b.txt` があり target に `a/` 自体が存在しない場合は、そのエントリが `MISSING` になるだけで正常である ([解決順序](filesystem.md#resolution))。

<a id="types"></a>
### ZIPの特殊エントリ

`ZipArchiveEntry` は作成元 OS (version made by) を公開しないため、`ExternalAttributes` だけで次のように判定する。

- ディレクトリエントリは名前が `/` (または `\`) で終わるもの。`Length` が 0 でないディレクトリエントリは FATAL。
- `ExternalAttributes` の上位16ビットのファイル種別 (`(attr >> 16) & 0xF000`) は、エントリの種類ごとに次だけを許す。それ以外は FATAL。
  - ファイルエントリ (名前が区切りで終わらない): 0 または `0x8000` (通常ファイル)。**種別が `0x4000` (ディレクトリ) のファイルエントリは FATAL。**
  - ディレクトリエントリ (名前が区切りで終わる): 0 または `0x4000`。**種別が `0x8000` (通常ファイル) のディレクトリエントリは FATAL。**
  - symlink `0xA000`、FIFO `0x1000`、文字デバイス `0x2000`、ブロックデバイス `0x6000`、socket `0xC000` などは、どちらのエントリでも FATAL。
- 下位の DOS 属性に `0x10` (ディレクトリ) があるのに名前が区切りで終わらない、または `0x400` (reparse point) があるエントリは FATAL。read-only、hidden、system、archive などその他の DOS 属性は unextract が復元しないため無視する。
- 判定できないエントリを通常ファイルとみなさない。

<a id="content"></a>
## 内容の読み取りと検証

<a id="read-scope"></a>
### 読む範囲

ZIP の内容を読むのは、Strict の**内容比較候補** (target に対象が存在し、[特殊ファイルと属性](filesystem.md#special-files) の特殊判定を通った後で安全な通常ファイルと判定され、かつサイズが `ZipArchiveEntry.Length` と一致するエントリ。[解決順序](filesystem.md#resolution) の手順 6→7→8 の順に判定する) だけである。`MISSING`、サイズ不一致の `MODIFIED`、`SKIPPED_SPECIAL_FILE`、`DIRECTORY` の内容は開かず、CRC も確認しない。そのため、これらのエントリの圧縮データだけが壊れていても FATAL・STOP にならない。`analyze` と `delete` で範囲は同じで、どちらも1つのエントリの内容を1回だけ読む。

<a id="verification"></a>
### 内容検証の6基準

次の基準を「エントリ内容の検証基準」と呼び、`analyze` と `delete` の全バイト比較の両方で同じように適用する。

1. `ZipArchiveEntry.IsEncrypted` が false。採用版の `ZipArchive` は暗号化フラグ付きの Deflate エントリを拒否せずに読み出すため、unextract が `Open()` の前に確認する。
2. `Open()` と読み取り中に例外が発生しない。`InvalidDataException` (破損・未対応圧縮方式)、`IOException` などは種類を問わず異常とする。
3. 読み出したバイト数が `Length` を**超えた時点で直ちに**異常とする (実測展開量の上限、[ZIP上限](#limits))。
4. ストリーム終端までのバイト数が `Length` と等しい。採用版では Deflate 圧縮データが途中で切れても例外なく短く終わり、Stored は宣言サイズでなく圧縮サイズ分を返すため、unextract が数える。
5. 読み出した全バイトの CRC-32 が `ZipArchiveEntry.Crc32` と等しい ([CRC](#crc))。
6. 固定サイズのバッファで target と比較し、全バイトが一致する。

ZIP ストリームは**一致・不一致にかかわらず必ず終端まで読み切る**。途中でバイトが異なっても 1〜5 の検査は省略しない。これにより、不一致の位置によって結果が変わらない。target 側は不一致を確定した後は読み続けなくてよい。0バイトのエントリも、ストリームが0バイトで正常に終わり CRC が 0 で、target が0バイトなら一致とする。

違反時の扱い: `analyze` では 1〜5 の違反は FATAL、1〜5 を満たし 6 だけが不成立なら `MODIFIED`、全て満たせば `MATCHED`。`delete` では 1〜5 の違反は STOP ([失敗の境界](filesystem.md#failure-boundary))、6 だけが不成立なら `MODIFIED` (削除せず続行)、全て満たせば最終確認 ([削除順序](filesystem.md#delete-flow)) へ進む。

mtime、ZIP の宣言サイズ、CRC 値だけで一致としない。

<a id="crc"></a>
### CRC-32

- 期待値は公開プロパティ `ZipArchiveEntry.Crc32` (Central Directory の CRC-32 フィールド) から得る。このプロパティは .NET Core 2.1 と .NET Standard 2.1 で追加された公開 API であり (.NET Core 2.0・.NET Standard 2.0 の参照アセンブリには無い。.NET Framework には無い)、採用する .NET 10 (LTS) に含まれる。.NET 10.0.12 のランタイムで直接参照してコンパイル・実行できることを確認した ([runtimeの根拠](../RATIONALE.md#zip-content))。
- 実測値は Microsoft 公式パッケージ `System.IO.Hashing` の `Crc32` で、読み出したストリームについて計算する。reflection、private field、独自の ZIP 構造解析は使わない。
- 採用版の `ZipArchive` は、読み切っても CRC 不一致を検出しない ([runtimeの根拠](../RATIONALE.md#zip-content))。したがって CRC-32 の照合は unextract の責務であり、内容比較候補では必須とする。将来の .NET が CRC 不一致を例外で通知するようになっても、[内容検証基準](#verification) の 2 により同じく異常になる。
- **CRC の位置づけ**: CRC は ZIP の作成者が宣言サイズや実データと一緒に自由に書き換えられるため、敵対的に作られた ZIP への防御にはならない。CRC で検出するのは、偶発的な破損、切り詰め、Central Directory の記録と実データの食い違い (宣言サイズが実データと合わず出力が途中で切られる場合を含む) である。削除の根拠は [対象と非目標](../SPEC.md#scope) のとおり全バイト一致であり、CRC は補助検査である。Local Header の CRC との照合はしない。

<a id="runtime"></a>
### ZipArchiveに委ねる範囲

ZIP64、DD、SFX、Stored/Deflate 以外の方式は形式だけで拒否も受理もせず、`ZipArchive` が開けるかどうかに委ねる。`ZipArchive` が開けない ZIP は FATAL。内容比較候補で [内容検証基準](#verification) の 1〜5 を満たさないエントリは、`analyze` では FATAL、`delete` では STOP。runtimeの観測条件は[理由](../RATIONALE.md#zip-content)、再確認方法は[TESTING](../TESTING.md#reproduction)に置く。EOCD、Central Directory、Local Header、DD の照合、圧縮領域の重なり・隙間の検査はしない。unextract は ZIP の完全な健全性を証明するツールではない。

<a id="limits"></a>
## ZIPのリソース制限

ZIP と target ファイルはストリーミングで読み、ZIP 全体やエントリ全体をメモリに展開しない。次の上限を定数として実装する。境界は「上限以下を許可、超過を拒否」。宣言側の上限の超過は Prepare の FATAL (削除0件)、実測側の上限の超過は `analyze` で FATAL、`delete` で STOP。ユーザー向けの上限変更オプションは設けない。

| 上限 | 値 | 対象・時点 |
|---|---|---|
| エントリ総数 | 100,000 | 全エントリ、[Prepare](../SPEC.md#prepare) の手順6 |
| 復号後のエントリ名 | 1,024 UTF-16 コード単位 (復号後の `FullName` のまま数える。ディレクトリの末尾区切りを含む) | 全エントリ、[Prepare](../SPEC.md#prepare) の手順6 |
| パス深さ | 128 成分 | 全エントリ、[Prepare](../SPEC.md#prepare) の手順6 |
| メタデータ総量 | 128 MiB (134,217,728 バイト) | 全エントリの復号後の名前の UTF-16 バイト数 + 1エントリ当たり 128 バイトの合計、[Prepare](../SPEC.md#prepare) の手順6 |
| 1エントリの宣言展開量 | 16 GiB (17,179,869,184 バイト) | 全ファイルエントリの `Length`、[Prepare](../SPEC.md#prepare) の手順6 |
| 宣言展開量の合計 | 64 GiB (68,719,476,736 バイト) | 全ファイルエントリの `Length` の合計、[Prepare](../SPEC.md#prepare) の手順6 |
| 1エントリの実測展開量 | そのエントリの `Length` | 内容を読むたび (`analyze`・`delete` の全バイト比較)、超過した時点 |
| 実測展開量の合計 | 64 GiB | その実行の全バイト比較の累計、超過した時点 |

- 名前長は、復号後の `FullName` を区切りの変換や末尾区切りの除去をせずにそのまま数える (ディレクトリエントリ `d/` は 2 コード単位)。メタデータ総量の名前の UTF-16 バイト数も同じ `FullName` で数える。
- ファイルエントリの宣言 `Length` が負の場合は、宣言展開量として扱えないため FATAL (`InvalidDeclaredLength`) とする ([Prepare](../SPEC.md#prepare) の手順6)。ディレクトリエントリの `Length` が 0 でない場合 (負を含む) は [エントリ種別](#types) により FATAL になる。
- メタデータ総量はエントリ数とは独立したハード上限である。長い名前が大量にある ZIP で 100,000 エントリより先にこの上限へ達しても、意図した拒否とする。
- **宣言展開量の上限は、内容を読まない `MISSING` を含む全エントリに適用する。** 全エントリが `MISSING` になる ZIP でも、宣言合計が 64 GiB を超えれば FATAL とする。`delete` で `--entries` を指定した場合も、選ばれなかったエントリを含む ZIP 全体に適用する。理由は[受理範囲の判断](../RATIONALE.md#zip-limits)による。
- 実際に読む量の上限は実測側で必ず強制する。内容を読むたびに `Length` を1バイトでも超えた時点で中断するため、実測量は常に宣言量以下になる。実測の合計も読み取り中に確認する。各エントリの内容は1回の実行で高々1回しか読まないため、実測の合計は宣言の合計を超えないが、独立した検査として残す。`delete` が途中で STOP した場合、累計はその実行の終了とともに捨てる (実行をまたいで数えない)。
- `ZipArchive` は開いた時点で Central Directory 全体をメモリに読み込む。この割り当ては unextract の上限検査より前に起こり、独自パーサなしには事前に制限できない。異常に大きな Central Directory でメモリが不足した場合は削除前に異常終了し、削除は起こらない。
- 空 ZIP、空 target は正常に扱う。

Fastは全ZIPの宣言側検査を維持し、内容検証と実測展開量の計上を行わない。[モード契約](../SPEC.md#modes)に従う。MISSING・サイズ不一致・特殊対象の内容破損は検出しない。完全なZIP健全性の証明や敵対的ZIPへの防御は保証しない。
