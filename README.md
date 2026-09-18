# Copilot Buddy

Copilot Buddy is a .NET 10 Windows Composition desktop companion that wanders along the primary Windows 11 taskbar. A companion `copilot-buddyctl` executable drives its event-agnostic presentation API over a current-user named pipe.

## Prerequisites

- Windows 11
- .NET 10 SDK 10.0.401 or a later .NET 10 feature band
- At least one seven-frame sprite sheet in `src/CopilotBuddy.Composition/Assets/Sprites/Buddies`

Buddy sprite sheets are horizontal 98x20 PNGs with seven 14x20 frames in this order: standing, walk left 1, walk left 2, walk right 1, walk right 2, wave 1, wave 2. Add a correctly sized `<buddy-name>.png` file to `src/CopilotBuddy.Composition/Assets/Sprites/Buddies` to make it available in settings; the filename becomes its display name and the top half of its standing frame becomes its avatar. Sprout is selected by default. Frame rectangles and animation/layout values are in `src/CopilotBuddy.Composition/presentation.json`. Idle breathing uses a subtle bottom-anchored squash and stretch; its duration and amount are configurable there and reduced-motion mode disables it.

## Build and test

```powershell
dotnet restore CopilotBuddy.slnx
dotnet build CopilotBuddy.slnx
dotnet test tests/CopilotBuddy.Core.Tests/CopilotBuddy.Core.Tests.csproj
```

## Run

Start the host:

```powershell
dotnet run --project src/CopilotBuddy.Composition
```

In another terminal, exercise the presentation API:

```powershell
dotnet run --project src/CopilotBuddy.Cli -- show "Build needs your approval"
dotnet run --project src/CopilotBuddy.Cli -- dismiss
dotnet run --project src/CopilotBuddy.Cli -- visible off
dotnet run --project src/CopilotBuddy.Cli -- visible on
```

The built CLI is named `copilot-buddyctl.exe`. Messages pause wandering and display a bounded, wrapped speech bubble while the buddy waves and hops. Long messages are truncated with an ellipsis. Reduced-motion mode disables hops and uses slower waving. Commands received while dragging or flying update the bubble immediately; attention animation resumes after landing.

Hovering pauses wandering and switches to the standing breathing pose; leaving starts a fresh idle interval. A left click toggles a demo message. Click and hold to drag with spring-like cursor play; release while moving to fling the buddy, which falls back to the taskbar and squishes on impact. Moving at least three DIP suppresses the click action. A right click on the buddy, bubble, or tray icon opens Show demo, Dismiss, Copilot Buddy visible, and Exit commands alongside the rendering options. Only opaque sprite pixels and visible bubble/menu content receive pointer input.

`visible off` hides both buddy and bubble, cancels active dragging/flight, and leaves the pipe and tray icon available. `visible on` restores the buddy and any retained message. The pipe is restricted to the current Windows user and retains protocol version 1, request validation, and structured success/error responses.

## Copilot Sessions

With the buddy running, **Alt+Enter** summons the oldest queued Copilot CLI session when one needs attention, focusing its window and dismissing its alert just like clicking the message popup. With no pending session action, it opens Copilot CLI using the Windows default terminal application in the buddy's working directory. The buddy briefly sparkles when the terminal launches. **Summon** is also available in the pixel skills menu and tray menu; the tray's **Summon shortcut** command lets you press a replacement key combination to save it. If another application owns the shortcut, the buddy reports the conflict and the menu remains available. Terminal window/tab placement follows your terminal settings; automatic terminal-tab selection is not supported. Under Windows Terminal, Copilot Buddy associates the foreground terminal window with the most recently opened or explicitly focused managed session because the child `copilot.exe` process does not own the top-level terminal window.

Press **Alt+Space** or right-click the buddy or its bubble to open the pixel skills menu beside the buddy. **Handoff** (**Alt+Shift+H**) captures the currently focused buddy-managed Copilot session before opening its question window, so the modal dialog cannot change the handoff target. It then asks whether the output should be a spec, research instructions, concrete implementation instructions with file and line details, or custom freeform instructions. The captured Copilot session writes a redacted handoff document to a specific unique path under the OS temporary directory.

**Gather** (**Alt+Shift+T**) restores and tiles every distinct terminal window controlled by Copilot Buddy into equal cells across the primary screen's working area. Buddy then hops to each tiled window in turn before returning to the taskbar. Other skills are ignored until he lands. Windows Terminal tabs that share one top-level window are gathered as one window, and unrelated terminal windows are left untouched.

