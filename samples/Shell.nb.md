# Shell cells

Set a cell's language to **Shell Script** (or start it with `#!bash`, `#!zsh`, or
`#!sh`) to run shell commands. Each cell is a fresh process, but the session keeps
notebook semantics: the **working directory and exported environment persist
across cells**, so `cd` and `export` behave the way you'd expect in one terminal.

```bash
echo "hello from $0"
uname -a
```

## Colour

The session advertises a colour-capable terminal (`TERM=xterm-256color`,
`CLICOLOR_FORCE=1`, `FORCE_COLOR=1`) even though output is captured through a
pipe, so tools that colour by convention keep doing it — and the ANSI escapes
render as real colour in the cell output:

```bash
printf '\033[31mred\033[0m \033[1;32mbold green\033[0m \033[44mon blue\033[0m\n'
ls --color=force 2>/dev/null || ls -G
```

## State persists across cells

```bash
cd /tmp
export GREETING="carried over"
```

```bash
pwd
echo "$GREETING"
```

## Other shells

A `zsh` or `sh` fence keeps its shell via the selector line:

```zsh
#!zsh
echo "running under zsh $ZSH_VERSION"
```

```sh
#!sh
echo "plain POSIX sh"
```

## Remote cells over SSH

Register a target once — auth is your ssh keys/agent/`~/.ssh/config`
(`BatchMode`, so a missing key fails fast instead of prompting; passwords are
deliberately unsupported) — then point any shell cell at it with
`--connection`:

```bash
#!shell-connect --name web01 --host web01.example.com --user deploy
```

```bash
#!bash --connection web01
hostname
uptime
```

The **remote working directory persists per target** (`cd` carries to the next
remote cell); exported environment does not — each remote cell is a fresh
login. Colour is forced on the remote end too.

**Windows targets work.** If the box doesn't have the shell you asked for, the
session auto-detects what it does have (bash → sh → pwsh → powershell, cached
per target) — so `#!bash --connection winbox` against a Windows OpenSSH server
runs your commands in PowerShell. Write commands the target understands; pin
the shell explicitly with `--remote-shell powershell` (or in the config node as
`"remoteShell"`) if you don't want detection. Targets can also live in
`connections.json` (or your git-ignored `connections.local.json`):

```json
{
  "web01": { "$type": "Ssh", "host": "web01.example.com", "user": "deploy", "port": "22" }
}
```

## Errors

stderr is captured inline (in order), and a non-zero exit fails the cell with
the exit code — the output before the failure is still shown:

```bash
echo "this prints"
ls /definitely/not/a/path
```
