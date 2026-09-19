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

   UI-only changes do not require a broker version bump.

3. If broker-loaded behavior changed, stop the current broker by its verified PID and exact shadow-copy path before rebuilding. Never stop it by process name:

   ```powershell
   $brokerRoot = [IO.Path]::GetFullPath(
       (Join-Path $env:LOCALAPPDATA 'CopilotBuddy\broker')) + [IO.Path]::DirectorySeparatorChar
   $brokerInfo = Get-CimInstance Win32_Process |
       Where-Object {
           $_.ExecutablePath -and
           [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith(
               $brokerRoot, [StringComparison]::OrdinalIgnoreCase) -and
           $_.CommandLine -match '(?i)(^|\s)--assistant-broker(\s|$)'
       } |
       Sort-Object CreationDate -Descending |
       Select-Object -First 1
   $oldBroker = if ($brokerInfo) { Get-Process -Id $brokerInfo.ProcessId -ErrorAction Stop }
   $oldBroker | Select-Object Id, Path, StartTime, Responding
   if ($oldBroker) {
       if ($oldBroker.Path -ne $brokerInfo.ExecutablePath) {
           throw "Broker PID $($oldBroker.Id) path changed before it could be stopped."
       }
       Stop-Process -Id $oldBroker.Id
       Wait-Process -Id $oldBroker.Id -Timeout 15 -ErrorAction SilentlyContinue
       if (Get-Process -Id $oldBroker.Id -ErrorAction SilentlyContinue) {
           throw "Copilot Buddy broker PID $($oldBroker.Id) did not stop."
       }
   }
   ```

4. Build, then test sequentially; parallel commands can contend for `CopilotBuddy.Core.dll`:

   ```powershell
   dotnet build .\src\CopilotBuddy.Composition\CopilotBuddy.Composition.csproj --no-restore --nologo
   dotnet test .\tests\CopilotBuddy.Core.Tests\CopilotBuddy.Core.Tests.csproj --no-restore --nologo
   ```

5. Launch the rebuilt UI as an attached asynchronous process, then verify its exact path:

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

6. If the broker was stopped, verify that launching the UI started a new broker from its exact shadow-copy path:

   ```powershell
   $newBrokerInfo = Get-CimInstance Win32_Process |
       Where-Object {
           $_.ExecutablePath -and
           [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith(
               $brokerRoot, [StringComparison]::OrdinalIgnoreCase) -and
           $_.CommandLine -match '(?i)(^|\s)--assistant-broker(\s|$)'
       } |
       Sort-Object CreationDate -Descending |
       Select-Object -First 1
   if (-not $newBrokerInfo) { throw 'The restarted Copilot Buddy broker was not found.' }
   $newBroker = Get-Process -Id $newBrokerInfo.ProcessId -ErrorAction Stop
   if ($newBroker.Path -ne $newBrokerInfo.ExecutablePath) {
       throw "Broker PID $($newBroker.Id) path does not match its command line record."
   }
   $newBroker | Select-Object Id, Path, StartTime, Responding
   ```

Report the old verified UI PID, build/test results, new UI PID, executable path, start time, and responsiveness. When broker-loaded behavior changed, also report the old and new verified broker PIDs, paths, start times, and responsiveness.
