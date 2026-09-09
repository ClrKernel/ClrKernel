namespace BuildFails;

public static class Broken {
    // CS0029 on purpose: the test asserts MSBuild's own text reaches the cell.
    public static int Count = "not a number";
}
