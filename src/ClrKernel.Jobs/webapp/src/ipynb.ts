// Pure helpers for rendering an executed .ipynb: normalising nbformat's
// string-or-array sources, picking an output's best representation, and turning
// ANSI colour codes into styled spans. Kept free of React so they can be tested
// directly — this is the only part of the SPA with real logic.

export interface NotebookCell {
  cell_type: 'code' | 'markdown' | string;
  source: string | string[];
  execution_count?: number | null;
  metadata?: { tags?: string[] };
  outputs?: NotebookOutput[];
}

export interface NotebookOutput {
  output_type: 'stream' | 'execute_result' | 'display_data' | 'error' | string;
  name?: string;
  text?: string | string[];
  data?: Record<string, string | string[]>;
  ename?: string;
  evalue?: string;
  traceback?: string[];
}

export interface Notebook {
  cells?: NotebookCell[];
}

/** nbformat allows a string or an array of lines; normalise to one string. */
export function joinSource(source: string | string[] | undefined): string {
  if (source == null) {
    return '';
  }
  return Array.isArray(source) ? source.join('') : source;
}

export function isInjectedParameters(cell: NotebookCell): boolean {
  return cell.metadata?.tags?.includes('injected-parameters') ?? false;
}

export type RenderedOutput =
  | { kind: 'html'; html: string }
  | { kind: 'text'; text: string }
  | { kind: 'error'; ename: string; evalue: string; traceback: string };

/**
 * Picks how to render one output. HTML wins when present (the kernel's formatters
 * emit rich tables and badges); otherwise plain text. Callers must sanitise the
 * html before injecting it.
 */
export function renderOutput(output: NotebookOutput): RenderedOutput | null {
  if (output.output_type === 'error') {
    return {
      kind: 'error',
      ename: output.ename ?? 'Error',
      evalue: output.evalue ?? '',
      traceback: (output.traceback ?? []).join('\n'),
    };
  }

  if (output.output_type === 'stream') {
    return { kind: 'text', text: joinSource(output.text) };
  }

  const data = output.data;
  if (!data) {
    return null;
  }
  if (data['text/html'] != null) {
    return { kind: 'html', html: joinSource(data['text/html']) };
  }
  if (data['text/plain'] != null) {
    return { kind: 'text', text: joinSource(data['text/plain']) };
  }
  // An image or another mime type we don't render inline yet.
  const [first] = Object.keys(data);
  return first ? { kind: 'text', text: `[${first}]` } : null;
}

export interface AnsiSpan {
  text: string;
  className?: string;
}

// The 8 standard foreground colours plus bold; enough for the kernel's ANSI
// output. Anything else is dropped rather than shown as garbage.
const ANSI_CLASSES: Record<number, string> = {
  1: 'ansi-bold',
  30: 'ansi-black',
  31: 'ansi-red',
  32: 'ansi-green',
  33: 'ansi-yellow',
  34: 'ansi-blue',
  35: 'ansi-magenta',
  36: 'ansi-cyan',
  37: 'ansi-white',
  90: 'ansi-bright-black',
  91: 'ansi-red',
  92: 'ansi-green',
  93: 'ansi-yellow',
  94: 'ansi-blue',
  95: 'ansi-magenta',
  96: 'ansi-cyan',
};

const ANSI_PATTERN = /\u001b\[([0-9;]*)m/g;

/** Splits text carrying ANSI SGR codes into spans with class names. */
export function parseAnsi(text: string): AnsiSpan[] {
  const spans: AnsiSpan[] = [];
  let classes: string[] = [];
  let lastIndex = 0;

  ANSI_PATTERN.lastIndex = 0;
  let match: RegExpExecArray | null;
  while ((match = ANSI_PATTERN.exec(text)) !== null) {
    if (match.index > lastIndex) {
      spans.push({
        text: text.slice(lastIndex, match.index),
        className: classes.length ? classes.join(' ') : undefined,
      });
    }
    for (const part of match[1].split(';')) {
      const code = Number(part || '0');
      if (code === 0) {
        classes = [];
      } else if (ANSI_CLASSES[code] && !classes.includes(ANSI_CLASSES[code])) {
        classes.push(ANSI_CLASSES[code]);
      }
    }
    lastIndex = match.index + match[0].length;
  }

  if (lastIndex < text.length) {
    spans.push({
      text: text.slice(lastIndex),
      className: classes.length ? classes.join(' ') : undefined,
    });
  }
  return spans;
}

/** Short relative time for run lists ("3m ago"). */
export function timeAgo(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) {
    return '—';
  }
  // Timestamps are UTC; the server writes them without a zone suffix.
  const stamp = /(Z|[+-]\d\d:\d\d)$/.test(iso) ? iso : `${iso}Z`;
  const seconds = Math.max(0, Math.round((now - Date.parse(stamp)) / 1000));
  if (seconds < 60) {
    return `${seconds}s ago`;
  }
  if (seconds < 3600) {
    return `${Math.round(seconds / 60)}m ago`;
  }
  if (seconds < 86400) {
    return `${Math.round(seconds / 3600)}h ago`;
  }
  return `${Math.round(seconds / 86400)}d ago`;
}

/** Elapsed time between two timestamps, for a run or a cell. */
export function duration(start: string | null, end: string | null): string {
  if (!start || !end) {
    return '—';
  }
  const stamp = (s: string) => (/(Z|[+-]\d\d:\d\d)$/.test(s) ? s : `${s}Z`);
  const ms = Date.parse(stamp(end)) - Date.parse(stamp(start));
  if (ms < 1000) {
    return `${ms}ms`;
  }
  if (ms < 60_000) {
    return `${(ms / 1000).toFixed(1)}s`;
  }
  const minutes = Math.floor(ms / 60_000);
  return `${minutes}m ${Math.round((ms % 60_000) / 1000)}s`;
}
