using ClrKernel.Primitives;
using Newtonsoft.Json.Linq;

namespace ClrKernel.Script;

public static class Extensions {
    public static DisplayData HTML(string html) {
        return new DisplayData {
            Data = new JObject
            {
                { "text/html", html }
            }
        };
    }
}
