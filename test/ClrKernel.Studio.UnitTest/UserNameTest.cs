using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// The handle rules. Every one of these is a git refname rule, a filesystem rule or
/// a rule about the branches this workspace already has — the charset was measured
/// with <c>git check-ref-format</c> rather than guessed.
/// </summary>
[TestClass]
public class UserNameTest {
    [TestMethod]
    public void Names_git_and_a_filesystem_both_accept() {
        foreach (var name in new[] { "jeremy", "j.adams", "jeremy-adams", "jeremy_adams", "a", "u2" }) {
            Assert.IsTrue(UserName.IsValid(name), $"{name}: {UserName.Problem(name)}");
        }
    }

    /// <summary>
    /// Two of these git would accept as a branch and this refuses anyway: a leading
    /// dash is read as a flag by every tool that later takes the name as an
    /// argument, and an upper-case name is the same directory as its lower-case
    /// twin on macOS and Windows.
    /// </summary>
    [TestMethod]
    public void Names_that_would_break_a_ref_a_folder_or_a_command_line() {
        var rejected = new[] {
            ("j adams", "a space is not a legal refname"),
            ("DOMAIN\\jadams", "a backslash is not a legal refname"),
            ("jeremy.", "git refuses a trailing dot"),
            ("-jeremy", "a valid ref, but reads as a flag"),
            ("Jeremy", "the same folder as jeremy on macOS and Windows"),
            ("jeremy@corp.com", "@ is outside the charset"),
            (".jeremy", "starts with punctuation"),
            ("", "empty"),
            (null, "null"),
        };
        foreach (var (name, why) in rejected) {
            Assert.IsFalse(UserName.IsValid(name), $"{name ?? "(null)"} should be refused: {why}");
            Assert.IsNotNull(UserName.Problem(name), "and the refusal says why");
        }
        Assert.IsFalse(UserName.IsValid(new string('a', UserName.MaxLength + 1)), "too long");
        Assert.IsTrue(UserName.IsValid(new string('a', UserName.MaxLength)), "exactly the limit is fine");
    }

    /// <summary>
    /// A person called `test` would own the branch the whole workflow promotes from,
    /// and one called `mine` would be unreachable — that word is the URL alias for
    /// "my own branch".
    /// </summary>
    [TestMethod]
    public void Names_that_are_already_branches() {
        foreach (var name in new[] { "test", "main", "dev", "mine" }) {
            Assert.IsFalse(UserName.IsValid(name), $"{name} names a branch");
            StringAssert.Contains(UserName.Problem(name), "branch");
        }

        // Not reserved, and deliberately so: it is the fallback Suggest returns, and
        // `user/user` is a perfectly good branch name.
        Assert.IsTrue(UserName.IsValid("user"));
    }

    [TestMethod]
    public void Suggest_turns_a_display_name_into_something_usable() {
        Assert.AreEqual("jeremy-adams", UserName.Suggest("Jeremy Adams"));
        Assert.AreEqual("o-brien", UserName.Suggest("O'Brien"));
        Assert.AreEqual("jose-garcia", UserName.Suggest("  José  García  "), "non-ascii is dropped, not mangled");

        // Whatever it returns is something the validator accepts — including for
        // input that has nothing usable in it at all.
        foreach (var input in new[] { "", "   ", "!!!", "试验", null, "test", "mine" }) {
            var suggestion = UserName.Suggest(input);
            Assert.IsTrue(UserName.IsValid(suggestion),
                $"Suggest({input ?? "null"}) = '{suggestion}', which is not valid");
        }
        Assert.IsTrue(UserName.Suggest(new string('x', 200)).Length <= UserName.MaxLength);
    }

    [TestMethod]
    public void Unique_appends_only_when_it_has_to() {
        Assert.AreEqual("jeremy", UserName.Unique("jeremy", Array.Empty<string>()));
        Assert.AreEqual("jeremy2", UserName.Unique("jeremy", new[] { "jeremy" }));
        Assert.AreEqual("jeremy3", UserName.Unique("jeremy", new[] { "jeremy", "jeremy2" }));

        // Case-insensitively taken: the folder is the same one.
        Assert.AreEqual("jeremy2", UserName.Unique("jeremy", new[] { "JEREMY" }));

        // The suffix must not push it over the limit, and the result stays valid.
        var atLimit = new string('a', UserName.MaxLength);
        var next = UserName.Unique(atLimit, new[] { atLimit });
        Assert.IsTrue(next.Length <= UserName.MaxLength, $"'{next}' is {next.Length} characters");
        Assert.IsTrue(UserName.IsValid(next));
        Assert.AreNotEqual(atLimit, next);
    }
}
