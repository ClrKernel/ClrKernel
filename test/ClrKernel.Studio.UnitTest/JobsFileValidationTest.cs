using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// The jobs-file checker is what stands between a typo and a job that silently
/// never runs, and it is the authority the push gate uses — so the cases here are
/// the ones that would otherwise reach `test`.
/// </summary>
[TestClass]
public class JobsFileValidationTest {
    private const string _good = """
        notebook: ./daily.nb.md
        jobs:
          - name: daily
            cron: "0 6 * * *"
        """;

    [TestMethod]
    public void A_good_file_has_nothing_to_say() {
        CollectionAssert.AreEqual(
            new string[0], JobsFileValidation.Check(_good).Select(p => p.Message).ToArray());
    }

    /// <summary>
    /// The motivating case from the spec. YamlDotNet is built with
    /// IgnoreUnmatchedProperties, so this parses into a job with no schedule and
    /// nothing anywhere complains — the job just never runs, and you find out
    /// weeks later.
    /// <para>
    /// No suggestion here, and that is right: `scedule` misspells *schedule*, and
    /// the setting is called `cron`. Nothing is within two edits, and a confident
    /// wrong guess would be worse than none.
    /// </para>
    /// </summary>
    [TestMethod]
    public void A_misspelled_key_is_caught() {
        var problems = JobsFileValidation.Check("""
            jobs:
              - name: daily
                scedule: "0 6 * * *"
            """);
        Assert.AreEqual(1, problems.Count, string.Join(" | ", problems.Select(p => p.Message)));
        StringAssert.Contains(problems[0].Message, "scedule");
        Assert.IsFalse(problems[0].Message.Contains("did you mean"));
        Assert.AreEqual(3, problems[0].Line, "pointing at the offending line, not the file");
    }

    /// <summary>A typo close to a real setting names its own fix.</summary>
    [TestMethod]
    public void A_near_miss_suggests_the_key_that_was_meant() {
        foreach (var (typo, meant) in new[] {
                     ("enabledd", "enabled"), ("crn", "cron"), ("retrycount", "retryCount") }) {
            var problems = JobsFileValidation.Check($"jobs:\n  - name: daily\n    {typo}: 1\n");
            Assert.AreEqual(1, problems.Count, typo);
            StringAssert.Contains(problems[0].Message, $"did you mean `{meant}`", typo);
        }
    }

    [TestMethod]
    public void An_unknown_key_with_no_near_match_still_reports() {
        var problems = JobsFileValidation.Check("""
            jobs:
              - name: daily
                elephant: true
            """);
        Assert.AreEqual(1, problems.Count);
        StringAssert.Contains(problems[0].Message, "elephant");
        Assert.IsFalse(problems[0].Message.Contains("did you mean"),
            "a guess that is not close is worse than no guess");
    }

    [TestMethod]
    public void Broken_yaml_reports_the_parser_position_and_stops() {
        var problems = JobsFileValidation.Check("jobs:\n  - name: daily\n   cron: bad indent\n");
        Assert.AreEqual(1, problems.Count, "one syntax error, not a cascade of structural ones");
        Assert.IsTrue(problems[0].Line >= 2, $"line {problems[0].Line}");
    }

    [TestMethod]
    public void A_file_with_no_jobs_is_refused_every_way_it_can_be_written() {
        foreach (var yaml in new[] { "", "   ", "notebook: ./x.nb.md\n", "jobs: []\n" }) {
            Assert.AreEqual(1, JobsFileValidation.Check(yaml).Count, $"for '{yaml.Trim()}'");
        }
    }

    [TestMethod]
    public void Every_job_needs_a_name() {
        var problems = JobsFileValidation.Check("jobs:\n  - cron: \"0 6 * * *\"\n");
        Assert.AreEqual(1, problems.Count);
        StringAssert.Contains(problems[0].Message, "name");
    }

    [TestMethod]
    public void Defaults_are_checked_too_but_may_not_name_a_job() {
        var problems = JobsFileValidation.Check("""
            defaults:
              name: shared
              retires: 2
            jobs:
              - name: daily
            """);
        Assert.AreEqual(2, problems.Count, string.Join(" | ", problems.Select(p => p.Message)));
        Assert.IsTrue(problems.Any(p => p.Message.Contains("defaults` cannot set")));
        Assert.IsTrue(problems.Any(p => p.Message.Contains("defaults.retires")),
            "a bad key in defaults says it came from defaults");
    }

    [TestMethod]
    public void A_cron_that_is_not_a_schedule_is_caught_where_it_is_written() {
        var problems = JobsFileValidation.Check("jobs:\n  - name: daily\n    cron: \"every tuesday\"\n");
        Assert.AreEqual(1, problems.Count);
        StringAssert.Contains(problems[0].Message, "not a schedule");
        Assert.AreEqual(3, problems[0].Line);
    }

