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

    private IReadOnlyList<JobDefinition> Load(string yaml) {
        var path = Path.Combine(_dir, "test.jobs.yaml");
        File.WriteAllText(path, yaml);
        return JobsFile.Load(path, _dir);
    }

    [TestMethod]
    public void Jobs_inherit_defaults_and_override_them() {
        var jobs = Load(
            """
            notebook: ./nightly.nb.md
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

    [TestMethod]
    public void Both_jobs_resolve_the_shared_notebook_relative_to_the_yaml() {
        var jobs = Load(
            """
            notebook: ./sub/nb.nb.md
            jobs:
              - name: a
              - name: b
                notebook: ./other.nb.md
            """);
        Assert.AreEqual(Path.Combine(_dir, "sub", "nb.nb.md"), jobs.Single(j => j.Name == "a").NotebookPath);
        Assert.AreEqual(Path.Combine(_dir, "other.nb.md"), jobs.Single(j => j.Name == "b").NotebookPath);
        Assert.AreEqual("sub/nb.nb.md", jobs.Single(j => j.Name == "a").NotebookRelative);
    }

    [TestMethod]
    public void A_job_without_a_name_is_an_error() {
        var e = Assert.ThrowsExactly<InvalidDataException>(() => Load(
            """
            notebook: ./nb.nb.md
            jobs:
              - cron: "* * * * *"
            """));
        StringAssert.Contains(e.Message, "name");
    }

    [TestMethod]
    public void A_job_without_any_notebook_is_an_error() {
        var e = Assert.ThrowsExactly<InvalidDataException>(() => Load(
            """
            jobs:
              - name: orphan
            """));
        StringAssert.Contains(e.Message, "notebook");
    }

    [TestMethod]
    public void An_empty_jobs_list_is_an_error() {
        Assert.ThrowsExactly<InvalidDataException>(() => Load("notebook: ./nb.nb.md"));
    }
}
