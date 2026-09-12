# F# in ClrKernel

An `fsharp` cell runs in one F# Interactive session per notebook, so a binding in
one cell is there in the next — the same session model as `#!pwsh` and `#!python`.
A cell ending on an expression shows it; a cell ending on a binding shows nothing.

```fsharp
let square x = x * x
let numbers = [1 .. 10]
```

```fsharp
numbers |> List.map square |> List.sum
```

`printfn` output streams as the cell runs, the way `Console.WriteLine` does in C#:

```fsharp
for n in numbers |> List.filter (fun n -> n % 2 = 0) do
    printfn "%d is even, squared %d" n (square n)
```

## Records and sequences render as tables

A list of records is a grid, one column per field — the same renderer every
language's tabular result goes through.

```fsharp
type Sale = { Region: string; Quarter: int; Revenue: decimal }

[ { Region = "EMEA"; Quarter = 1; Revenue = 120_000m }
  { Region = "EMEA"; Quarter = 2; Revenue = 135_500m }
  { Region = "APAC"; Quarter = 1; Revenue = 98_250m } ]
```

## What it is not

F# and C# cells are two compilers over two sessions: a `let` here is not visible
to a `var` there, and vice versa. Hand data across through a file or a database
if a notebook needs both. `#r "nuget: …"` inside an F# cell is not wired up yet;
put a package's assembly on disk and `#r` its path.

## Errors

A cell that does not compile reports fsi's own diagnostic, with the line and
column, and the session carries on — the next cell still sees every earlier
binding.

```fsharp
let n: int = "not a number"
```

```fsharp
square 12
```
