namespace WithP2P;

public static class Shouter {
    public static string Shout(string name) => SimpleLib.Greeter.Greet(name).ToUpperInvariant();
}
