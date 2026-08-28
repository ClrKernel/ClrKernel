using System.IO;
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// A jobs file and its notebook are named for each other. The rule is load-bearing
/// — it is what makes "promote this file" have one answer, and what stops prod
/// holding a schedule whose notebook is missing — so the naming edges get tests.
/// </summary>
[TestClass]
public class JobsPairingTest {
    private string _dir;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-pairing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    [TestMethod]
    public void The_base_name_is_the_name_minus_its_known_extension() {
        // Not "everything before the first dot", which turned
        // `quarterly.report.nb.md` into `quarterly` and paired it with nothing.
        Assert.AreEqual("quarterly.report", JobsPairing.BaseNameOfNotebook("a/quarterly.report.nb.md"));
        Assert.AreEqual("quarterly.report", JobsPairing.BaseNameOfJobsFile("a/quarterly.report.jobs.yaml"));
        Assert.AreEqual("etl", JobsPairing.BaseNameOfNotebook("etl.ipynb"));
        Assert.IsNull(JobsPairing.BaseNameOfNotebook("readme.txt"));
        Assert.IsNull(JobsPairing.BaseNameOfJobsFile("docker-compose.yaml"));
    }

    [TestMethod]
    public void A_notebook_names_exactly_one_jobs_file_whether_or_not_it_exists() {
        Assert.AreEqual(
            Path.Combine("reports", "daily.jobs.yaml"),
            JobsPairing.JobsFileFor(Path.Combine("reports", "daily.nb.md")));
        Assert.IsNull(JobsPairing.JobsFileFor("readme.txt"));
    }

    [TestMethod]
    public void A_jobs_file_finds_whichever_notebook_kind_is_actually_there() {
        // The other direction has to look, because a notebook could be any of
        // four extensions and only one of them is on disk.
        File.WriteAllText(Path.Combine(_dir, "etl.ipynb"), "{}");
        Assert.AreEqual(
            Path.Combine(_dir, "etl.ipynb"),
            JobsPairing.NotebookFor(Path.Combine(_dir, "etl.jobs.yaml")));
    }

    [TestMethod]
    public void And_says_so_when_there_is_none() {
        Assert.IsNull(JobsPairing.NotebookFor(Path.Combine(_dir, "orphan.jobs.yaml")));
    }

    [TestMethod]
    public void A_declared_notebook_matches_only_the_sibling_it_is_paired_with() {
        var path = "reports/daily.jobs.yaml";
        Assert.IsTrue(JobsPairing.Matches(path, "./daily.nb.md"));
        Assert.IsTrue(JobsPairing.Matches(path, "daily.nb.md"));
        Assert.IsTrue(JobsPairing.Matches(path, null), "declaring nothing is fine");
        Assert.IsFalse(JobsPairing.Matches(path, "./weekly.nb.md"));
        // Right base name, wrong file: a sibling is the only thing it can be.
        Assert.IsFalse(JobsPairing.Matches(path, "../other/daily.nb.md"));
        Assert.IsFalse(JobsPairing.Matches(path, "sub/daily.nb.md"));
    }
}
