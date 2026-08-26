import { useEffect, useRef, useState } from 'react';
import type { Accent, ThemeName } from '../theme/palette';
import { useAccent } from '../theme/accent';
import { useTheme } from '../theme/theme';
import { FONT_MONO, FONT_SANS, paletteFor } from '../theme/palette';

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

  const accent = useAccent();
  const theme = useTheme();
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
      srcDoc={document_(html, token.current, accent, theme)}
    />
  );
}

/**
 * The kernel writes its HTML for VS Code, so it reads `--vscode-*` variables with
 * fallbacks. Mapping them onto this app's palette is what makes the output look
 * like part of the page rather than a window into another program.
 *
 * The frame is a separate document and cannot see the app's tokens, so the
 * values are interpolated from the same palette module instead of being a second
 * hand-written copy. It follows the *app's* theme rather than the OS: a frame
 * that read `prefers-color-scheme` itself would render dark output inside a
 * light page for anyone who had overridden the OS.
 *
 * This is also why dark mode needed no change in the kernel. The formatters
 * write their HTML against `--vscode-*` variables with fallbacks, and this is
 * where those variables get their values — so a dark grid is a matter of passing
 * different ones, not of touching a single line of C#.
 */
function document_(html: string, token: string, accent: Accent, theme: ThemeName): string {
  const { neutral, grid } = paletteFor(theme);
  return `<!doctype html>
<html><head><meta charset="utf-8"><style>
:root {
  color-scheme: ${theme};
  --vscode-font-family: ${FONT_SANS};
  --vscode-font-size: 13px;
  --vscode-editor-font-family: ${FONT_MONO};
  --vscode-foreground: ${neutral.foreground};
  --vscode-editor-background: ${neutral.card};
  /* The grid's sticky header and filter row read this one. A step darker than
     the panel colour so a scrolled header stays distinct from the cell chrome
     around it. */
  --vscode-editorWidget-background: ${grid.header};
  --vscode-panel-border: ${neutral.border};
  --vscode-input-background: ${neutral.card};
  --vscode-input-foreground: ${neutral.foreground};
  --vscode-input-border: ${neutral.border};
  --vscode-button-background: ${accent.primary};
  --vscode-button-foreground: ${accent.primaryForeground};
  --vscode-button-secondaryBackground: ${neutral.panel};
  --vscode-button-secondaryForeground: ${neutral.foreground};
  --vscode-list-hoverBackground: ${neutral.hover};
  --vscode-toolbar-hoverBackground: ${neutral.panel};
  --vscode-textLink-foreground: ${accent.primary};
  --vscode-descriptionForeground: ${neutral.mutedForeground};
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
