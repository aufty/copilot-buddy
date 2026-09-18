# Copilot Buddy: Visual Handoff

## Scope

Build the first visual prototype in C# as a transparent, borderless Windows overlay. The buddy treats the top edge of the Windows taskbar as its platform. This document covers presentation only; Copilot attachment and event acquisition are outside scope.

## Assets

Use the supplied PNG sprite sheet with five poses:

- Standing: 1 frame
- Walking: 2 frames
- Waving: 2 frames

Load frame rectangles from presentation configuration so the sheet can be replaced or resized without changing behavior. Render with nearest-neighbor scaling and one configurable integer scale factor.

## Presentation Model

Expose a small event-agnostic presentation API:

- `ShowMessage(text)`
- `DismissMessage()`
- `SetVisible(bool)`

`ShowMessage` interrupts wandering, displays the bubble, and runs the attention animation. Callers do not expose Copilot-specific event types to the visual layer.

Visual states:

- **Idle:** standing frame; short randomized pause.
- **Walking:** alternate walking frames while moving horizontally.
- **Attention:** stop, alternate waving frames, and hop vertically while the message is visible.
- **Dragging:** pause wandering and follow the captured pointer through a damped spring so the buddy trails slightly behind it.
- **Airborne:** preserve release velocity, apply gravity, and keep the buddy within horizontal screen bounds.
- **Landing:** anchor the feet to the taskbar and briefly squash and rebound in proportion to impact speed.

Clicks:

- Left click raises an action event; behavior is intentionally undecided.
- Hovering pauses wandering in place and shows the standing pose; leaving resumes with a fresh randomized idle interval.
- Click and hold captures the pointer for dragging; crossing a small movement threshold suppresses the left-click action and releasing flings the buddy.
- Right click opens a small native-style context menu populated by the host; options are intentionally undecided.

## Wandering

Use a bounded random waypoint behavior:

1. Choose a random reachable horizontal destination on the current taskbar.
2. Walk toward it at a constant configurable speed.
3. Idle for a random interval, then choose another destination.
4. Avoid choosing very short moves repeatedly and never cross the taskbar bounds.

Pause the wander controller during attention. Resume with a new idle interval after the message is dismissed. Prefer a seeded random source in tests.

## Layout And Animation

- Anchor the buddy's feet to the taskbar's top edge and dynamically mirror taskbar visibility: attach/show/topmost above normal windows; hide, detach, and drop topmost while a fullscreen foreground window covers the primary monitor.
- Recalculate placement when taskbar bounds, display scale, or monitor layout changes.
- Keep the overlay background transparent and limit hit testing to visible buddy/menu content.
- Animate sprite frames and movement from elapsed time, not frame counts.
- For attention, combine the wave loop with a small eased vertical hop; preserve the feet baseline after each hop.
- Respect the presentation configuration's reduced-motion flag by stopping hops and breathing/landing deformation and using a slower wave. Direct dragging and fling physics remain enabled. The first prototype does not read the Windows accessibility setting automatically.

The speech bubble appears above and near the buddy without covering it. It grows and shrinks to fit content, using minimum and maximum width, text wrapping, internal padding, and a maximum height with scrolling or truncation for unusually long messages. Clamp the bubble to the monitor work area and move its pointer to remain aimed at the buddy.

## CLI Test Surface

Provide a small companion CLI that sends commands to the running presentation host over a local named pipe:

```text
copilot-buddyctl show "Build needs your approval"
copilot-buddyctl dismiss
copilot-buddyctl visible on
copilot-buddyctl visible off
```

`show` must demonstrate the complete first iteration: wandering pauses, the adaptive speech bubble appears, and the buddy waves and hops. The protocol should express presentation commands only so future Copilot events can map onto the same API.

## First-Iteration Acceptance

- Copilot Buddy wanders naturally along the taskbar and alternates between walking and idle.
- Copilot Buddy stays aligned after taskbar/display changes and cannot leave visible bounds.
- `copilot-buddyctl show <text>` produces a correctly sized, on-screen bubble and attention animation.
- `copilot-buddyctl dismiss` removes the bubble and returns the buddy to wandering.
- Left and right clicks are detected without making the entire overlay intercept clicks.
