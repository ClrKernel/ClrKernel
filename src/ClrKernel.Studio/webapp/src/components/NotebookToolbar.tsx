import {
  ArrowDownToLine,
  Copy,
  FileOutput,
  Info,
  MoreHorizontal,
  Play,
  RotateCcw,
  TriangleAlert,
  Undo2,
  Upload,
} from 'lucide-react';
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
import type { BranchStanding } from '../api';
import { STATUS_LABEL, STATUS_TITLE, type SaveStatus } from '../autosave';
import {
  kernelLabel, promoteControl, showsExecution, toolbarLayout,
} from '../notebookToolbar';
import {
  promotionProgress, type PromotionProgress, type StepState,
} from '../promotionSteps';
import {
  useCanRun, useCanWrite, useIsProjectAdmin, useIsProjectMember,
} from '../sessionContext';

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
  /** A `.csx`: one cell, so nothing to add cells to or focus on. */
  isScript: boolean;
  /** A picture: there is no text to diff and nothing to save. */
  isImage: boolean;
  /** Only a *.jobs.yaml has an Overview tab — the form over the same file. */
  isJobsFile: boolean;
  canRun: boolean;
  running: boolean;
  session: { started?: boolean; kernel?: string | null; version?: string | null } | null;
  mode: 'normal' | 'focus';
  onMode: (mode: 'normal' | 'focus') => void;
  onRunAll: () => void;
  onRestart: () => void;
  /** There is a structural change to walk back. */
  canUndo: boolean;
  onUndo: () => void;
  saveStatus: SaveStatus;
  busy: boolean;
  /** Retry, for the one state where there is something to retry. */
  onSave: () => void;
  onPromote: () => void;
  promotion: { eligible: boolean; isDeletion?: boolean; reasons: string[] } | null;
  /** Where your own branch stands against test. */
  standing: BranchStanding | null;
  onPush: (message: string) => void;
  /** Create-or-open the paired `*.jobs.yaml`. Absent for a file that is not a notebook. */
  onSchedule?: () => void;
  onUpdate: () => void;
  /** Which branch is open — `mine`, `test`, `prod`, or `user-<id>`. */
  branch: string;
  /** False for a file Files lists but nobody may write — a `.txt`, a plain yaml. */
  fileEditable: boolean;
  /** Copies what is on screen onto your own branch and opens it there. */
  onCopyToMine: () => void;
  /** Writes a copy at a path you pick, and opens that. */
  onSaveAs: () => void;
  /** Renames it, or moves it into another folder. */
  onMove: () => void;
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
 * Background a toolbar button can explain, on request.
 *
 * Each of these used to be a permanent banner — one above the notebook, one
 * under it. Neither changes while you work, so both cost a strip of the screen
 * to repeat themselves every time you scrolled past. As a toast they answer the
 * question when it is actually asked.
 *
 * They auto-dismiss but carry an explicit Dismiss too: the text runs to several
 * lines, and a notice that vanishes mid-sentence is worse than one you close. A
 * fixed `id` per topic means hammering the button updates the one toast rather
 * than stacking identical copies.
 */
function notice(
  id: string,
  kind: 'warning' | 'info',
  title: string,
  body: React.ReactNode,
): void {
  toast[kind](title, {
    id,
    duration: 8000,
    description: body,
    action: { label: 'Dismiss', onClick: () => toast.dismiss(id) },
  });
}

/** The ⓘ that opens one. Ghost, so it reads as an aside to the button it follows. */
function InfoTip({ label, onOpen }: { label: string; onOpen: () => void }) {
  return (
    <Button variant="ghost" size="icon-sm" aria-label={label} title={label} onClick={onOpen}>
      <Info className="size-3.5" aria-hidden="true" />
    </Button>
  );
}

const STEP_MARK: Record<StepState, string> = { done: '✓', current: '→', todo: '·' };

/**
 * The steps left, not the complaints made. A list of refusals told the user what
 * was wrong with the state and never what to do about it, which is how somebody
 * ends up pushing, adding a job and running it in the wrong order and concluding
 * the whole thing is broken.
 */
