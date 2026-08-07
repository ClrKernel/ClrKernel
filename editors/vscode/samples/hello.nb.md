# ClrKernel executable markdown

This file is a notebook in VS Code (via the ClrKernel Notebooks extension) and
a plain markdown document everywhere else. The same file can be imported into
any ClrKernel session with `#!import "hello.nb.md"` — the csharp fences run,
the prose doesn't.

```csharp
var greeting = "Hello from ClrKernel";
Console.WriteLine(greeting);
```

State persists between cells, exactly like a Jupyter session:

```csharp
Console.WriteLine(greeting.ToUpper() + "!");
```

NuGet references work too:

```csharp
#r "nuget: Humanizer"
using Humanizer;
Console.WriteLine(TimeSpan.FromDays(45).Humanize());
```

```csharp
#r "nuget: YamlDotNet, 16.3.0"
using YamlDotNet.Serialization;
Console.WriteLine("YamlDotNet loaded");
var yaml = @"name: ClrKernel
version: 0.3.0
tags:
  - jupyter
  - dotnet";
  var deserializer = new Deseriali
```
