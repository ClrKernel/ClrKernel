# SQL pipelines & deployment in ClrKernel

Turn a notebook of SQL steps into a dependency-aware ETL job, and deploy database
definitions idempotently. Both build on the named connections from `#!sql`.

## Connect

```sql
#!sql-connect --name source --server sql-src --database app --auth integrated --default
#!sql-connect --name warehouse --server sql-dw --database dw --auth sql --user etl
```

## Define steps

Annotate a SQL cell with `-- step <name>` to make it a pipeline step, and
`-- needs <a, b>` to declare what must finish first. Running a step cell
**registers** it (it doesn't execute yet). A step's body can be plain SQL or a
`#!sql-merge` / `#!sql-bulk` magic.

`-- step`, `-- needs`, and `-- connections` all autocomplete (Ctrl+Space), and
`-- needs` completes the names of steps you've defined in other cells — so you
don't have to remember them.

```sql
-- step extract_customers
-- connections source
SELECT * INTO warehouse..stg_Customers FROM dbo.Customers;
```
```sql
-- step extract_orders
-- connections source
SELECT * INTO warehouse..stg_Orders FROM dbo.Orders;
```
```sql
-- step load_dim_customer
-- needs extract_customers
#!sql-merge --connection warehouse --target dim.Customer --source stg_Customers --on Id
```
```sql
-- step load_fact_orders
-- needs extract_orders, load_dim_customer
#!sql-merge --connection warehouse --target fact.Orders --source stg_Orders --on OrderId
```

## Run the pipeline

`#!sql-run` builds the DAG and runs it: `extract_customers` and `extract_orders`
run **in parallel** (nothing depends on the other), `load_dim_customer` waits for
its extract, and `load_fact_orders` waits for *both* its dependencies. A live
status board shows each step as pending → running → done (or failed/skipped). If
a step fails, everything downstream is skipped and independent branches still
finish.

```sql
#!sql-run
```

Run a subset (with its upstream dependencies) while iterating:

```sql
#!sql-run --select load_fact_orders --max-parallel 8
```

## Deploy definitions

Point `#!sql-deploy` at a folder of `.sql` files. Programmable objects (procs,
views, functions, triggers) are rewritten to `CREATE OR ALTER`, so re-running is
safe. Files run in filename order (use numeric prefixes), and any that fail
because a referenced object isn't there yet are retried in later passes — so
cross-file dependencies resolve without you ordering everything by hand.

```sql
#!sql-deploy --connection warehouse --path ./database/definitions --recurse
```

Preview without executing:

```sql
#!sql-deploy --connection warehouse --path ./database/definitions --dry-run
```

## From C#

The same operations are available in `#!csharp` cells:

```csharp
var deploy = SqlServer.Deploy("warehouse", new DeployOptions { Path = "./database/definitions", Recurse = true });
deploy   // e.g. "5 definition file(s) deployed."
```
