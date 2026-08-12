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

```powershell
# Colored output using modern PSStyle
Write-Output "$($PSStyle.Foreground.Green)Success:$($PSStyle.Reset) File uploaded."
Write-Output "$($PSStyle.Foreground.Red)Error:$($PSStyle.Reset) Connection failed."
```

```powershell
# Green text (\e[32m) and Reset (\e[0m)
Write-Output "`e[32mThis text is green`e[0m"

# Red text (\e[31m)
Write-Output "`e[31mThis text is red`e[0m"
Write-Output "`e[01;32mBob@example.com`e[00m:`e[01;34mC:/bash`e[00m"

echo "console.log('\x1b[32m%s\x1b[0m', 'Hello World');" | bun run -
```

## Mixing languages

C# and PowerShell cells share the same notebook — use whichever fits the step.

```csharp
Console.WriteLine("C# and PowerShell, one notebook.");
```

```csharp
var x = 10;
```

```csharp
x
```

```csharp
x.Display(); // semicolon stops displayed value from showing this should show 10 with (i) int type like
```
