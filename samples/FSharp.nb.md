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

## Sharing with C#

F# and C# are two compilers over two sessions, so a `let` is not automatically a
`var`. `#!share` hands a value across — the same directive a Polyglot notebook
uses, so a migrated `.dib` keeps working. In an F# cell, pull from C#:

```csharp
var threshold = 4;
var names = new List<string> { "ada", "bo" };
```

```fsharp
#!share --from csharp threshold
#!share --from csharp names --as people
people |> Seq.map (fun s -> s.ToUpper()) |> String.concat ", "
```

And in a C# cell, pull from F#. The value keeps its runtime type — an F# list is
an `IEnumerable<int>` to C# — so members complete:

```csharp
#!share --from fsharp numbers --as xs
xs.Where(x => x > threshold).Sum()
```

It is a copy of the reference, not a live link: rebinding on one side does not
move the other. `#r "nuget: …"` inside an F# cell is not wired up yet; put a
package's assembly on disk and `#r` its path.

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
