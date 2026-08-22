import { Info, MoreHorizontal, Play, RotateCcw } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { Separator } from '@/components/ui/separator';
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { toast } from 'sonner';
import { kernelLabel, showsExecution, toolbarLayout } from '../notebookToolbar';

const RESTART_HINT =
  'Kills the kernel. This is also the only way to stop a cell that will not finish.';

/**
 * Re-renders on resize so the toolbar can shed detail rather than wrap.
 *
 * A ResizeObserver on the bar itself, not `window.innerWidth`: the editor's file
 * explorer sits between the two, so the window can be 250px wider than the space
 * the toolbar has — and dragging the explorer wider fires no window resize at
 * all. Starts at the viewport width so the first paint is not the narrowest
 * layout.
 */
function useBarWidth(ref: React.RefObject<HTMLElement | null>): number {
  const [width, setWidth] = useState(() => window.innerWidth);
  useEffect(() => {
    const node = ref.current;
    if (node == null) {
      return;
    }
    const observer = new ResizeObserver(([entry]) => setWidth(entry.contentRect.width));
    observer.observe(node);
    return () => observer.disconnect();
  }, [ref]);
  return width;
}

/**
 * The segmented Normal|Focus control. `data-[state=on]` is the *selected* half,
 * and it takes the accent fill: this is a view switch, so which half is live has
 * to be unmistakable at a glance.
 */
const TOGGLE =
  'text-muted-foreground data-[state=on]:bg-primary data-[state=on]:font-medium ' +
  'data-[state=on]:text-primary-foreground';

const DOT: Record<string, string> = {
  running: 'bg-status-running animate-pulse',
  idle: 'bg-status-success',
  stopped: 'bg-status-idle',
};

export interface NotebookToolbarProps {
  tab: string;
  onTab: (tab: string) => void;
  /** Only a .nb.md has a Notebook tab; anything else opens straight to Source. */
  isNotebook: boolean;
  canRun: boolean;
  running: boolean;
  session: { started?: boolean; kernel?: string | null; version?: string | null } | null;
  mode: 'normal' | 'focus';
  onMode: (mode: 'normal' | 'focus') => void;
  onRunAll: () => void;
  onRestart: () => void;
  dirty: boolean;
  busy: boolean;
  onSave: () => void;
  onPromote: () => void;
  promotion: { eligible: boolean; isDeletion?: boolean; reasons: string[] } | null;
}

/**
 * One row: tabs on the left, everything you can do on the right.
 *
 * The old page spent three rows on chrome — a header, a tab row and a controls
 * row. The top bar says where you are; this says what you can do here, and it
 * never wraps: as the window narrows it drops Restart's label, then Run All's
 * label and the kernel version, then folds the execution controls into a menu.
 */
/**
 * Why promotion is blocked, as a toast rather than a banner.
 *
 * It auto-dismisses, but carries an explicit Dismiss too: the reasons can run to
 * several lines, and a notice that vanishes mid-sentence while you are reading
 * it is worse than one you have to close. `id` is fixed so hammering the button
 * updates the one toast instead of stacking identical copies.
 */
function explainBlocked(reasons: string[]): void {
  toast.warning('Not promotable yet', {
    id: 'promotion-blocked',
    duration: 8000,
    description: (
      <ul className="mt-1 list-disc space-y-0.5 pl-4">
        {reasons.map((reason) => (
          <li key={reason}>{reason}</li>
        ))}
      </ul>
    ),
    action: { label: 'Dismiss', onClick: () => toast.dismiss('promotion-blocked') },
  });
}

