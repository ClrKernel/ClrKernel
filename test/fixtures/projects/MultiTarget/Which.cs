namespace MultiTarget;

public static class Which {
#if NET9_0
    public const string Framework = "net9.0";
#else
    public const string Framework = "net8.0";
#endif
}
