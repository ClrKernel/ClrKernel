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

## Errors

stderr is captured inline (in order), and a non-zero exit fails the cell with
the exit code — the output before the failure is still shown:

```bash
echo "this prints"
ls /definitely/not/a/path
```
