# HANDOFF-21 — Dark mode

*Landed 2026-08-26. Prompted by `docs/Focus-mode-thumbnails-spec.md`, whose
"changing the theme recolors the thumbnails" turned out to describe something the
app could not do: it was light-only, with Monaco pinned to one theme and a
comment saying the accent never appears inside the editor. Dark mode is the
requirement behind that criterion, so it went first.*

## The shape

Three modes — **System, Light, Dark** — with System the default. Three and not a
toggle, because "follow the OS" is a real answer and not the absence of one: a
user whose machine switches at sunset wants this to switch with it, and a user
who picked dark on a light machine wants it to stay dark. A boolean loses the
first.

`data-theme` on `<html>` always carries the **resolved** value, never the word
`system`. The token layer then needs one selector rather than a selector plus a
media query that can disagree with it — and an explicit choice can override the
OS at all, which a media query alone cannot do. The inline script in `index.html`
resolves it before first paint, beside the accent, for the same reason the accent
is there: a white flash on the way into a dark app is the thing people notice.

## Why the kernel needed no change

The formatters in `ClrKernel.Formatting.Html` write their HTML against
`--vscode-*` variables with fallbacks, and `SandboxedHtml` is where those
variables get their values. A dark grid is a matter of passing different ones.
Not a line of C# moved.

The frame follows the *app's* theme rather than the OS. One that read
`prefers-color-scheme` itself would render dark output inside a light page for
anyone who had overridden the OS.

## Where the colours live

`palette.ts` gained `DARK_*` groups beside the existing ones, and `paletteFor()`
hands out the set the two literal-only consumers need — Monaco, whose theme API
takes hex, and the output frame, which is its own document and cannot read the
app's tokens. Everything else reads the token layer.

Values were measured, not chosen. Body text is 12.7:1 on a card, the faintest
label 4.3:1 (light's equivalent manages 3.65, so dark is the better of the two),
and every accent clears 4.5:1 on its own foreground — which is why the dark
foregrounds are *dark*: a fill light enough to read as the accent against
near-black cannot carry white text.

## The test that matters most

`:root` still applies under `[data-theme='dark']`; the dark block only overrides.
So a colour token added to light and forgotten in dark does not fail, break or
warn — it silently keeps its light value, and one cream rectangle appears in a
dark app. `palette.test.ts` walks every `:root` token whose value is a colour and
requires a dark counterpart. Verified it discriminates by deleting one.

Font and radius tokens are excluded automatically by looking at the value rather
than by keeping a list of exceptions.

## Two things worth knowing

- **Monaco's theme is global**, not per-editor, so switching is one `setTheme`
  call and it re-themes editors that already exist. Both themes are built by one
  function from `paletteFor`, so the dark one cannot be missing a rule the light
  one has.
- **`--code-bg` must equal what Monaco paints** or there is a visible seam at
  every cell edge. That was already asserted for light; it is asserted for dark
  too, and checked in a browser.

## Verified

14 browser assertions on a live server: a dark OS gets a dark app before React
has run, the editor follows and matches `--code-bg`, the kernel's own HTML in its
sandboxed frame is dark, an explicit Light overrides a dark OS and survives a
reload, System follows the OS again, and the accent lightens rather than sinking
into the canvas. Plus a sweep of six pages looking for any element painting a
light surface on the dark canvas — none.

## Next

The thumbnails feature this came out of. Its acceptance criterion "changing the
theme recolors the thumbnails" is now demonstrable: `monacoThemeFor()` is
exported for the colorize cache to key on, because `monaco.editor.colorize` bakes
the colours into the HTML it returns.
