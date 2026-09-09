using Microsoft.Extensions.Logging;

namespace WithDepConflict;

public static class Loud {
    // Takes the kernel's ILogger, or the cell could not hand one over: the type
    // has to unify with the copy already loaded, not split into two.
    public static string Announce(ILogger logger, string what) {
        logger.LogInformation("{What}", what);
        return what.ToUpperInvariant();
    }
}
