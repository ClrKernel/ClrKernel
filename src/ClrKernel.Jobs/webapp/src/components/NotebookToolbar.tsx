import { MoreHorizontal, Play, RotateCcw } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Badge } from '@/components/ui/badge';
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
import { kernelLabel, showsExecution, toolbarLayout } from '../notebookToolbar';

const RESTART_HINT =
  'Kills the kernel. This is also the only way to stop a cell that will not finish.';

/** Re-renders on resize so the toolbar can shed detail rather than wrap. */
function useWindowWidth(): number {
  const [width, setWidth] = useState(() => window.innerWidth);
  useEffect(() => {
    const onResize = () => setWidth(window.innerWidth);
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, []);
  return width;
}

/**
 * shadcn's default segmented list: a muted track with the active tab lifted out
 * of it. The active pill is `--card`, not `--background` — in this palette the
 * page is grey and the raised surface is white, which is the opposite way round
 * from stock shadcn, where `--background` is the white one.
 *
 * Neutral, not accent: the accent is reserved for the primary action, and
 * `Promote to production` is the only accent-filled control on this page.
 */
const TAB = 'px-3 data-active:bg-card data-active:text-foreground';

/**
 * `bg-muted` is nearly the toolbar's own background, so the default selected
 * state is not visible here. A tinted foreground fill reads at a glance and
 * stays neutral — the accent belongs to the primary action.
 */
const TOGGLE =
  'text-muted-foreground data-[state=on]:bg-foreground/10 data-[state=on]:font-medium ' +
  'data-[state=on]:text-foreground';

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
export function NotebookToolbar(props: NotebookToolbarProps) {
  const layout = toolbarLayout(useWindowWidth());
  const execution = showsExecution(props.tab) && props.canRun;
  const kernel = kernelLabel(props.session, props.running, layout.showKernelVersion);

  const runAll = (
    <Button
      variant="outline"
      size="sm"
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
      size="sm"
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
    <div className="nb-toolbar sticky top-0 z-20 flex h-[48px] items-stretch gap-2 border-b border-border bg-muted px-6">
      <Tabs value={props.tab} onValueChange={props.onTab} className="h-full">
        <TabsList variant="line" className="h-full! gap-1 bg-transparent p-0">
          {props.isNotebook && <TabsTrigger value="notebook" className={TAB}>Notebook</TabsTrigger>}
          <TabsTrigger value="source" className={TAB}>
            Source
          </TabsTrigger>
          <TabsTrigger value="diff" className={TAB}>
            Diff vs production
          </TabsTrigger>
        </TabsList>
      </Tabs>

      <div className="flex-1" />


      {execution && (
        <div className="flex items-center gap-2">
          {/* Information before controls: you read what the kernel is doing
              before you reach the buttons that change it. */}
          <Badge variant="secondary" className="gap-1.5 font-mono text-xs font-normal">
            <span
              aria-hidden="true"
              className={`size-1.5 shrink-0 rounded-full ${DOT[kernel.state]}`}
            />
            {kernel.text}
          </Badge>

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
                <Button variant="outline" size="sm" aria-label="Execution controls">
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
        size="sm"
        onClick={props.onSave}
        disabled={props.busy || !props.dirty}
      >
        {props.dirty ? 'Save' : 'Saved'}
      </Button>
      <Button
        size="sm"
        onClick={props.onPromote}
        disabled={props.busy || !props.promotion?.eligible}
        title={props.promotion?.eligible ? 'Ship to production' : props.promotion?.reasons.join('\n')}
      >
        {props.promotion?.isDeletion ? 'Promote deletion' : 'Promote to production'}
      </Button>
      </div>
    </div>
  );
}