function explainBlocked(progress: PromotionProgress): void {
  notice(
    'promotion-blocked',
    'warning',
    'Getting this to production',
    <>
      <ol className="mt-1 space-y-1">
        {progress.steps.map((step) => (
          <li
            key={step.label}
            className={step.state === 'current'
              ? 'font-semibold'
              : step.state === 'done' ? 'text-muted-subtle' : undefined}
          >
            <span aria-hidden="true" className="mr-1.5">{STEP_MARK[step.state]}</span>
            {step.label}
            {step.detail && (
              <span className="block pl-4 text-sm font-normal text-muted-foreground">
                {step.detail}
              </span>
            )}
          </li>
        ))}
      </ol>
      {progress.warning && <p className="mt-2">{progress.warning}</p>}
    </>,
  );
}

function explainSaving(): void {
  notice(
    'saving',
    'info',
    'You are editing your own branch',
    <p className="mt-1">
      Saving writes the file to your branch — nobody else sees it and nothing runs from it on a
      schedule. <strong>Push to test</strong> is the commit: everything you have saved becomes one
      commit on test, under a message you write. Cells you run here execute in a warm kernel that
      is dropped after 30 idle minutes; those runs never appear in run history and never count
      towards promotion. Promotion unlocks when every job on this notebook has a clean green run in
      test of exactly this content.
    </p>,
  );
}

/**
 * Where the buffer stands, as a word rather than a button.
 *
 * There is nothing to press: the editor writes to your branch as you work. The
 * one state that is actionable is a failed write, and that one is a button —
 * everything else is a label, and a label that looks pressable is a lie.
 */
function SaveStatusChip({ status, onRetry }: { status: SaveStatus; onRetry: () => void }) {
  if (status === 'failed') {
    return (
      <Button variant="outline" size="xs" onClick={onRetry} title={STATUS_TITLE.failed}>
        <TriangleAlert className="size-3.5 text-status-error" aria-hidden="true" />
        {STATUS_LABEL.failed}
      </Button>
    );
  }
  return (
    <span
      title={STATUS_TITLE[status]}
      aria-live="polite"
      // A fixed width so the row does not shuffle every time the word changes.
      className={`inline-block w-[58px] shrink-0 text-right text-xs ${
        status === 'saved' ? 'text-muted-subtle' : 'text-muted-foreground'
      }`}
    >
      {STATUS_LABEL[status]}
    </span>
  );
}

/**
 * Push to test, and the state that says whether it is worth offering.
 *
 * A single button rather than a dialog: the message is the only thing to collect,
 * and a prompt for one line is a modal for one line. `behind` swaps it for the
 * update it is blocked on, because pushing over somebody else's work is the case
 * the server refuses anyway.
 */
function PushControl({
  standing,
  busy,
  onPush,
  onUpdate,
}: {
  standing: BranchStanding | null;
  busy: boolean;
  onPush: (message: string) => void;
  /** Create-or-open the paired `*.jobs.yaml`. Absent for a file that is not a notebook. */
  onSchedule?: () => void;
  onUpdate: () => void;
}) {
  const [message, setMessage] = useState('');
  const [open, setOpen] = useState(false);

  if (standing?.hasBranch !== true) {
    return null;
  }
  const conflicts = standing.conflicts ?? [];
  const behind = standing.behind ?? 0;
  const pending = (standing.ahead ?? 0) > 0 || standing.dirty === true;

  if (conflicts.length > 0) {
    return (
      <Button
        variant="outline"
        size="xs"
        onClick={() =>
          notice('conflicts', 'warning', 'Resolve these first', (
            <ul className="mt-1 list-disc space-y-0.5 pl-4">
              {conflicts.map((file) => (
                <li key={file}>{file}</li>
              ))}
            </ul>
          ))
        }
      >
        {conflicts.length} conflicted
      </Button>
    );
  }

  if (behind > 0) {
    return (
      <Button variant="outline" size="xs" disabled={busy} onClick={onUpdate}>
        <ArrowDownToLine className="size-3.5" aria-hidden="true" />
        Update from test
      </Button>
    );
  }

  if (!pending) {
    return null;
  }

  return open ? (
    <span className="flex items-center gap-1">
      <input
        autoFocus
        value={message}
        onChange={(e) => setMessage(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && message.trim()) {
            onPush(message.trim());
            setMessage('');
            setOpen(false);
          }
          if (e.key === 'Escape') {
            setOpen(false);
          }
        }}
        placeholder="What did you change?"
        aria-label="Push message"
        className="h-6 w-[190px] rounded-md border border-input bg-background px-2 text-xs text-foreground outline-none focus:border-ring"
      />
      <Button
        size="xs"
        disabled={busy || !message.trim()}
        onClick={() => {
          onPush(message.trim());
          setMessage('');
          setOpen(false);
        }}
      >
        Push
      </Button>
    </span>
  ) : (
    <Button variant="outline" size="xs" disabled={busy} onClick={() => setOpen(true)}>
      <Upload className="size-3.5" aria-hidden="true" />
      Push to test
    </Button>
  );
}

