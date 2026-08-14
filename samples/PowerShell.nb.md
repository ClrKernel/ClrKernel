# PowerShell in ClrKernel

## PSRemoting

Register a remote target once, then run any PowerShell cell on it with
`--connection` — the remote runspace persists, so remote variables and imports
carry across cells exactly like the local session:

```powershell
#!pwsh-connect --name srv --host srv01.example.com --user admin --identity ~/.ssh/id_ed25519
```

```powershell
#!pwsh --connection srv
$env:COMPUTERNAME
Get-Process | Select-Object -First 5
```

`--ssh` (the default) uses PowerShell-over-SSH: key auth, and the remote needs
PowerShell with the ssh subsystem enabled. `--winrm` uses classic PSRemoting:
`--user CONTOSO\svc --secret ps:srv01` takes the password from your OS
credential store by reference — it is never written to the notebook or any
file. Targets can also live in `connections.json` as `"$type": "PSRemoting"`
(or reuse a shell `"$type": "Ssh"` entry).

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