    [TestMethod]
    public void Two_jobs_with_one_name_in_the_same_file() {
        var problems = JobsFileValidation.Check("jobs:\n  - name: daily\n  - name: DAILY\n");
        Assert.AreEqual(1, problems.Count);
        StringAssert.Contains(problems[0].Message, "called 'DAILY'");
    }

    [TestMethod]
    public void Notify_keys_are_checked() {
        var problems = JobsFileValidation.Check("""
            jobs:
              - name: daily
                notify:
                  onFailure: [ops]
                  onFailure2: [ops]
            """);
        Assert.AreEqual(1, problems.Count);
        StringAssert.Contains(problems[0].Message, "notify.onFailure2");
    }

    [TestMethod]
    public void Numbers_that_are_not_numbers() {
        var problems = JobsFileValidation.Check("jobs:\n  - name: daily\n    retryCount: soon\n");
        Assert.AreEqual(1, problems.Count);
        StringAssert.Contains(problems[0].Message, "whole number");
    }

    /// <summary>
    /// A job cannot name its own notebook any more: a jobs file schedules the
    /// notebook it is named for, and every entry in it is a schedule for that one.
    /// The key is simply unknown now, which is what the editor underlines.
    /// </summary>
    [TestMethod]
    public void A_job_cannot_name_its_own_notebook() {
        var problems = JobsFileValidation.Check("jobs:\n  - name: daily\n    notebook: ./other.nb.md\n");
        Assert.AreEqual(1, problems.Count);
        StringAssert.Contains(problems[0].Message, "notebook");
    }

    /// <summary>
    /// The file-level one may stay, but only if it repeats the pairing. Checked
    /// only when the caller passes a path — the text alone cannot know.
    /// </summary>
    [TestMethod]
    public void A_declared_notebook_is_checked_against_the_file_name() {
        var good = "notebook: ./daily.nb.md\njobs:\n  - name: daily\n";
        Assert.AreEqual(0, JobsFileValidation.Check(good, "reports/daily.jobs.yaml").Count);
        Assert.AreEqual(0, JobsFileValidation.Check(good).Count, "no path, no opinion");

        var wrong = JobsFileValidation.Check(good, "reports/weekly.jobs.yaml");
        Assert.AreEqual(1, wrong.Count);
        StringAssert.Contains(wrong[0].Message, "not what this file is named for");
        Assert.AreEqual(1, wrong[0].Line, "on the line that declares it");
    }
}

/// <summary>
/// The editor's schema and the server's checker come from one place, and that
/// place has to keep up with the model. Adding a property to JobsFile without
/// describing it would otherwise give a key the editor flags as unknown and the
/// push refuses — for a setting that works.
/// </summary>
[TestClass]
public class JobsSchemaTest {
    private static string[] Camel<T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .OrderBy(n => n).ToArray();

    [TestMethod]
    public void The_schema_describes_exactly_the_model() {
        CollectionAssert.AreEqual(Camel<JobsFile>(), JobsSchema.RootKeys.OrderBy(k => k).ToArray(),
            "JobsFile gained or lost a property — describe it in JobsSchema.Root");
        CollectionAssert.AreEqual(Camel<JobsFileEntry>(), JobsSchema.EntryKeys.OrderBy(k => k).ToArray(),
            "JobsFileEntry gained or lost a property — describe it in JobsSchema.Entry");
        CollectionAssert.AreEqual(Camel<NotifyRules>(), JobsSchema.NotifyKeys.OrderBy(k => k).ToArray(),
            "NotifyRules gained or lost a property — describe it in JobsSchema.Notify");
    }

    [TestMethod]
    public void The_published_schema_is_json_and_refuses_unknown_keys() {
        using var document = JsonDocument.Parse(JobsSchema.Json);
        var root = document.RootElement;
        Assert.IsFalse(root.GetProperty("additionalProperties").GetBoolean(),
            "the parser ignores unknown keys, so the schema must not");
        Assert.IsFalse(root.GetProperty("definitions").GetProperty("entry")
            .GetProperty("additionalProperties").GetBoolean());
        Assert.AreEqual("jobs", root.GetProperty("required")[0].GetString());
        Assert.AreEqual(1, root.GetProperty("properties").GetProperty("jobs").GetProperty("minItems").GetInt32());
    }

    [TestMethod]
    public void Every_field_carries_something_a_person_can_read() {
        foreach (var field in JobsSchema.Root.Concat(JobsSchema.Entry).Concat(JobsSchema.Notify)) {
            Assert.IsFalse(string.IsNullOrWhiteSpace(field.Description), field.Name);
        }
    }
}