The menu's **Supplies** section contains a ball, food, water, and chair. One of each can be deployed at the same time; a deployed item's solid icon becomes a clickable silhouette that recalls it. Select an available item to attach it to the pointer, then click where it should fall onto the taskbar. Landed items can be picked up, caught, dragged, and thrown until Buddy physically holds or consumes them. Moving an item cancels and resets its current approach or play sequence, and redropping it places it at the end of the oldest-first supply queue.

Skills take priority over an in-progress supply approach or chair transition. Buddy preserves his rendered position, performs the skill animation, then resumes pursuing the queued supply instead of teleporting or discarding the objective.

When an alert hop ends as a supply becomes actionable, Buddy completes a short landing before beginning the horizontal approach. The walk never stretches a partially completed hop across the distance to the item.

Copilot Buddy runs over to collect food and water. Food is lifted to his face for a three-bite "munch munch munch" animation with flying pixel crumbs. Water tilts through three "glug glug glug" sips while blue droplets spill and splash. With the ball, he plays for a randomized number of rounds, throwing it with varied strength and direction, then chasing it after its gravity-driven arc and damped taskbar bounces. Each interaction ends with a heart. Hunger, thirst, and play meters deplete independently at slightly randomized rates; the buddy asks for the lowest need in its bubble. A hidden relationship score rewards timely care and declines during prolonged neglect.

After higher-priority actions and queued consumables finish, Buddy walks to a deployed chair, hops into its bucket seat, and stops autonomous wandering while needs, bubbles, alerts, reactions, and explicit actions continue. Actions that require movement make him hop out and return afterward. Dragging or throwing either Buddy or the occupied chair moves them as one unit, and he remains seated after landing. Clicking the chair silhouette hops him out, restores the chair to Supplies, and resumes wandering. Chair state is intentionally transient across restarts.

Actionable Copilot alerts always take priority over supplies. A carried item, falling item, ball flight, eating/drinking animation, Buddy chase, or chair transition pauses when an alert appears and resumes only after that alert is acknowledged.

**Inquire** (**Alt+Shift+G**) captures the most recently focused buddy-managed Copilot session, jumps the buddy to that terminal window, injects the `grilling` skill prompt into that exact session, then jumps back down to the taskbar. The prompt makes Copilot stress-test a plan, decision, or idea as a dependency-aware design tree, asking each currently unblocked frontier of decisions as one recommended-answer round and waiting for the user's answers before expanding the tree. The buddy sparkles while a brief sparkle traces the injected terminal window's full border.

Copilot Buddy plays the same triumphant jump used when opening a session as it drops a present near its current position. The present follows a light toss arc, lands, and makes one subtle finishing skip. Presents choose from fun preset colors, with an occasional animated rainbow treatment. When the source session reports that it is done and the file is available, Copilot Buddy gracefully closes the terminal window associated with that source session and the present displays a clickable "<session> handoff ready to open" bubble. It does not delete the session through the Copilot API. Clicking the present or bubble pops it open with a small bounce away from the click and launches a fresh Copilot session instructed to continue from that exact handoff file. The present rendering and file-readiness behavior are provider-neutral; the Copilot adapter owns session prompts, completion signals, window closure, and continuation launch.

**Store** (**Alt+Shift+S**) captures the most recently highlighted buddy-managed Copilot session, saves its session ID, title, and working directory in `%LOCALAPPDATA%\CopilotBuddy\stored-sessions.json`, drops it into its own present, and then closes the associated terminal window. Hovering over a stored present shows which session it contains. Clicking the present pops it open, launches Copilot CLI with that saved session ID, and attaches Copilot Buddy to the resumed session normally. Multiple stored sessions can coexist, and the broker reloads and replays their presents after Copilot Buddy, broker, or Windows restarts. A stored record is removed only after the saved session resumes and SDK attachment succeeds.

Install GitHub Copilot CLI with `winget install --id GitHub.Copilot --exact` and complete its sign-in flow. This integration uses GitHub.Copilot.SDK 1.0.14 and currently pins the interactive runtime to CLI 1.0.86-2, which must already be extracted in the CLI's per-user package cache. The SDK runtime download is disabled because the actual interactive CLI is used. CLI authentication, directory trust, tool approvals, questions, and plan decisions remain in the terminal; the buddy does not approve requests or submit prompts.

CLI 1.0.86-2 has a UI-server bug: its embedded-server constructor omits the connection token, causing SDK attachment to fail with `AUTHENTICATION_NOT_CONFIGURED`. `CopilotBuddy.Copilot` creates an isolated copy at `%LOCALAPPDATA%\CopilotBuddy\copilot-runtime\1.0.86-2-ui-auth-no-ide-v2`, restores that constructor argument, removes the token from the CLI environment once consumed, and disables IDE auto-connect for Copilot Buddy-owned terminal sessions. The original installation/cache and global Copilot settings are not modified. Only buddy-launched processes select this copy through `COPILOT_CLI_DIST_DIR`, with auto-update disabled. The workaround validates the package version and exact patched call sites before changing them; updating this pinned runtime requires reviewing the workaround rather than silently patching another version.

