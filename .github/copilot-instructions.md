# Copilot Buddy repository instructions

## Rebuild and rerun

`CopilotBuddy.Composition.exe` locks Debug output. The assistant broker survives UI restarts and can keep stale `Core`/`Copilot` code. Never stop processes by name.

1. Stop only the running repository UI whose `Path` exactly matches the expected executable:

   ```powershell
   $exe = (Resolve-Path '.\src\CopilotBuddy.Composition\bin\Debug\net10.0-windows10.0.22000.0\CopilotBuddy.Composition.exe').Path
   $old = Get-Process | Where-Object { $_.Path -eq $exe } |
       Sort-Object StartTime -Descending | Select-Object -First 1
   $old | Select-Object Id, Path, StartTime, Responding
   if ($old) {
       Stop-Process -Id $old.Id
       Wait-Process -Id $old.Id -Timeout 15 -ErrorAction SilentlyContinue
       if (Get-Process -Id $old.Id -ErrorAction SilentlyContinue) {
           throw "Copilot Buddy PID $($old.Id) did not stop."
       }
   }
   ```

2. If broker-loaded behavior changed, increment these together before rebuilding:

   - `AssistantBrokerProtocol.Version`
   - `AssistantBrokerProtocol.PipeName`
   - the broker mutex in `CopilotSessionBrokerHost`

   This forces the UI to start a fresh hash-copied broker under `%LOCALAPPDATA%\CopilotBuddy\broker`. UI-only changes do not require a broker version bump.

3. Build, then test sequentially; parallel commands can contend for `CopilotBuddy.Core.dll`:

   ```powershell
   dotnet build .\src\CopilotBuddy.Composition\CopilotBuddy.Composition.csproj --no-restore --nologo
   dotnet test .\tests\CopilotBuddy.Core.Tests\CopilotBuddy.Core.Tests.csproj --no-restore --nologo
   ```

4. Launch the rebuilt UI as an attached asynchronous process, then verify its exact path:

   ```powershell
   $exe = (Resolve-Path '.\src\CopilotBuddy.Composition\bin\Debug\net10.0-windows10.0.22000.0\CopilotBuddy.Composition.exe').Path
   & $exe
   ```

   ```powershell
   $new = Get-Process | Where-Object { $_.Path -eq $exe } |
       Sort-Object StartTime -Descending | Select-Object -First 1
   if (-not $new) { throw 'The rebuilt Copilot Buddy process was not found.' }
   $new | Select-Object Id, Path, StartTime, Responding
   ```

   Run the launch command with the PowerShell tool in async mode and do not detach it.

Report the old verified PID, build/test results, new PID, executable path, start time, and responsiveness.
