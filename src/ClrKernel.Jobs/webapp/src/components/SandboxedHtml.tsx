import { useEffect, useRef, useState } from 'react';

/**
 * Kernel output that needs its own scripts to run — the interactive grid builds
 * its rows from an embedded JSON payload, so sanitising the script away leaves a
 * toolbar and nothing else, which is exactly what DisplayTable looked like.
 *
 * It runs in an iframe with `allow-scripts` and deliberately *without*
 * `allow-same-origin`: the frame gets an opaque origin, so the script can render
 * itself but cannot reach this page's DOM, cookies, or storage. That is what
 * makes running kernel-authored script safe enough to be worth it.
 *
 * VS Code renders notebook output in an iframe for the same reason, which is why
 * the same HTML has always worked there and not here.
 */
export function SandboxedHtml({ html }: { html: string }) {
  const frame = useRef<HTMLIFrameElement | null>(null);
  const [height, setHeight] = useState(120);
  // Identifies this frame's height messages. Frames are same-page siblings and
  // every one of them posts to the same window.
  const token = useRef(`ck-${Math.random().toString(36).slice(2)}`);

  useEffect(() => {
    function onMessage(event: MessageEvent) {
      const data = event.data;
      if (data?.token === token.current && typeof data.height === 'number') {
        // A little slack: the reported height excludes sub-pixel rounding and
        // any margin the output's own styles add at the bottom.
        setHeight(Math.min(Math.max(data.height + 8, 60), 900));
      }
    }
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, []);

  return (
    <iframe
      ref={frame}
      className="output-frame"
      title="Notebook output"
      sandbox="allow-scripts"
      style={{ height }}
      srcDoc={document_(html, token.current)}
    />
  );
}

/**
 * The kernel writes its HTML for VS Code, so it reads `--vscode-*` variables with
 * fallbacks. Mapping them onto this app's palette is what makes the output look
 * like part of the page rather than a window into another program — including in
 * dark mode, which the frame follows on its own because it is a real document.
 */
function document_(html: string, token: string): string {
  return `<!doctype html>
<html><head><meta charset="utf-8"><style>
:root {
  color-scheme: light dark;
  --vscode-font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
  --vscode-font-size: 13px;
  --vscode-foreground: #1c1f24;
  --vscode-editor-background: #ffffff;
  --vscode-editorWidget-background: #f6f7f9;
  --vscode-panel-border: #d8dce3;
  --vscode-input-background: #ffffff;
  --vscode-input-foreground: #1c1f24;
  --vscode-input-border: #d8dce3;
  --vscode-button-background: #2563eb;
  --vscode-button-foreground: #ffffff;
  --vscode-button-secondaryBackground: #eceff3;
  --vscode-button-secondaryForeground: #1c1f24;
  --vscode-list-hoverBackground: #eceff3;
  --vscode-toolbar-hoverBackground: #e2e6ec;
  --vscode-textLink-foreground: #2563eb;
  --vscode-descriptionForeground: #6b7280;
}
@media (prefers-color-scheme: dark) {
  :root {
    --vscode-foreground: #e6e8eb;
    --vscode-editor-background: #14171c;
    --vscode-editorWidget-background: #1c2027;
    --vscode-panel-border: #2c323c;
    --vscode-input-background: #14171c;
    --vscode-input-foreground: #e6e8eb;
    --vscode-input-border: #2c323c;
    --vscode-button-background: #2563eb;
    --vscode-button-secondaryBackground: #242a33;
    --vscode-button-secondaryForeground: #e6e8eb;
    --vscode-list-hoverBackground: #242a33;
    --vscode-toolbar-hoverBackground: #2c323c;
    --vscode-textLink-foreground: #60a5fa;
    --vscode-descriptionForeground: #9aa3b0;
  }
}
html, body { margin: 0; background: transparent; }
body {
  font-family: var(--vscode-font-family);
  font-size: var(--vscode-font-size);
  color: var(--vscode-foreground);
  overflow-x: auto;
}
</style></head>
<body>${html}
<script>
(function () {
  var last = 0;
  function report() {
    // The BODY, not documentElement: the root element is sized to the frame's own
    // viewport, so measuring it means the height can only ever grow — the frame
    // gets taller, the root reports taller, and the content sits in a growing gap.
    var h = document.body.scrollHeight;
    if (h !== last) {
      last = h;
      parent.postMessage({ token: ${JSON.stringify(token)}, height: h }, '*');
    }
  }
  report();
  window.addEventListener('load', report);
  // The grid re-lays out when you sort, filter or page it, and a fixed height
  // would either clip the result or leave a gap.
  if (window.ResizeObserver) { new ResizeObserver(report).observe(document.body); }
  setInterval(report, 500);
})();
</script>
</body></html>`;
}