Attachment diagnostics are under `%LOCALAPPDATA%\CopilotBuddy\connections`, with stage, loopback port, process ID, and redacted errors. `--open-copilot` launches one CLI session after the host initializes. The VS Code tasks **Build Copilot attachment fix**, **Try Copilot attachment once**, and **Verify Copilot attachment authentication** provide bounded checks. The last sends only an intentionally incorrect connection token to an attached listener and must be rejected; it does not send a model prompt.

Permission requests, questions (including elicitation), plan review, errors, and completed work each select from a dedicated pool of 50 funny messages, for 250 messages total. Pending sessions are queued in arrival order. Left-click the buddy or its message bubble to focus the oldest session and remove its alert. Manually focusing or switching to a managed session also removes that session's queued alert, and closing its window or ending the session clears it through the existing closure signal. Bubbles appear without stealing focus; a queued-session bubble permits activation when clicked. Each extra pending session adds a small floating orb. Repeated events from one pending session update its message without moving it in the queue. A failed focus leaves the alert queued. Demo and pipe messages cannot replace queued session alerts. With no queued alerts, clicking the buddy retains the demo-message toggle; clicking the bubble does nothing.

### Context pressure

A tracked session is context-heavy when its **current context** reaches **140,000 tokens** or exceeds **40% of its context limit**, whichever happens first. While any session is heavy, the buddy walks at half speed and shows cyan pixel sweat drops. Idle breathing, attention bounces, and drag/release physics are unchanged. Reduced-motion mode uses stationary drops.

Click the sweating buddy when no normal alert is displayed to show: "X session is getting heavy on context. I recommend compacting or handing off to a new session". Click the buddy or that bubble again to focus the selected session. X is the reported session title, falling back to a short session ID until a title arrives. Existing normal alerts keep their click-to-focus behavior; alerts arriving while a context warning is displayed remain queued underneath it. Failed focus keeps the warning visible. Focusing does not clear heavy usage; reported recovery below both thresholds or session closure does. Nothing compacts or hands off automatically.

The replaceable policy and two-click state live in `src/CopilotBuddy.Core/ContextPressure.cs`; host interaction and sweat visuals live in `src/CopilotBuddy.Composition/ReplayWindow.ContextPressure.cs`. The Copilot adapter only forwards provider-neutral usage/title updates. Usage comes from `session.usage_info` and successful compaction events, not cumulative input/output token counts. The percentage rule is unavailable until a positive context limit is reported; the absolute threshold still applies.

With the normal buddy exited, run `dotnet run --file tools/Test-ContextPressure.cs -- --windowed` for a bounded synthetic-provider UI check. It covers warning/focus behavior, alert coexistence, sweat cleanup, and recovery without launching a CLI or submitting a prompt. Small preview captures are saved under `artifacts/context-pressure`.

Settings, including the selected buddy sprite, are saved to `%LOCALAPPDATA%\CopilotBuddy\settings.json`. You can also create/edit that file and restart the buddy:

```json
{
	"shortcut": "Alt+Enter",
	"buddy": "sprout",
	"workingDirectory": "C:\\source\\copilot-buddy",
	"copilotLaunch": {
		"executable": "custom-launcher",
		"arguments": ["copilot", "--custom-option"]
	}
}
```

Open the pixel skills menu and select the gear at its top-right edge to choose a buddy and edit the Copilot launch command. The friendly command line is stored as a structured executable and prefix-argument array; Copilot Buddy appends its required UI-server, port, and session arguments. For example, `custom-launcher copilot --custom-option` produces the configuration above. Use `copilot` for the normal launch behavior. `workingDirectory: null` uses the directory the buddy was started from. For a normal launch, the adapter checks `COPILOT_CLI_PATH`, PATH, and the per-user WinGet installation. The legacy `cliPath` setting remains readable and is migrated when launch settings are saved. No GitHub credentials are stored in these settings. SDK connections use loopback and a random per-window connection token passed through the child environment.

This version observes only CLI windows launched by the buddy, including sessions created/switched inside those windows. It does not attach to arbitrary existing terminals. Exiting the visual Copilot Buddy unregisters its shortcuts and leaves both the broker and CLI windows running, allowing the next visual process to reconnect and recover pending alerts and current context state. If attachment fails, check the CLI's first-run prompts, close that window, and reopen it through the buddy. Hidden buddies retain pending alerts, which appear when made visible again. Replay/smoke modes do not register the shortcut or launch Copilot.

### In-memory connection broker

