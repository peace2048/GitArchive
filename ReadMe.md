# GitArchive

GitのリポジトリのソースをZIPで固めて保存します。また、リモートリポジトリと同期されていないブランチを報告します。

## ZIPで保存

ソースをZIPで保存するには `git archive -o xxx.zip master` でmasterブランチをxxx.zipに出力できますが、これを自動で行います。
保存するコミットは、タグ付けされたコミットと指定されたブランチです。

タグ付けされたコミットは、`リポジトリ名-タグ名.zip` で保存されます。
ブランチは、`リポジトリ名-ブランチ名-latest.zip` で保存されます。

## 作業状態のチェック

コミットを忘れて放置状態が30日以上経過しているリポジトリを通知します。
ただ、何日前から放置されているのかの判断が難しく、このプログラムを走らせたとき変更状態に変わっていた時刻を別ファイルに保存しておき、
保存された時刻を放置開始と判断します。

通知は、ファイルに出力されます。

## 動作

設定されたフォルダー内で監視設定ファイルを見つけ、監視設定ファイルを元にZIPファイルを作成します。
監視するフォルダーは実行ファイルと同じフォルダー内のGitArchive.config.jsonで設定します。
また、作業状態チェックでコミット忘れ等を通知するファイル名もGitArchive.config.jsonで設定します。

GitArchive.config.json
```json
{
    "Archive": {
        "Folders": ["監視フォルダー", "..."],
        "NotifyFile": "通知ファイル"
    }
}
```

通知ファイルは、設定されたファイル名に時刻を追加して作成されます。
例えば notify.txt の場合、2020-1-3 3:04:06 に実行すると notify-20200102030405.txt で作成されます。


また、監視フォルダー配下のGitワーキングディレクトリ直下または.gitフォルダーに監視設定ファイル `git_archive.json` を置きます。
`git_archive.json` では、ZIPの保存先、保存するブランチを設定します。
ブランチは複数設定できます。

git_archive.json
```json
{
    "ArchiveFolder": "保存先フォルダー",
    "Branches": [
        "master"
    ]
}
```

保存先フォルダーに "-" を指定すると、ソースのZIP圧縮は行いません。

`GitArchive archive walk` コマンドをスケジューラに登録し、ログイン時に実行するようなことを想定しています。

## コマンド一覧

- GitArchive json create
- GitArchive json archive
- GitArchive json add
- GitArchive archive walk
- GitArchive archive execute

### GitArchive json create

カレントディレクトリに関し監視設定ファイル(git_archive.josn)を作成します。

    Usage: GitArchive json create <archive folder> [options...]

    Arguments:
    [0] <String>    archive folder

    Options:
    -b, -branches <String[]>     (Default: null)
    -g, -saveToGitFolder         (Optional)

引数のブランチを省略すると master ブランチがデフォルトで設定されます。

例) master と v1 ブランチを C:\Project\X に保存する。

    GitArchive json create C:\Project\X -b "master v1"

例) ZIP圧縮は行わない。設定ファイルを .git フォルダーに作成。

    GitArchive json create - -g


### GitArchive json archive

git_archive.json のZIP保存先を変更します。

    Usage: GitArchive json archive <set archive folder>

    Arguments:
    [0] <String>    set archive folder

### GitArchive json add

git_archive.json ZIP保存するブランチを追加します。

    Usage: GitArchive json add <branch>

    Arguments:
    [0] <String>    branch

ブランチを削除するときは、直接設定ファイルを編集して下さい。

### GitArchive archive walk

設定ファイルに基づいてZIPファイル作成と作業状態のチェックを行います。

    Usage: GitArchive archive walk [options...]

    Options:
    -s, -silent     (Optional)

引数に -s を付けると通知ファイルは作成されません。

### GitArchive archive execute

カレントディレクトリのZIPファイル作成と作業状態のチェックを行います。
通知ファイルは作成されませ。

    Usage: GitArchive archive execute

    Options:
    ()

git_archive.json の存在するフォルダーで実行すると、そのフォルダーを対象に実行します。
動作確認に使用してください。