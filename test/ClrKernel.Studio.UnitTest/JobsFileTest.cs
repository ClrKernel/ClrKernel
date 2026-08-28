using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>*.jobs.yaml parsing: the defaults merge and its error cases.</summary>
[TestClass]
public class JobsFileTest {
    private string _dir;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-studio-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// Writes `test.jobs.yaml` with `test.nb.md` beside it — the pairing every
    /// jobs file now has, so most of these tests are about what is *inside* the
    /// file rather than which notebook it found.
    /// </summary>
    private IReadOnlyList<JobDefinition> Load(string yaml, string notebook = "test.nb.md") {
        var path = Path.Combine(_dir, "test.jobs.yaml");
        File.WriteAllText(path, yaml);
        if (notebook != null) {
            File.WriteAllText(Path.Combine(_dir, notebook), "```csharp\n1\n```\n");
        }
        return JobsFile.Load(path, _dir);
    }

    [TestMethod]
    public void Jobs_inherit_defaults_and_override_them() {
        var jobs = Load(
            """
            defaults:
              timeoutSeconds: 3600
              retryCount: 1
              parameters: {env: prod, region: us}
            jobs:
              - name: us
                cron: "0 2 * * *"
              - name: eu
                timeoutSeconds: 60
                parameters: {region: eu}
            """);

        Assert.AreEqual(2, jobs.Count);
        var us = jobs.Single(j => j.Name == "us");
        Assert.AreEqual(3600, us.TimeoutSeconds);
        Assert.AreEqual(1, us.RetryCount);
        Assert.AreEqual("0 2 * * *", us.Cron);
        Assert.AreEqual("us", us.Parameters["region"]);

        var eu = jobs.Single(j => j.Name == "eu");
        Assert.AreEqual(60, eu.TimeoutSeconds);
        Assert.IsNull(eu.Cron);
        Assert.AreEqual("eu", eu.Parameters["region"], "job parameters merge over defaults");
        Assert.AreEqual("prod", eu.Parameters["env"], "unset keys stay inherited");
    }

    /// <summary>
    /// Every job in a file runs the notebook the file is named for. They are
    /// schedules for one notebook, which is what makes "promote this file"
    /// answerable and stops prod holding a schedule whose notebook is missing.
    /// </summary>
    [TestMethod]
    public void Every_job_runs_the_notebook_the_file_is_named_for() {
        var jobs = Load(
            """
            jobs:
              - name: a
              - name: b
            """);
        foreach (var job in jobs) {
            Assert.AreEqual(Path.Combine(_dir, "test.nb.md"), job.NotebookPath);
            Assert.AreEqual("test.nb.md", job.NotebookRelative);
        }
    }

    [TestMethod]
    public void The_pairing_finds_any_notebook_kind() {
        var jobs = Load("jobs:\n  - name: a\n", notebook: "test.ipynb");
        Assert.AreEqual(Path.Combine(_dir, "test.ipynb"), jobs.Single().NotebookPath);
    }

    /// <summary>
    /// The file-level `notebook:` may stay — plenty of files have it and it reads
    /// well — but only if it says what is already true.
    /// </summary>
    [TestMethod]
    public void A_declared_notebook_may_repeat_the_pairing_and_nothing_else() {
        var jobs = Load("notebook: ./test.nb.md\njobs:\n  - name: a\n");
        Assert.AreEqual(Path.Combine(_dir, "test.nb.md"), jobs.Single().NotebookPath);

        var e = Assert.ThrowsExactly<InvalidDataException>(
            () => Load("notebook: ./other.nb.md\njobs:\n  - name: a\n"));
        StringAssert.Contains(e.Message, "not the notebook this file is named for");
    }

    [TestMethod]
    public void A_jobs_file_with_no_notebook_beside_it_is_an_error() {
        // It schedules nothing, and nothing is what it would do.
        var e = Assert.ThrowsExactly<InvalidDataException>(
            () => Load("jobs:\n  - name: a\n", notebook: null));
        StringAssert.Contains(e.Message, "No notebook beside this file");
    }

    [TestMethod]
    public void A_job_without_a_name_is_an_error() {
        var e = Assert.ThrowsExactly<InvalidDataException>(() => Load(
            """
            jobs:
              - cron: "* * * * *"
            """));
        StringAssert.Contains(e.Message, "name");
    }

    [TestMethod]
    public void An_empty_jobs_list_is_an_error() {
        Assert.ThrowsExactly<InvalidDataException>(() => Load("notebook: ./test.nb.md"));
    }
}