Normal launches start or reconnect to a lightweight current-user assistant broker. The broker owns `CopilotSessions`, UI-server ports, and connection tokens in memory, while the visual Copilot Buddy communicates with it over a current-user named pipe. Restarting or crashing the visual process therefore does not lose attachments to Copilot Buddy-launched CLI windows. The broker also retains current pending alerts and context-usage snapshots and replays them to the next visual client. Stored-session metadata is the exception to the broker's otherwise in-memory state: it is persisted without credentials so stored presents survive broker and Windows restarts.

No connection token is written to settings, diagnostics, or a reconnect registry. The broker runs from a versioned shadow copy under `%LOCALAPPDATA%\CopilotBuddy\broker` so its long lifetime does not lock the main application binaries during rebuilds or updates. The broker remains alive after the visual Copilot Buddy exits; it is scoped to the current Windows user and still does not discover arbitrary Copilot terminals.

## Windows Composition host

The default mode is a transparent, borderless, topmost overlay with no taskbar button. The buddy wanders along the primary taskbar, alternates walking frames, breathes while idle, and pauses on hover. Dragging uses spring lag; release flings the buddy with gravity, wall bounces, and a landing squash. Right-click the buddy or its notification-area icon and choose Exit. The normal overlay stays open until exited.

A Windows Forms window supplies native window and input plumbing; Windows Composition plays movement, deformation, walking, and attention animations. The speech bubble uses a separate non-activating owned window with native text rendering. During dragging, one uninterrupted compositor expression follows the latest pointer position and briefly extrapolates its velocity between input samples. Target updates change only composition properties; they do not restart the visual animation. Small prediction corrections decay continuously instead of snapping. An `InteractionTracker` follows the expression and drives the sprite. Release transitions directly to compositor-evaluated gravity and damped wall bounces. Passive walking and landing deformation are submitted as complete animations. A pointer timer updates hover, bubble placement, and transparent-background input routing. Taskbar placement accounts for auto-hide and updates on display/settings/DPI changes. Presentation configuration includes 2x DIP scale and exact-white sprite transparency.

`--windowed` provides a framed drag trial that closes after three minutes. `--replay` runs the original framed 2.5-second repeating path and closes after 60 seconds. Escape also closes either framed mode. These modes are non-topmost. Only one Composition host can run per session.

`--trace-drag` records up to 4096 events per gesture, including pointer coordinates, model poses, target changes, animation submissions, flight poses, and capture/hover transitions. On landing or airborne re-grab it writes a timestamped JSON report under `%TEMP%\CopilotBuddy\drag-traces`. No screenshots are taken. The trace describes application activity, not displayed frame timing; motion behavior is unchanged and tracing is off by default.

Sprite pixels always use hard edges without antialiasing.

Drag targets are coalesced by a 16 ms input timer. The compositor predicts at most 30 ms from the latest pointer velocity, then holds until another sample; corrections converge without interrupting the expression. On release, the current predicted pose and velocity seed flight without waiting for a UI callback. Release velocity is bounded by the configured release speed. Gravity, wall restitution, and the exact floor contact time are evaluated on the compositor. At impact the core receives position and impact speed for landing squash. Tracker reports update hit-testing and permit airborne re-grabs; they do not drive displayed dragging or flight. Tracker callbacks are not measurements of displayed frames.

Normal launches use uninterrupted predictive direct-follow dragging. `--spring-drag` enables the older retargeted spring for diagnostic comparisons. Both modes use the same tracker and release handoff.

`--smoke` runs the framed replay for 5.5 seconds and saves two client-area desktop captures in the build output directory. `--interaction-smoke` instead scripts mid-flight target changes, a stationary hold that must settle without retargeting, release at rest, airborne re-grab, and capture loss while moving, then writes an interaction report. It requires both flights to be reported by the compositor and the buddy to land. Add `--spring-drag` to test the comparison mode or `--delayed-drag-reports` to batch pose-report delivery at the 150 ms test cadence. MovingRetargets counts target changes during observed motion, not a measurement of velocity continuity. Captures may include the desktop behind the transparent window; they establish rendering and movement, not smooth frame pacing. Successful startup writes `replay-started.txt`; startup failures write `startup-error.txt`.

## Projects

- `CopilotBuddy.Composition`: Windows Composition taskbar overlay, assets/configuration, speech bubbles, named-pipe host, and comparison modes
- `CopilotBuddy.Copilot`: Copilot SDK adapter, long-lived in-memory broker host, CLI process ownership, event mapping, and session-window focusing
- `CopilotBuddy.Core`: provider-neutral session interface, broker client/protocol, attention queue, presentation state, wandering, animation timing, configuration, and protocol contracts
- `CopilotBuddy.Cli`: presentation-only named-pipe client
- `CopilotBuddy.Core.Tests`: deterministic controller and protocol tests