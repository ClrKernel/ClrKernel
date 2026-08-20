// Monaco's editor worker, as a local worker entry point.
//
// Imported by setup.ts as `./editor.worker?worker`: a RELATIVE `?worker` import
// of our own file, which Vite handles identically in dev and in a build. Pointing
// `?worker` straight at the package instead breaks both ways — Monaco's exports
// map rejects a subpath carrying the query, and the dev-mode dependency
// optimizer rewrites it to a URL that 404s.
//
// Note the specifier: the exports map ("./*.js" -> "./esm/vs/*.js") supplies the
// esm/vs prefix itself, so it must NOT be repeated here.
import 'monaco-editor/editor/editor.worker.js';
