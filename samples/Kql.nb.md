# KQL cells in ClrKernel

Set a cell's language to **KQL** (or start it with `#!kql`) to query Kusto — Azure
Data Explorer, a Fabric Eventhouse / KQL database, or Log Analytics. Results
render as an interactive grid. Sign-in is Microsoft Entra; nothing here holds a
password.

## Connect a database

Register one or more databases with `#!kql-connect`; the first (or `--default`)
becomes the default for `#!kql` cells. The public help cluster works for any Entra
account, so this notebook runs as it is:

```kql
#!kql-connect --name help --cluster https://help.kusto.windows.net --database Samples --default
```

`--auth interactive` always opens a browser so you pick the account. A service
principal is `--auth clientsecret --tenant <id> --client-id <id> --secret <ref>`,
where the secret comes from the secret store or `CLRKERNEL_SECRET_<ref>` — never
the notebook. A Fabric Eventhouse's query URI (from its KQL database's settings)
goes in `--cluster` unchanged.

## Query the default database

```kql
StormEvents
| where StartTime between (datetime(2007-01-01) .. datetime(2007-12-31))
| summarize Events = count(), Damage = sum(DamageProperty) by State
| top 10 by Damage desc
```

## Target a specific database

A cell runs against the default, or one named in a leading `// connections <name>`
comment (valid KQL):

```kql
// connections help
StormEvents | take 5
```

## Management commands

A cell starting with a dot is a management command and runs as one:

```kql
.show tables
```

## From C#

The same databases are reachable from C# cells through `KustoDb`:

```csharp
var samples = KustoDb.Connect("https://help.kusto.windows.net", "Samples");
samples.Query("StormEvents | summarize count() by EventType | top 5 by count_")
```
