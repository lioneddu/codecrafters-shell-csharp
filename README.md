# POSIX-like shell in C#

[![progress-banner](https://backend.codecrafters.io/progress/shell/2723d535-fda9-4607-84d1-555119aa8adf)](https://app.codecrafters.io/users/codecrafters-bot?r=2qF)
![.NET 9](https://img.shields.io/badge/9.0-512BD4?logo=dotnet&logoColor=white)

An interactive shell written from scratch in C#: it parses command lines, runs builtins and external programs, connects them with pipes and redirections, and manages background jobs.
Built as part of the [CodeCrafters "Build Your Own Shell"](https://app.codecrafters.io/courses/shell/overview) challenge.

## Features

| Area | Supported |
|---|---|
| Builtins | `echo`, `exit`, `type`, `pwd`, `cd` (absolute, relative, `~`), `history`, `jobs`, `declare`, `complete` |
| External programs | Lookup through `PATH`, argument passing, `command not found` handling |
| Quoting & expansion | Single quotes, double quotes, backslash escapes, `$VAR` and `${VAR}` expansion |
| Pipelines | `cmd1 \| cmd2 \| cmd3`, mixing builtins and external programs |
| Redirection | `>`, `1>`, `>>`, `1>>`, `2>`, `2>>` |
| Background jobs | `cmd &`, job IDs, `jobs`, completion notices (`Done`) |
| History | In-memory history, `history -r / -w / -a`, persistence through `HISTFILE` |
| Line editing | Raw terminal mode, Tab completion for builtins and executables (longest common prefix, candidate list on double Tab), custom completions with `complete -C` |

## How it works

```
read line  →  tokenize (quotes, escapes, variables)  →  split pipeline stages
          →  resolve each stage (builtin or PATH lookup)  →  wire stdin/stdout + redirections
          →  run in foreground or as a background job
```

- **Line editor**: the terminal is switched to raw mode with `stty`, so the shell reads one key at a time and can handle Tab completion and redraw the prompt itself.
- **Tokenizer**: a single-pass state machine that tracks whether it is inside single or double quotes, which escapes are valid in each context, and when a `$` starts a variable.
- **Pipelines**: external stages are started as `Process` instances and their streams are connected; builtins inside a pipeline have their output captured and forwarded to the next stage.
- **Background jobs**: a `BackgroundJobManager` assigns job IDs, tracks running processes and reports finished ones before the next prompt, like bash does.

## Running locally

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download) and a Unix-like terminal (Linux, macOS or WSL): raw mode relies on `stty`.

```sh
./your_program.sh
$ echo "hello $USER" | tr a-z A-Z
$ ls -la > files.txt
$ sleep 5 &
$ jobs
```

## What I learned
- How a shell reads, parses, and runs commands (it’s not magic!)
- What a REPL is and how it works under the hood
- How commands like echo run, vs. external ones (like cursor)
- What $PATH means and its role in identifying executable files
- How running commands require spawning OS processes and exits
- How a shell actually parses quotes and escapes (the rules are subtler than they look)
