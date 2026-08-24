using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Jobs.UnitTest;

/// <summary>
/// The registry that turns a slug into a workspace. Two properties carry the
/// design: a server that registered nothing still has exactly one project — called
/// <c>default</c>, so run history written before projects existed still matches —
/// and a slug nobody registered resolves to nothing rather than to the first one.
/// </summary>
[TestClass]
public class ProjectRegistryTest {
    private string _dir;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-projects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "data"));
        Directory.CreateDirectory(Path.Combine(_dir, "notebooks"));
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    private JobsOptions Options(bool git = false) => new() {
        DataDir = Path.Combine(_dir, "data"),
        NotebooksRoot = Path.Combine(_dir, "notebooks"),
        GitEnabled = git,
    };

    [TestMethod]
    public void With_no_projects_file_the_notebooks_root_is_the_default_project() {
        var registry = new ProjectRegistry(Options(), NullLoggerFactory.Instance);

        Assert.AreEqual(1, registry.Projects.Count);
        Assert.AreEqual(ProjectRegistry.DefaultSlug, registry.Default.Slug);
        Assert.AreEqual("notebooks", registry.Default.Name, "the folder name is the display name");
        Assert.AreEqual(Path.Combine(_dir, "notebooks"), registry.Default.Root);
        Assert.IsFalse(File.Exists(ProjectsFile.PathIn(Options().DataDir)),
            "nothing was registered, so nothing is written");
    }

    [TestMethod]
    public void A_slug_nobody_registered_resolves_to_nothing() {
        var registry = new ProjectRegistry(Options(), NullLoggerFactory.Instance);

        Assert.IsNotNull(registry.Find("DEFAULT"), "slugs are matched case-insensitively");
        Assert.IsNull(registry.Find("finance"));
        Assert.IsNull(registry.Find(""));
        Assert.IsNull(registry.Find(null));
    }

    [TestMethod]
    public void Registered_projects_are_scanned_separately_and_tagged() {
        var finance = Path.Combine(_dir, "finance");
        Directory.CreateDirectory(finance);
        File.WriteAllText(Path.Combine(finance, "close.nb.md"), "```csharp\n1+1\n```\n");
        File.WriteAllText(Path.Combine(finance, "close.jobs.yaml"),
            "notebook: ./close.nb.md\njobs: [{name: nightly}]\n");

        var notebooks = Path.Combine(_dir, "notebooks");
        File.WriteAllText(Path.Combine(notebooks, "etl.nb.md"), "```csharp\n1+1\n```\n");
        File.WriteAllText(Path.Combine(notebooks, "etl.jobs.yaml"),
            "notebook: ./etl.nb.md\njobs: [{name: nightly}]\n");

        ProjectsFile.Write(Options().DataDir, new[] {
            new Project { Slug = "default", Name = "Notebooks", Root = notebooks },
            new Project { Slug = "finance", Name = "Finance", Root = finance },
        });

        var registry = new ProjectRegistry(Options(), NullLoggerFactory.Instance);
        var result = registry.LoadAll();

        // The same job name in both: legal, and the whole reason the project is
        // part of every key downstream.
        Assert.AreEqual(2, result.Jobs.Count);
        Assert.AreEqual("close.nb.md", result.Find("finance", "default", "nightly").NotebookRelative);
        Assert.AreEqual("etl.nb.md", result.Find("default", "default", "nightly").NotebookRelative);
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public void One_git_service_per_project_because_it_owns_the_workspace_lock() {
        var registry = new ProjectRegistry(Options(git: true), NullLoggerFactory.Instance);

        var first = registry.GitFor(registry.Default);
        Assert.IsNotNull(first);
        Assert.AreSame(first, registry.GitFor(registry.Default));
        Assert.AreSame(
            registry.CatalogFor(registry.Default), registry.CatalogFor(registry.Default));
    }

    [TestMethod]
    public void A_project_without_the_git_workflow_has_no_git_layer() =>
        Assert.IsNull(new ProjectRegistry(Options(), NullLoggerFactory.Instance)
            .GitFor(new ProjectRegistry(Options(), NullLoggerFactory.Instance).Default));

    [TestMethod]
    [DataRow("My Notebooks", "my-notebooks")]
    [DataRow("finance/close", "finance-close")]
    [DataRow("  Spaced  ", "spaced")]
    [DataRow("!!!", "project")]
    [DataRow("", "project")]
    public void Slugs_are_url_safe(string name, string expected) =>
        Assert.AreEqual(expected, Project.SlugFor(name));
}
