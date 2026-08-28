using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cronos;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ClrKernel.Studio;

/// <summary>One problem with a jobs file, positioned so the editor can point at it.</summary>
/// <param name="Line">1-based, as editors count.</param>
/// <param name="Column">1-based.</param>
public sealed record JobsProblem(int Line, int Column, string Message);

/// <summary>
/// Checks a <c>*.jobs.yaml</c> without writing it anywhere, and says where each
/// problem is.
/// <para>
/// It exists because <see cref="JobsFile"/>'s deserializer is built with
/// <c>IgnoreUnmatchedProperties</c>: a misspelled <c>scedule:</c> parses cleanly
/// into a job that simply never runs, and nothing anywhere would have said so.
/// So this walks the node graph rather than deserializing, and is stricter than
/// the parser on purpose — the same strictness the published schema applies in
/// the editor.
/// </para>
/// <para>
/// It does not resolve notebooks on disk. That is a different question ("does the
/// file this names exist on this branch"), the catalog already answers it, and
/// asking it here would make a valid file invalid while you are still typing the
/// path.
/// </para>
/// </summary>
public static class JobsFileValidation {
    /// <param name="path">
    /// The file's path, when the caller has it. Only the name is used, to check a
    /// declared <c>notebook:</c> against the one this file is paired with. Omitted,
    /// that check is skipped — everything else is answerable from the text alone.
    /// </param>
    public static IReadOnlyList<JobsProblem> Check(string yaml, string path = null) {
        var problems = new List<JobsProblem>();
        if (string.IsNullOrWhiteSpace(yaml)) {
            problems.Add(new JobsProblem(1, 1, "A jobs file needs a `jobs:` list."));
            return problems;
        }

        var stream = new YamlStream();
        try {
            stream.Load(new StringReader(yaml));
        } catch (YamlException e) {
            // The parser's own position, which is the one that matters — a syntax
            // error anywhere makes every other check meaningless, so stop here.
            problems.Add(new JobsProblem(
                (int)Math.Max(1, e.Start.Line), (int)Math.Max(1, e.Start.Column), Clean(e.Message)));
            return problems;
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) {
            problems.Add(new JobsProblem(1, 1, "A jobs file is a mapping with a `jobs:` list."));
            return problems;
        }

        UnknownKeys(root, JobsSchema.RootKeys, problems, "");

        // A jobs file schedules the notebook it is named for. Declaring a different
        // one is not a second option, it is a statement that is not true — and the
        // loader refuses it, so catching it here is the difference between a
        // squiggle and a job that vanishes from the catalog.
        if (path != null && Child(root, "notebook") is YamlScalarNode { Value: { Length: > 0 } declared }
            && !JobsPairing.Matches(path, declared)) {
            var paired = JobsPairing.BaseNameOfJobsFile(path);
            problems.Add(At(Child(root, "notebook"),
                $"`notebook: {declared}` is not what this file is named for. It schedules "
                + $"`{paired}` beside it — remove this line, or point it there."));
        }

        if (Child(root, "defaults") is YamlMappingNode defaults) {
            CheckEntry(defaults, problems, isDefaults: true);
        }

        var jobs = Child(root, "jobs");
        if (jobs == null) {
            problems.Add(At(root, "A jobs file needs a `jobs:` list."));
            return problems;
        }
        if (jobs is not YamlSequenceNode sequence) {
            problems.Add(At(jobs, "`jobs:` is a list, one entry per job."));
            return problems;
        }
        if (sequence.Children.Count == 0) {
            problems.Add(At(jobs, "`jobs:` has no entries — a file with no jobs defines nothing."));
            return problems;
        }

        var names = new Dictionary<string, YamlNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sequence.Children) {
            if (item is not YamlMappingNode entry) {
                problems.Add(At(item, "Every job is a mapping, starting with `name:`."));
                continue;
            }
            CheckEntry(entry, problems, isDefaults: false);

            if (Child(entry, "name") is YamlScalarNode { Value: { Length: > 0 } name }) {
                // Within one file only. Across files is the catalog's job, and it
                // already reports it — this is the half that can be answered from
                // the buffer you are typing into.
                if (!names.TryAdd(name, entry)) {
                    problems.Add(At(entry, $"Two jobs in this file are called '{name}'."));
                }
            }
        }
        return problems;
    }

    private static void CheckEntry(YamlMappingNode entry, List<JobsProblem> problems, bool isDefaults) {
        UnknownKeys(entry, JobsSchema.EntryKeys, problems, isDefaults ? "defaults." : "");

        if (!isDefaults) {
            var name = Child(entry, "name") as YamlScalarNode;
            if (string.IsNullOrWhiteSpace(name?.Value)) {
                problems.Add(At(entry, "Every job needs a `name:`."));
            }
        } else if (Child(entry, "name") != null) {
            problems.Add(At(Child(entry, "name"), "`defaults` cannot set `name:` — a name belongs to one job."));
        }

        if (Child(entry, "cron") is YamlScalarNode { Value: { Length: > 0 } cron }) {
            try {
                CronExpression.Parse(cron);
            } catch (CronFormatException e) {
                problems.Add(At(Child(entry, "cron"), $"`cron: {cron}` is not a schedule: {Clean(e.Message)}"));
            }
        }

        foreach (var (key, node) in new[] { "timeoutSeconds", "retryCount" }
                     .Select(k => (k, Child(entry, k)))) {
            if (node is YamlScalarNode scalar && !int.TryParse(scalar.Value, out _)) {
                problems.Add(At(node, $"`{key}:` is a whole number of {(key == "retryCount" ? "attempts" : "seconds")}."));
            }
        }

        if (Child(entry, "notify") is YamlMappingNode notify) {
            UnknownKeys(notify, JobsSchema.NotifyKeys, problems, "notify.");
        }
    }

    private static void UnknownKeys(
        YamlMappingNode node, IReadOnlyCollection<string> known, List<JobsProblem> problems, string prefix) {
        foreach (var key in node.Children.Keys.OfType<YamlScalarNode>()) {
            if (key.Value != null && !known.Contains(key.Value)) {
                var suggestion = Nearest(key.Value, known);
                problems.Add(At(key, $"`{prefix}{key.Value}` is not a setting"
                    + (suggestion != null ? $" — did you mean `{suggestion}`?" : ".")));
            }
        }
    }

    /// <summary>
    /// The closest known key within one or two edits, so a typo names its fix.
    /// Levenshtein on a handful of short strings — the cost of being clever here
    /// would exceed the cost of the whole check.
    /// </summary>
    private static string Nearest(string typo, IReadOnlyCollection<string> known) {
        string best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in known) {
            var distance = Distance(typo.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance < bestDistance) {
                (best, bestDistance) = (candidate, distance);
            }
        }
        return bestDistance <= 2 ? best : null;
    }

    private static int Distance(string a, string b) {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++) {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++) {
                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    private static YamlNode Child(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static JobsProblem At(YamlNode node, string message) =>
        new((int)Math.Max(1, node.Start.Line), (int)Math.Max(1, node.Start.Column), message);

    /// <summary>YamlDotNet appends its own position to the message; the caller has it.</summary>
    private static string Clean(string message) {
        var at = message.IndexOf("): ", StringComparison.Ordinal);
        return (at >= 0 ? message[(at + 3)..] : message).Trim();
    }
}
