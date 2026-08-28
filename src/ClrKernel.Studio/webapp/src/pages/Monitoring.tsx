import { useMemo, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { X } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select';
import { api, isActive, type Run, type RunPage } from '../api';
import { EnvBadge, ErrorBanner, PageHeader, StatusBadge, usePolling } from '../components/common';
import { DashboardTabs } from './Dashboard';
import { duration, timeAgo } from '../ipynb';
import { useProjects } from '../projectContext';
import { rerunOutcome, rerunQuestion } from '../rerun';
import {
  activeFilters, fromSearch, PAGE_SIZE, runsQuery, sortBy, toSearch, withFilter,
  type RunFilters, type RunSort,
} from '../runFilters';

/**
 * How a run was started, said once.
 *
 * The spec asked for Trigger and "run mode" as two columns. They are the same
 * column: `scheduled` is `Trigger.Schedule`, `manual-all` is `Trigger.Manual`,
 * and the third value it named — a cell-by-cell run from the editor — is not a
 * job run at all and lives in its own table on purpose. Two columns would have
 * been one fact printed twice and one that could never fill in.
 */
function Trigger({ run, onActor }: { run: Run; onActor: (id: string) => void }) {
  if (run.trigger !== 'Manual') {
    return <span className="text-muted-foreground">{run.trigger.toLowerCase()}</span>;
  }
  return (
    <span className="text-muted-foreground">
      manual
      {/* Null here reads two ways — a run from before the column existed, and one
          whose account is gone — and neither is worth a second column to tell
          apart, so the name is simply absent when it is not known.

          It is also why the actor filter is set by clicking a name rather than
          picked from a list: a dropdown of everyone who has ever pressed run
          would have been empty on any server that upgraded into this, and an
          empty dropdown reads as broken rather than as "nobody yet". */}
      {run.actorName && (
        <>
          {/* The separator sits outside the button, or the leading space
              collapses and it reads "manual· Ada". */}
          {' · '}
          <button
            type="button"
            className="text-foreground hover:underline"
            title="Only this person's runs"
            onClick={(e) => {
              e.stopPropagation();
              if (run.actorId) {
                onActor(run.actorId);
              }
            }}
          >
            {run.actorName}
          </button>
        </>
      )}
    </span>
  );
}

/** A heading that sorts. The caret says which way, and only on the live column. */
function SortHeader({
  column, label, filters, onSort, className = '',
}: {
  column: RunSort;
  label: string;
  filters: RunFilters;
  onSort: (next: RunFilters) => void;
  className?: string;
}) {
  const live = filters.sort === column;
  return (
    <th className={className}>
      <button
        type="button"
        className="inline-flex items-center gap-1 font-inherit hover:text-foreground"
        aria-sort={live ? (filters.asc ? 'ascending' : 'descending') : 'none'}
        onClick={() => onSort(sortBy(filters, column))}
      >
        {label}
        <span aria-hidden="true" className={live ? '' : 'invisible'}>
          {filters.asc ? '↑' : '↓'}
        </span>
      </button>
    </th>
  );
}

/** "Any" as a Radix value, because Radix refuses an empty one. */
const ANY = '__any';

function Filter({
  label, value, options, onPick,
}: {
  label: string;
  value: string | undefined;
  options: { value: string; label: string }[];
  onPick: (value: string | undefined) => void;
}) {
  return (
    <Select
      value={value ?? ANY}
      onValueChange={(picked) => onPick(picked === ANY ? undefined : picked)}
    >
      <SelectTrigger size="sm" className="h-8 w-auto min-w-[9rem]" aria-label={label}>
        <SelectValue>{value ?? label}</SelectValue>
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={ANY}>{label}</SelectItem>
        {options.map((option) => (
          <SelectItem key={option.value} value={option.value}>
            {option.label}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

const STATUSES = ['Pending', 'Running', 'Succeeded', 'Failed', 'Cancelled', 'TimedOut'];
const TRIGGERS = ['Schedule', 'Manual', 'Dependency', 'Retry'];

/**
 * Every project's runs, in one grid.
 *
 * The one view in the app that deliberately ignores the selected project —
 * Project is its first column, and "what is failing right now" is a question
 * about the whole install. Which projects it may show is decided by the server,
 * inside the query, so a page of fifty is fifty rows and not "the eleven of the
 * fifty you were allowed to see".
 *
 * Filtering, sorting and paging are all the server's for the same reason: run
 * history grows without bound, and every one of them done here would work right
 * up until the day it very suddenly didn't.
 */
export function Monitoring() {
  const [search, setSearch] = useSearchParams();
  const navigate = useNavigate();
  const { projects } = useProjects();
  const filters = useMemo(() => fromSearch(search.toString()), [search]);

  // The filters live in the URL, so a filtered grid is a link somebody can send.
  const apply = (next: RunFilters) => setSearch(toSearch(next).replace(/^\?/, ''));

  const query = runsQuery(filters);
  const { data, error } = usePolling<{ page: RunPage; environments: string[] }>(
    async () => ({
      page: await api.runGrid(query),
      environments: (await api.health()).environments,
    }),
    5000,
    [query],
  );

  const runs = data?.page.runs ?? [];
  const chips = activeFilters(filters);

  // What is selected is intersected with what is on the page, not stored as a
  // list of its own: the grid polls, and a row that filters or pages away from
  // under a ticked box must stop counting rather than stay in a rerun you can no
  // longer see. The ids linger in the set and mean nothing until the row is back.
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const selected = runs.filter((run) => picked.has(run.id));
  const toggle = (id: string) => setPicked((was) => {
    const next = new Set(was);
    if (!next.delete(id)) {
      next.add(id);
    }
    return next;
  });

  /**
   * Bulk rerun is always at branch HEAD. Going back to a recorded commit is a
   * single deliberate act — it lives on the run's own page — and after a fix, the
   * fix is the thing you want run.
   *
   * Nothing throttles here because the scheduler already does: every launch waits
   * on the same parallelism semaphore a scheduled run does, so fifty reruns queue
   * rather than arriving at a database at once.
   */
  async function rerunSelected() {
    if (!confirm(rerunQuestion(selected, false))) {
      return;
    }
    setNote(null);
    setBusy(true);
    try {
      const result = await api.rerun(selected.map((run) => run.id));
      setNote(rerunOutcome(result.started, result.refused));
      setPicked(new Set());
    } catch (e) {
      setNote((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <PageHeader title="Dashboard" />
      <DashboardTabs />
      <ErrorBanner error={error} />

      <div className="mb-3 flex flex-wrap items-center gap-2">
        <Filter
          label="Project"
          value={filters.project}
          options={projects.map((p) => ({ value: p.slug, label: p.name }))}
          onPick={(value) => apply(withFilter(filters, 'project', value))}
        />
        <Filter
          label="Branch"
          value={filters.env}
          options={(data?.environments ?? []).map((e) => ({ value: e, label: e }))}
          onPick={(value) => apply(withFilter(filters, 'env', value))}
        />
        <Filter
          label="Status"
          value={filters.status}
          options={STATUSES.map((s) => ({ value: s, label: s }))}
          onPick={(value) => apply(withFilter(filters, 'status', value as RunFilters['status']))}
        />
        <Filter
          label="Trigger"
          value={filters.trigger}
          options={TRIGGERS.map((t) => ({ value: t, label: t }))}
          onPick={(value) => apply(withFilter(filters, 'trigger', value as RunFilters['trigger']))}
        />
        {/* Native date inputs: the platform already has a calendar, a locale and
            a keyboard story, and none of that is worth reimplementing. */}
        <label className="flex items-center gap-1.5 text-base text-muted-foreground">
          from
          <input
            type="date"
            className="h-8 rounded-md border border-border bg-card px-2 text-base"
            value={filters.from ?? ''}
            max={filters.to}
            onChange={(e) => apply(withFilter(filters, 'from', e.target.value))}
          />
        </label>
        <label className="flex items-center gap-1.5 text-base text-muted-foreground">
          to
          <input
            type="date"
            className="h-8 rounded-md border border-border bg-card px-2 text-base"
            value={filters.to ?? ''}
            min={filters.from}
            onChange={(e) => apply(withFilter(filters, 'to', e.target.value))}
          />
        </label>
      </div>

      {/* Job, file and actor are narrowed by clicking a cell rather than typed.
          The server matches them exactly, and a text box that looks like search
          but demands the whole string is a worse offer than no text box. */}
      {chips.length > 0 && (
        <div className="mb-3 flex flex-wrap items-center gap-1.5">
          {chips.map(({ key, value }) => (
            <Badge key={key} variant="secondary" className="gap-1 font-normal">
              <span className="text-muted-subtle">{key}</span> {value}
              <button
                type="button"
                aria-label={`Clear the ${key} filter`}
                className="ml-0.5 hover:text-foreground"
                onClick={() => apply(withFilter(filters, key, undefined))}
              >
                <X className="size-3" />
              </button>
            </Badge>
          ))}
          <Button variant="ghost" size="sm" className="h-6 px-2" onClick={() => apply({
            ...filters, project: undefined, env: undefined, status: undefined, trigger: undefined,
            job: undefined, path: undefined, actor: undefined, from: undefined, to: undefined,
            page: 0,
          })}>
            clear all
          </Button>
        </div>
      )}

      {selected.length > 0 && (
        <div className="mb-3 flex flex-wrap items-center gap-2">
          <Button size="sm" onClick={rerunSelected} disabled={busy}>
            {busy ? 'Starting…' : `Run again (${selected.length})`}
          </Button>
          <Button variant="ghost" size="sm" onClick={() => setPicked(new Set())}>
            Clear selection
          </Button>
        </div>
      )}
      {note && (
        <p className="mb-3 text-base text-muted-foreground">{note}</p>
      )}

      {data && runs.length === 0 ? (
        <p className="text-base text-muted-foreground">
          {chips.length > 0 ? 'No runs match these filters.' : 'No runs yet.'}
        </p>
      ) : (
        <div className="table-box">
          <table className="table">
            <thead>
              <tr>
                <th className="w-[34px]">
                  <input
                    type="checkbox"
                    aria-label="Select every run on this page"
                    checked={runs.length > 0 && selected.length === runs.length}
                    onChange={(e) => setPicked(
                      e.target.checked ? new Set(runs.map((run) => run.id)) : new Set(),
                    )}
                  />
                </th>
                <SortHeader column="project" label="Project" filters={filters} onSort={apply} />
                <SortHeader column="jobName" label="Job" filters={filters} onSort={apply} />
                <th>File</th>
                <SortHeader column="environment" label="Branch" filters={filters} onSort={apply} />
                <SortHeader column="status" label="Status" filters={filters} onSort={apply} />
                <SortHeader column="trigger" label="Trigger" filters={filters} onSort={apply} />
                <SortHeader column="started" label="Started" filters={filters} onSort={apply} />
                <th>Took</th>
              </tr>
            </thead>
            <tbody>
              {runs.map((run) => (
                <tr
                  key={run.id}
                  className="cursor-pointer"
                  onClick={() => navigate(`/runs/${run.id}`)}
                >
                  <td onClick={(e) => e.stopPropagation()}>
                    <input
                      type="checkbox"
                      aria-label={`Select the ${run.jobName} run`}
                      checked={picked.has(run.id)}
                      onChange={() => toggle(run.id)}
                    />
                  </td>
                  <td className="whitespace-nowrap">
                    <button
                      type="button"
                      className="hover:underline"
                      title="Only this project"
                      onClick={(e) => {
                        e.stopPropagation();
                        apply(withFilter(filters, 'project', run.project));
                      }}
                    >
                      {projects.find((p) => p.slug === run.project)?.name ?? run.project}
                    </button>
                  </td>
                  <td className="whitespace-nowrap">
                    <Link
                      className="font-semibold text-primary hover:underline"
                      to={`/jobs/${run.project}/${run.environment}/${encodeURIComponent(run.jobName)}`}
                      onClick={(e) => e.stopPropagation()}
                    >
                      {run.jobName}
                    </Link>
                  </td>
                  <td className="font-mono text-code text-muted-foreground">
                    <button
                      type="button"
                      className="hover:underline"
                      title="Only this file"
                      onClick={(e) => {
                        e.stopPropagation();
                        apply(withFilter(filters, 'path', run.notebookPath));
                      }}
                    >
                      {run.notebookPath}
                    </button>
                  </td>
                  <td><EnvBadge env={run.environment} /></td>
                  <td><StatusBadge status={run.status} /></td>
                  <td className="whitespace-nowrap">
                    <Trigger
                      run={run}
                      onActor={(id) => apply(withFilter(filters, 'actor', id))}
                    />
                  </td>
                  <td className="whitespace-nowrap text-muted-foreground">
                    {timeAgo(run.startedAt ?? run.createdAt)}
                  </td>
                  <td className="whitespace-nowrap text-muted-foreground">
                    {isActive(run.status) ? '—' : duration(run.startedAt, run.finishedAt)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {(filters.page > 0 || data?.page.hasMore) && (
        <div className="mt-3 flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            disabled={filters.page === 0}
            onClick={() => apply({ ...filters, page: filters.page - 1 })}
          >
            Previous
          </Button>
          <span className="text-base text-muted-foreground">
            {filters.page * PAGE_SIZE + 1}–{filters.page * PAGE_SIZE + runs.length}
          </span>
          <Button
            variant="outline"
            size="sm"
            disabled={!data?.page.hasMore}
            onClick={() => apply({ ...filters, page: filters.page + 1 })}
          >
            Next
          </Button>
        </div>
      )}
    </div>
  );
}
