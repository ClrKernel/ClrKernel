import { Keyboard } from 'lucide-react';
import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Modal } from './Modal';

/** Mac keyboards say ⌘ and ⌥ where everything else says Ctrl and Alt. */
const mac = typeof navigator !== 'undefined'
  && /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);
const MOD = mac ? '⌘' : 'Ctrl';
const ALT = mac ? '⌥' : 'Alt';

interface Shortcut {
  keys: string[][];
  does: string;
}

/**
 * Every key here is one this app actually binds, or one Monaco itself provides in
 * every editor — a list that promised a key which did nothing would be worse than
 * no list. Adding a shortcut means adding its line here.
 */
export const SHORTCUTS: { title: string; where?: string; items: Shortcut[] }[] = [
  {
    title: 'Running cells',
    where: 'In a cell, in either layout',
    items: [
      { keys: [[MOD, 'Enter']], does: 'Run the cell' },
      { keys: [['Shift', 'Enter']], does: 'Run the cell and move to the next one' },
      { keys: [[ALT, 'Enter']], does: 'Run the cell and add a new cell below' },
    ],
  },
  {
    title: 'Editing',
    items: [
      { keys: [[MOD, 'S']], does: 'Save now (it also saves a moment after you stop typing)' },
      { keys: [[MOD, 'Z']], does: 'Undo the last cell change — add, delete, move — when no cell is being edited' },
      { keys: [['Ctrl', 'Space']], does: 'Show completions' },
      { keys: [[MOD, 'F']], does: 'Find in the cell' },
    ],
  },
  {
    title: 'Focus Mode',
    items: [
      { keys: [[ALT, '↑'], [ALT, '↓']], does: 'Previous / next cell, from the editor' },
      { keys: [['↑'], ['↓']], does: 'Select a cell in the contents list' },
      { keys: [[ALT, '↑'], [ALT, '↓']], does: 'Move the selected cell up / down, in the contents list' },
    ],
  },
  {
    title: 'Query editor',
    where: 'Connections',
    items: [
      { keys: [[MOD, 'Enter'], ['F5']], does: 'Run the query' },
    ],
  },
];

function Keys({ keys }: { keys: string[][] }) {
  return (
    <span className="flex flex-wrap items-center gap-1 whitespace-nowrap">
      {keys.map((chord, i) => (
        <span key={i} className="flex items-center gap-1">
          {i > 0 && <span className="px-0.5 text-muted-subtle">/</span>}
          {chord.map((key) => (
            <kbd
              key={key}
              className="rounded border border-border bg-muted px-1.5 py-0.5 font-mono text-xs text-foreground"
            >
              {key}
            </kbd>
          ))}
        </span>
      ))}
    </span>
  );
}

/** The toolbar's keyboard button and the list it opens. */
export function KeyboardShortcuts() {
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button
        variant="outline"
        size="xs"
        aria-label="Keyboard shortcuts"
        title="Keyboard shortcuts"
        onClick={() => setOpen(true)}
      >
        <Keyboard className="size-3.5" aria-hidden="true" />
      </Button>
      {open && (
        <Modal title="Keyboard shortcuts" onClose={() => setOpen(false)}>
          {SHORTCUTS.map((group) => (
            <section key={group.title}>
              <h3 className="mb-1.5 text-sm font-semibold">
                {group.title}
                {group.where && (
                  <span className="ml-2 font-normal text-muted-subtle">{group.where}</span>
                )}
              </h3>
              <table className="w-full text-sm">
                <tbody>
                  {group.items.map((item) => (
                    <tr key={item.does} className="border-t border-border first:border-t-0">
                      <td className="w-[11rem] py-1.5 pr-3 align-top"><Keys keys={item.keys} /></td>
                      <td className="py-1.5 text-muted-foreground">{item.does}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </section>
          ))}
        </Modal>
      )}
    </>
  );
}