export function NotebookToolbar(props: NotebookToolbarProps) {
  const bar = useRef<HTMLDivElement>(null);
  const layout = toolbarLayout(useBarWidth(bar));
  const canWrite = useCanWrite();
  const mayRun = useCanRun();
  const isAdmin = useIsProjectAdmin();
  const isMember = useIsProjectMember();
  const execution = showsExecution(props.tab) && props.canRun && mayRun;
  // One cell is already all of it. Focus Mode shows one cell at a time.
  const focusable = !props.isScript;
  const kernel = kernelLabel(props.session, props.running, layout.showKernelVersion);
  // Not `canWrite`: promoting is not a write to your branch, and taking the
  // branch rule from the write rule is what kept this button off the test view.
  const promote = promoteControl(props.branch, {
    isAdmin,
    isMember,
    eligible: props.promotion?.eligible === true,
  });
  const progress = promotionProgress({
    reasons: props.promotion?.eligible === false ? props.promotion.reasons : [],
    standing: props.standing,
    isAdmin,
    eligible: props.promotion?.eligible === true,
  });

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
    <div
      ref={bar}
      className={
        'nb-toolbar sticky top-0 z-20 flex h-[44px] shrink-0 items-stretch gap-1.5 '
        + 'overflow-x-auto whitespace-nowrap border-b bg-card px-4 '
        // Production gets a colour of its own. Everything else about this row is
        // identical whichever branch you are on, and that is exactly the problem:
        // the one moment worth interrupting is running something against prod.
        + (props.branch === 'prod' ? 'border-b-2 border-status-warning' : 'border-border')
      }
    >
      <Tabs value={props.tab} onValueChange={props.onTab} className="h-full">
        <TabsList variant="line">
          {props.isNotebook && (
            <TabsTrigger value="edit">{props.isScript ? 'Script' : 'Notebook'}</TabsTrigger>
          )}
          {props.isJobsFile && <TabsTrigger value="overview">Overview</TabsTrigger>}
          <TabsTrigger value="source">
            {props.isJobsFile ? 'YAML' : props.isImage ? 'Preview' : 'Source'}
          </TabsTrigger>
          {/* A picture has no text to compare; both sides of the diff come back
              through the route that reads a file as text. */}
          {!props.isImage && <TabsTrigger value="diff">Diff vs production</TabsTrigger>}
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

          {focusable && (
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
          )}

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

      {/* The Normal|Focus switch is not a write, so it stays for viewers — Focus
          Mode is a reading layout as much as an editing one. */}
      {!execution && showsExecution(props.tab) && focusable && (
        <div className="flex items-center gap-2">
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
            <ToggleGroupItem value="focus" className={TOGGLE}>
              Focus
            </ToggleGroupItem>
          </ToggleGroup>
        </div>
      )}

      {/* Readable, not writable. Said out loud, because the alternative is a
          toolbar that quietly has no Save on it and an editor that ignores your
          typing. Only on your own branch — on test or prod the branch note below
          is the more important of the two, and two notes is noise. */}
      {props.branch === 'mine' && !props.fileEditable && (
        <span className="whitespace-nowrap text-xs text-muted-subtle">
          {props.isImage ? 'read-only — a picture opens to look at' : 'read-only — this file is not text'}
        </span>
      )}

      {/* Not your branch: say so, and offer the legitimate place to make the
          change you came here to make. */}
      {props.branch !== 'mine' && (
        <div className="flex items-center gap-2">
          <span
            className={`whitespace-nowrap text-xs ${
              props.branch === 'prod' ? 'font-semibold text-status-warning' : 'text-muted-subtle'
            }`}
          >
            {props.branch === 'prod' || props.branch === 'test'
              ? `${props.branch} — read-only${mayRun ? ', runnable' : ''}`
              : 'somebody else’s branch — read-only'}
          </span>
          <Button variant="outline" size="xs" onClick={props.onCopyToMine}>
            <Copy className="size-3.5" aria-hidden="true" />
            Copy to my branch
          </Button>
        </div>
      )}

      {/* Document-level, so these stay on every tab and never collapse. */}
      {canWrite && (
      <div className="flex items-center gap-2">
      {/* Cells only: on Source and Diff, undo is Monaco's and lives in the
          editor under the same key. Icon-only at every width — it is one glyph
          everybody already knows, and the bar has no room to spend teaching it. */}
      {showsExecution(props.tab) && (
        <Button
          variant="outline"
          size="xs"
          disabled={!props.canUndo}
          onClick={props.onUndo}
          aria-label="Undo"
          title="Undo the last cell change (⌘/Ctrl+Z). Editing inside a cell undoes with the same key."
        >
          <Undo2 className="size-3.5" aria-hidden="true" />
        </Button>
      )}
      {/* Save as and Move, behind one icon. They are the two things you do to a
          notebook's *name* rather than its contents, they are rare, and the bar
          has no room for two more labelled buttons — the breakpoints below were
          measured with this one glyph on it. */}
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="outline" size="xs" aria-label="File" title="Save a copy, or move this notebook">
            <FileOutput className="size-3.5" aria-hidden="true" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          <DropdownMenuItem onSelect={props.onSaveAs}>Save a copy as…</DropdownMenuItem>
          <DropdownMenuItem onSelect={props.onMove}>Move or rename…</DropdownMenuItem>
          {/* The same act as `+ job` in the Files list, offered where promotion
              says a notebook with no job cannot prove itself — which is here,
              and not on a page you would have to know to go back to. */}
          {props.onSchedule && (
            <DropdownMenuItem onSelect={props.onSchedule}>Schedule (add a job)…</DropdownMenuItem>
          )}
        </DropdownMenuContent>
      </DropdownMenu>
      <SaveStatusChip status={props.saveStatus} onRetry={props.onSave} />
      <InfoTip label="Where does this save to?" onOpen={explainSaving} />
      <PushControl
        standing={props.standing}
        busy={props.busy}
        onPush={props.onPush}
        onUpdate={props.onUpdate}
      />
      </div>
      )}

      {promote !== 'hidden' && (
        <Button
          size="xs"
          // Never disabled by the gate. Disabling it put the reasons behind a
          // separate ⓘ — a smaller target than the thing people actually press,
          // and one that says "there is an explanation somewhere" rather than
          // giving it. Blocked is a button that answers.
          variant={promote === 'ready' ? 'default' : 'outline'}
          disabled={props.busy}
          onClick={promote === 'ready' ? props.onPromote : () => explainBlocked(progress)}
          title={promote === 'ready' ? 'Ship to production' : 'What is left before this can ship'}
        >
          {props.promotion?.isDeletion
            ? 'Promote deletion'
            : layout.shortPromote
              ? 'Promote'
              : 'Promote to production'}
        </Button>
      )}
    </div>
  );
}
