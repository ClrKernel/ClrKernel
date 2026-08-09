# DAX cells in ClrKernel

Set a cell's language to **DAX** (or start it with `#!dax`) to run DAX queries
against a cube — on-prem SSAS, Azure Analysis Services, or a Microsoft Fabric /
Power BI semantic model. Results render as an interactive grid. The `#!dax-*`
magics, their flags, cube names, and DAX keywords/functions all autocomplete.

## Connect a cube

Define one or more cubes with `#!dax-connect`; the first (or `--default`) becomes
the default cube for `#!dax` cells.

```dax
#!dax-connect --name analytics --server ssas.db.local --database DataWarehouse --default
#!dax-connect --name sales --fabric --workspace "Analytics WS" --model "Sales Model"
```

`--fabric --workspace W --model M` connects to a Fabric / Power BI semantic model
with Entra auth. `--azure-as` targets Azure Analysis Services. For a SQL/basic
login, use `--user svc --secret <env-var>` (the password is read from an
environment variable, never the notebook). `--connection-string "..."` is the
advanced escape hatch.

## Query the default cube

```dax
EVALUATE
TOPN(
    100,
    SUMMARIZECOLUMNS(
        'Date'[Year],
        "Revenue", [Total Sales]
    ),
    [Revenue], DESC
)
```

## Target a specific cube

A cell runs against the default cube, or one named with a leading
`-- connections <name>` comment (valid DAX), which is what the completion offers:

```dax
-- connections sales
EVALUATE ROW("Total", [Total Sales])
```

## From C#

The same models are reachable from C# cells via `Ssas` (ad-hoc, outside the
`#!dax` cube registry):

```csharp
var cube = Ssas.Connect("ssas.db.local", "DataWarehouse");
cube.Query("EVALUATE VALUES('Product'[Category])");
```
