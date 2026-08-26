using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

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
    public void Registering_the_second_project_persists_the_first_one_too() {
        var finance = Path.Combine(_dir, "finance");
        Directory.CreateDirectory(finance);
        var registry = new ProjectRegistry(Options(), NullLoggerFactory.Instance);

        var created = registry.Register(new Project { Name = "Finance Close", Root = finance }, out _);

        Assert.AreEqual("finance-close", created.Slug, "the slug comes from the name");
        // The implicit project exists only in memory until something is written.
        // Persisting just the new one would delete it, and with it every run row
        // that names it.
        var onDisk = ProjectsFile.Read(Options().DataDir);
        CollectionAssert.AreEqual(
            new[] { "default", "finance-close" }, onDisk.Select(p => p.Slug).ToArray());
    }

    [TestMethod]
    public void A_project_cannot_overlap_one_already_registered() {
        var registry = new ProjectRegistry(Options(), NullLoggerFactory.Instance);
        var inside = Path.Combine(_dir, "notebooks", "reports");
        Directory.CreateDirectory(inside);

        // Both would find the same *.jobs.yaml, so the same job would be scheduled
        // twice under two project names. Either direction counts: the registered
        // project inside the new one is the same mistake as the other way round.
        foreach (var root in new[] { Path.Combine(_dir, "notebooks"), inside }) {
            var e = Assert.ThrowsExactly<ProjectRegistry.ProjectException>(
                () => registry.Register(new Project { Name = "Overlapping", Root = root }, out _));
            StringAssert.Contains(e.Message, "overlaps");
        }

        // A root above everything swallows the data directory as well, which is
        // the more alarming half and the one the message names.
        StringAssert.Contains(
            Assert.ThrowsExactly<ProjectRegistry.ProjectException>(
                () => registry.Register(new Project { Name = "Everything", Root = _dir }, out _))
                .Message,
            "data directory");
    }

    [TestMethod]
    public void Registration_refuses_a_taken_slug_and_makes_a_folder_that_is_not_there() {
        var registry = new ProjectRegistry(Options(), NullLoggerFactory.Instance);
        var finance = Path.Combine(_dir, "finance");
        Directory.CreateDirectory(finance);

        StringAssert.Contains(
            Assert.ThrowsExactly<ProjectRegistry.ProjectException>(() => registry.Register(
                new Project { Slug = "default", Name = "Clash", Root = finance }, out _)).Message,
            "already registered");
        StringAssert.Contains(
            Assert.ThrowsExactly<ProjectRegistry.ProjectException>(() => registry.Register(
                new Project { Name = "Relative", Root = "notebooks" }, out _)).Message,
            "absolute path");
        // Registering a project is how you make one; being sent to the box to run
        // mkdir first is not a workflow.
        var missing = Path.Combine(_dir, "brand", "new");
        Assert.AreEqual(missing, registry.Register(
            new Project { Name = "Brand New", Root = missing }, out var createdRoot).Root);
        Assert.IsTrue(createdRoot, "and it says it made it");
        Assert.IsTrue(Directory.Exists(missing));

        Assert.IsFalse(
            registry.Register(new Project { Name = "Adopted", Root = finance }, out _) == null);
    }

    [TestMethod]
    public void A_project_cannot_be_rooted_in_the_data_directory_or_on_a_file() {
        var registry = new ProjectRegistry(Options(), NullLoggerFactory.Instance);
        var file = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(file, "not a folder");

        StringAssert.Contains(
            Assert.ThrowsExactly<ProjectRegistry.ProjectException>(() => registry.Register(
                new Project { Name = "File", Root = file }, out _)).Message,
            "is a file");
        // The data directory holds the run history and the settings. A project
        // rooted inside it would hand the notebook editor a path to the database.
        StringAssert.Contains(
            Assert.ThrowsExactly<ProjectRegistry.ProjectException>(() => registry.Register(
                new Project { Name = "Inside", Root = Path.Combine(_dir, "data", "nested") },
                out _)).Message,
            "data directory");
    }

    [TestMethod]
    public void An_edit_cannot_move_a_project_or_rename_its_slug() {
        var registry = new ProjectRegistry(Options(), NullLoggerFactory.Instance);
        var root = registry.Default.Root;

        var updated = registry.Update("default", p => {
            p.Name = "Renamed";
            p.Slug = "something-else";
            p.Root = Path.Combine(_dir, "finance");
            p.RemoteMode = RemoteMode.ServerAuthoritative;
            p.Remote = "origin";
            p.RemoteSecret = "GIT_TOKEN";
        });

        Assert.AreEqual("Renamed", updated.Name);
        Assert.AreEqual("default", updated.Slug, "the slug is in every run row");
        Assert.AreEqual(root, updated.Root, "the history those rows describe happened here");
        Assert.AreEqual(RemoteMode.ServerAuthoritative, updated.RemoteMode);
        Assert.AreEqual("GIT_TOKEN", updated.RemoteSecret, "a secret reference, not a secret");
        Assert.IsNull(registry.Update("nope", _ => { }));
    }

    [TestMethod]
    public void Unregistering_leaves_the_folder_alone_and_refuses_the_last_project() {
        var finance = Path.Combine(_dir, "finance");
        Directory.CreateDirectory(finance);
        File.WriteAllText(Path.Combine(finance, "close.nb.md"), "```csharp\n1+1\n```\n");
        var registry = new ProjectRegistry(Options(), NullLoggerFactory.Instance);
        registry.Register(new Project { Slug = "finance", Name = "Finance", Root = finance }, out _);

        Assert.IsTrue(registry.Unregister("finance"));
        Assert.IsNull(registry.Find("finance"));
        Assert.IsTrue(File.Exists(Path.Combine(finance, "close.nb.md")),
            "unregistering forgets a project; it does not delete one");
        Assert.IsFalse(registry.Unregister("finance"), "already gone");

        StringAssert.Contains(
            Assert.ThrowsExactly<ProjectRegistry.ProjectException>(
                () => registry.Unregister("default")).Message,
            "only project");
    }

    [TestMethod]
    [DataRow("My Notebooks", "my-notebooks")]
    [DataRow("finance/close", "finance-close")]
    [DataRow("  Spaced  ", "spaced")]
    [DataRow("!!!", "project")]
    [DataRow("", "project")]
    public void Slugs_are_url_safe(string name, string expected) =>
        Assert.AreEqual(expected, Project.SlugFor(name));
}