export function NotebookToolbar(props: NotebookToolbarProps) {
  const bar = useRef<HTMLDivElement>(null);
  const layout = toolbarLayout(useBarWidth(bar));
  const execution = showsExecution(props.tab) && props.canRun;
  const kernel = kernelLabel(props.session, props.running, layout.showKernelVersion);
  const blockedReasons =
    props.promotion && !props.promotion.eligible ? props.promotion.reasons : [];

  const runAll = (
    <Button
      variant="outline"
      size="xs"
      onClick={props.onRunAll}
      disabled={props.running}
      aria-label="Run all cells"
    >
      <Play className="size-3.5" aria-hidden="true" />
      {!layout.runAllIconOnly && 'Run All'}
    </Button>
  );

  const restart = (
    <Button
      variant="outline"
      size="xs"
      onClick={props.onRestart}
      aria-label="Restart kernel"
      title={RESTART_HINT}
    >
      <RotateCcw className="size-3.5" aria-hidden="true" />
      {!layout.restartIconOnly && 'Restart kernel'}
    </Button>
  );

  return (
    // Sticky, so Run All stays reachable while scrolling a long notebook in
    // Normal Mode. The tabs' underline sits on the row's own bottom border.
    <div ref={bar} className="nb-toolbar sticky top-0 z-20 flex h-[44px] shrink-0 items-stretch gap-1.5 overflow-x-auto whitespace-nowrap border-b border-border bg-card px-4">
      <Tabs value={props.tab} onValueChange={props.onTab} className="h-full">
        <TabsList variant="line">
          {props.isNotebook && <TabsTrigger value="notebook">Notebook</TabsTrigger>}
          <TabsTrigger value="source">Source</TabsTrigger>
          <TabsTrigger value="diff">Diff vs production</TabsTrigger>
        </TabsList>
      </Tabs>

      <div className="flex-1" />


      {execution && (
        <div className="flex items-center gap-2">
          {/* Information before controls: you read what the kernel is doing
              before you reach the buttons that change it. */}
          {layout.showKernel && (
          <span className="inline-flex items-center gap-1.5 whitespace-nowrap font-mono text-xs text-muted-subtle">
            <span
              aria-hidden="true"
              className={`size-[7px] shrink-0 rounded-full ${DOT[kernel.state]}`}
            />
            {kernel.text}
          </span>
          )}

          <ToggleGroup
            type="single"
            variant="outline"
            size="sm"
            value={props.mode}
            onValueChange={(value) => value && props.onMode(value as 'normal' | 'focus')}
            aria-label="Notebook view"
          >
            <ToggleGroupItem value="normal" className={TOGGLE}>
              Normal
            </ToggleGroupItem>
            <ToggleGroupItem
              value="focus"
              className={TOGGLE}
              title="One cell at a time, with its output below"
            >
              Focus
            </ToggleGroupItem>
          </ToggleGroup>

          <Separator orientation="vertical" className="h-5" />

          {layout.collapse ? (
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="outline" size="xs" aria-label="Execution controls">
                  <MoreHorizontal className="size-3.5" aria-hidden="true" />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuItem onSelect={props.onRunAll} disabled={props.running}>
                  <Play className="size-3.5" aria-hidden="true" />
                  Run All
                </DropdownMenuItem>
                <DropdownMenuSeparator />
                <DropdownMenuItem onSelect={props.onRestart}>
                  <RotateCcw className="size-3.5" aria-hidden="true" />
                  Restart kernel
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          ) : (
            <>
              {layout.runAllIconOnly ? (
                <Tooltip>
                  <TooltipTrigger asChild>{runAll}</TooltipTrigger>
                  <TooltipContent>Run All</TooltipContent>
                </Tooltip>
              ) : (
                runAll
              )}
              {layout.restartIconOnly ? (
                <Tooltip>
                  <TooltipTrigger asChild>{restart}</TooltipTrigger>
                  <TooltipContent>Restart kernel — stops anything running</TooltipContent>
                </Tooltip>
              ) : (
                restart
              )}
            </>
          )}

          <Separator orientation="vertical" className="h-5" />
        </div>
      )}

      {/* Document-level, so these stay on every tab and never collapse. */}
      <div className="flex items-center gap-2">
      <Button
        variant="secondary"
        size="xs"
        onClick={props.onSave}
        disabled={props.busy || !props.dirty}
      >
        {props.dirty ? 'Save' : 'Saved'}
      </Button>
      <Button
        size="xs"
        onClick={props.onPromote}
        disabled={props.busy || !props.promotion?.eligible}
        title={props.promotion?.eligible ? 'Ship to production' : undefined}
      >
        {props.promotion?.isDeletion
          ? 'Promote deletion'
          : layout.shortPromote
            ? 'Promote'
            : 'Promote to production'}
      </Button>
      {/* Beside the button, not inside it: a disabled button swallows clicks,
          and this control has to stay clickable precisely when Promote is not.
          Why you cannot promote is a question you ask occasionally, so it is a
          thing you reach for — not a banner sitting above the notebook forever. */}
      {blockedReasons.length > 0 && (
        <Button
          variant="ghost"
          size="icon-sm"
          aria-label="Why can’t I promote?"
          title="Why can’t I promote?"
          onClick={() => explainBlocked(blockedReasons)}
        >
          <Info className="size-3.5" aria-hidden="true" />
        </Button>
      )}
      </div>
    </div>
  );
}
