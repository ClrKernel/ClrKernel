using System;
using System.IO;
using System.Threading;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// Removing a test's temp workspace, on Windows as well.
///
/// <para>
/// <c>Directory.Delete(recursive: true)</c> is enough on Unix, where a file's own
/// permissions do not govern whether its directory entry can be removed. On Windows
/// they do, and **git marks every loose object read-only** — so a test that ran
/// <c>git init</c> could not clean up after itself, and the whole class failed in
/// <c>TestCleanup</c> with `Access to the path '&lt;40 hex&gt;' is denied` naming a
/// file nobody wrote by hand.
/// </para>
/// <para>
/// The retry is for the other Windows difference: a handle that has just been closed
/// — SQLite's pool, a kernel process that has just exited — can hold the file a
/// moment longer than the call that closed it.
/// </para>
/// </summary>
internal static class TempDirectory {
    public static void Delete(string path) {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) {
            try {
                File.SetAttributes(file, FileAttributes.Normal);
            } catch (Exception) {
                // Gone, or not ours to change: the delete below is what reports it.
            }
        }
        for (var attempt = 0; ; attempt++) {
            try {
                Directory.Delete(path, recursive: true);
                return;
            } catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException)
                                       && attempt < 4) {
                Thread.Sleep(100);
            } catch (Exception) {
                // A temp directory that will not go is not worth failing a green run
                // over — the OS clears it. Anything else here would turn a passing
                // test red for a reason that is not about the code under test.
                return;
            }
        }
    }
}
