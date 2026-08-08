# PowerShell in ClrKernel

ClrKernel runs **PowerShell cells** in an in-process runspace. In a notebook,
set a cell's language to **PowerShell** (or start it with the `#!pwsh` selector);
in a plain `.nb.md` file, use a ` ```powershell ` fenced block. Variables and
functions persist across cells, and you get native tab-completion for cmdlets,
parameters, paths, and session variables. No separate PowerShell install is
needed — it's hosted in-process.

## State persists across cells

```powershell
$service = 'ClrKernel'
$version = [Version]'0.7.0'
"$service $version"
```

```powershell
# $service and $version are still in scope here
Write-Host "Deploying $service v$version"
```

## Rich object output

Objects render the way the console formats them.

```powershell
Get-Command Get-Date, Get-Location |
  Select-Object Name, CommandType
```

## Mixing languages

C# and PowerShell cells share the same notebook — use whichever fits the step.

```csharp
Console.WriteLine("C# and PowerShell, one notebook.");
```
