import { useEffect, useState } from 'react';
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { api, type ApiConnection, type ApiLanguage } from '../api';
import { connectionChoices } from '../notebook';

/** What "no choice" is worth on the wire and on screen. */
const DEFAULT = '__default__';

/**
 * Which connection a query file runs against — SSMS's database dropdown.
 *
 * It exists for the files that are one query and have nowhere to put a
 * `#!sql-connect`: a `.sql`, a `.dax`. A notebook says which connection it uses
 * in its own text, which travels with it and is the right answer there; a bare
 * query file has no such line, and adding one would mean editing the file every
 * time you wanted to run the same query somewhere else.
 *
 * So the choice lives here and rides on the *run*. Point it at test, run, point
 * it at production, run again — the file is the same file throughout and git sees
 * nothing.
 *
 * Not remembered between openings, deliberately: which database you last ran a
 * query against is exactly the thing you do not want silently inherited a week
 * later. It holds while the file is open, which is the session it belongs to.
 */
export function ConnectionPicker({
  language, value, onChange,
}: {
  /** The file's language, for the compatibility mark. Null while it loads. */
  language: ApiLanguage | null;
  /** The chosen connection's name, or null for whatever the notebook's
   *  `connections.json` resolves to. */
  value: string | null;
  onChange: (connection: string | null) => void;
}) {
  const [connections, setConnections] = useState<ApiConnection[] | null>(null);

  useEffect(() => {
    let live = true;
    api.connections()
      .then((reply) => live && setConnections(reply.connections))
      // A picker that cannot list anything is a picker that is not offered; the
      // run still works and resolves the default, which is what it did before.
      .catch(() => live && setConnections([]));
    return () => { live = false; };
  }, []);

  if (connections == null || connections.length === 0) {
    return null;
  }

  const choices = connectionChoices(connections, language);
  const shared = choices.filter((c) => c.connection.scope === 'shared');
  const mine = choices.filter((c) => c.connection.scope !== 'shared');

  const group = (
    label: string, items: ReturnType<typeof connectionChoices>,
  ) => (items.length === 0 ? null : (
    <SelectGroup>
      <SelectLabel>{label}</SelectLabel>
      {items.map(({ connection, runnable, why }) => (
        <SelectItem
          key={connection.id}
          value={connection.name}
          disabled={!runnable}
          title={why ?? connection.type}
        >
          {connection.name}
          <span className="ml-2 text-xs text-muted-subtle">{connection.type}</span>
        </SelectItem>
      ))}
    </SelectGroup>
  ));

  return (
    <Select
      value={value ?? DEFAULT}
      onValueChange={(next) => onChange(next === DEFAULT ? null : next)}
    >
      <SelectTrigger size="sm" className="h-6 w-auto gap-1 text-sm" aria-label="Connection">
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={DEFAULT}>Default connection</SelectItem>
        {/* Shared first: it is the one somebody else can also run, and the one a
            scheduled job would use. */}
        {group('Shared', shared)}
        {group('Only mine', mine)}
      </SelectContent>
    </Select>
  );
}
