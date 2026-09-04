# SheetJS (Community Edition) 0.20.3

Vendored, not installed. The npm package `xlsx` stopped at **0.18.5** and the
advisories against it (prototype pollution, ReDoS) are fixed only in 0.19.3+,
which is published to <https://cdn.sheetjs.com> and not to npm. Taking the
current version therefore means taking the file.

```
https://cdn.sheetjs.com/xlsx-0.20.3/package/xlsx.mjs
sha256  1a0fb062ee9781b13f6687371b202aaefc53b6ce55b530c027e01f9c087b77db

https://cdn.sheetjs.com/xlsx-0.20.3/package/types/index.d.ts  ->  xlsx.d.mts
sha256  191e4e6aceae3602aa3a1e9a6bc0e98821d6d5fb787e2bc16e250439482bddb6
```

Apache-2.0; the licence is beside this file.

To update: fetch the new `xlsx.mjs`, its types and `LICENSE`, record the version and hash
here, and re-run the webapp tests — `sheet.test.ts` reads a workbook written by
openpyxl, so a regression in the reader fails rather than passing quietly.

Imported through a dynamic `import()` so it lands in its own chunk: it is a
megabyte, and most sessions never open a spreadsheet.
