import { Fragment, createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { useParams, useSearchParams } from 'react-router-dom';
import { api, setProject, type Project } from './api';

/** Remembers which project you were last in, per browser. */
const STORAGE_KEY = 'clrkernel-jobs-project';

export interface ProjectState {
  projects: Project[];
  current: string;
  select: (slug: string) => void;
}

const ProjectContext = createContext<ProjectState>({
  projects: [],
  current: 'default',
  select: () => undefined,
});

export function useProjects(): ProjectState {
  return useContext(ProjectContext);
}

export function useCurrentProject(): Project | undefined {
  const { projects, current } = useProjects();
  return projects.find((p) => p.slug === current);
}

/**
 * Loads the registered projects and holds which one the app is looking at.
 *
 * The value is mirrored into the API client rather than passed to it, so a page
 * makes the same call whichever project is selected. That works because every
 * page is remounted when the selection changes — see the `key` in App — so no
 * request outlives the project it was made for.
 *
 * Renders nothing until the list has arrived: `default` is only the right guess
 * for a server that registered nothing, and requests made before we know would
 * 404 against a slug that does not exist.
 */
export function ProjectProvider({ children }: { children: ReactNode }) {
  const [projects, setProjects] = useState<Project[] | null>(null);
  const [current, setCurrent] = useState<string>(
    () => localStorage.getItem(STORAGE_KEY) ?? 'default',
  );

  useEffect(() => {
    api.projects()
      .then(({ projects: found }) => {
        setProjects(found);
        // The remembered project may have been unregistered since, and a slug
        // nobody has 404s every request the page makes.
        setCurrent((slug) => (found.some((p) => p.slug === slug) ? slug : found[0]?.slug ?? 'default'));
      })
      .catch(() => setProjects([]));
  }, []);

  useEffect(() => {
    setProject(current);
    localStorage.setItem(STORAGE_KEY, current);
  }, [current]);

  if (projects == null) {
    return null;
  }

  const select = (slug: string) => {
    setProject(slug);   // before the re-render, so the remounted pages ask for the right one
    setCurrent(slug);
  };

  return (
    <ProjectContext.Provider value={{ projects, current, select }}>
      {/* Keyed by the selection: switching projects remounts everything below,
          so no page keeps data it fetched for the project you just left. Every
          page here is project-scoped, so there is nothing worth preserving. */}
      <Fragment key={current}>{children}</Fragment>
    </ProjectContext.Provider>
  );
}

/**
 * A route whose URL names its project — `/jobs/:project/…`, or `?project=` for
 * the editor, which keeps its subject in the query string.
 *
 * A link to a job has to mean one job. Without the project in the URL it would
 * mean whichever project the person opening it happened to have selected, which
 * for two projects that each have a `nightly` is two different jobs.
 *
 * The page renders only once the selection agrees with the URL, so it never
 * makes a request against the project you were looking at a moment ago.
 */
export function ProjectScope({ children }: { children: ReactNode }) {
  const { project: fromPath } = useParams<{ project?: string }>();
  const [params] = useSearchParams();
  const wanted = fromPath ?? params.get('project') ?? undefined;
  const { current, select } = useProjects();

  useEffect(() => {
    if (wanted && wanted !== current) {
      select(wanted);
    }
  });

  return wanted && wanted !== current ? null : <>{children}</>;
}
