# Markdown preview test

Every construct the preview is expected to render, on one page, so a change to the
rendering can be checked by looking at it. Not a notebook — a plain `.md`, which is
what Files opens at **Source** with **Preview** beside it.

Open it in Studio: copy it into your notebooks folder, click it in Files, then
Preview.

## Paragraphs and inline marks

A paragraph, and then a second one, so the gap between them is visible. A line
break in the source
does not start a new paragraph — this sentence continues the one above it, which is
CommonMark's rule and the thing that looks like a bug when the spacing is missing.

*Emphasis*, **strong**, ***both***, `inline code`, ~~struck through~~, and a
[link to the docs](https://github.com/ClrKernel/ClrKernel). A bare URL is
autolinked: https://github.com/ClrKernel/ClrKernel

Escapes: \*not emphasis\*, and a literal backtick `` ` `` inside code.

## Headings

### Third level

#### Fourth level

##### Fifth level

###### Sixth level

## Lists

- First item
- Second item, long enough to wrap so the hanging indent is visible against the
  bullet rather than running back under it
- Third item
  - Nested
  - Also nested
    - Third level

1. Numbered
2. Second
   1. Nested numbered
   2. Second nested
3. Third

- [x] A finished task
- [ ] An unfinished one
- [ ] Another, to show they line up

Term-style list with paragraphs:

- **First**

  A paragraph belonging to the first item.

- **Second**

  And one belonging to the second.

## Quotes

> A block quote, with a second sentence so the left rule runs the height of it.
>
> A second paragraph inside the quote.
>
> > And a nested quote.

## Code

Inline `var x = 1;` sits in the line. A fenced block does not:

```csharp
var greeting = "Hello from ClrKernel";
Console.WriteLine($"{greeting} at {DateTime.UtcNow:HH:mm}");
```

```sql
SELECT customer, SUM(total) AS revenue
FROM sales.order_lines
GROUP BY customer
ORDER BY revenue DESC;
```

An indented block, which is code without a fence:

    $ clrkernel run reports/monthly.nb.md
    Succeeded in 1.2s

## Tables

| Column | What it holds | Notes |
|---|---|---|
| `name` | the job's name | unique per file |
| `cron` | when it runs | empty means manual |
| `notebook` | what it runs | relative to the jobs file |

Alignment, which GitHub-flavoured markdown adds and CommonMark does not:

| Left | Centre | Right |
|:---|:---:|---:|
| a | b | 1 |
| longer cell | centred | 1000 |

A wide table, to check it scrolls inside itself rather than pushing the page
sideways:

| One | Two | Three | Four | Five | Six | Seven | Eight | Nine | Ten | Eleven | Twelve |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 |

## Rules

Above the rule.

---

Below it.

## Images

An image that does not resolve, to check it degrades to its alt text rather than
breaking the layout:

![Alt text for a missing image](./no-such-image.png)

## HTML in markdown

Raw HTML is <b>not</b> rendered — it is escaped and shown as written. That is
deliberate: a document in a repository is not a place to run markup from.

<script>alert('this must not run')</script>

## The end

If everything above reads as a document rather than as a wall of text, the preview
is doing its job.
