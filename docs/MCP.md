# PhoneUse MCP

The MCP server uses the same mirror, OCR service, Bluetooth input, and owner
approval controls as the CLI.

## Setup in Codex

On Windows, open **Phone Use** from Start or launch
`windows\dist\PhoneUseMirror.exe` from a checkout. Connect Codex during the
**Agents** stage of guided setup or from **Settings → Agent → Codex**. Then
open a new Codex chat. The Agent settings tab can connect or disconnect both
Codex and Claude Code.

From a terminal, the same switch is:

```powershell
phoneuse codex connect
```

`phoneuse codex disconnect` undoes it, and `phoneuse codex` alone prints the
state. It registers the app's server with the Codex CLI, links the skill, and
refuses when Codex already has a different server named `phoneuse` or has this
one disabled.

Connecting also links the Phone Use skill (`skill\`), which explains every tool
and the `siriai` models to agents. Claude Code gets it at
`~\.claude\skills\phoneuse`, and Codex gets it at `~\.agents\skills\phoneuse`,
where Codex reads personal skills. Both are directory junctions to the
installed copy, and Phone Use repairs them each time it starts, so an update
or a move doesn't leave them pointing at an old copy. A real folder already at
the Codex path is treated as the user's own skill and left alone.

**After an update.** Each time it starts, Phone Use also removes the Claude
app's private copy of `%LOCALAPPDATA%\phoneuse\run`. The Claude desktop app is
an MSIX package, so a `phoneuse` command run from Claude Code writes that
folder into the package's own storage. From then on it reads its own stale
token and pid files instead of the services' current ones, and commands fail
with "authentication rejected or stale service". The `phoneuse` command in
`~\.local\bin` also chooses `core\.venv` or the bundled Python each time it
runs, so a Python that an update replaces doesn't break it.

WinUI starts a local Streamable HTTP server. You can also register it by hand
with the Codex CLI (this doesn't link the skill until Phone Use next starts).

```powershell
codex mcp add phoneuse --url http://127.0.0.1:8779/mcp
codex mcp get phoneuse
```

Closing the WinUI window hides it to the tray and leaves MCP running. Choose
**Quit** from the Phone Use tray menu to stop the server. MCP startup errors
are logged in `%LOCALAPPDATA%\phoneuse\run\phoneuse-mcp.log`.

For clients that launch their own server, `phoneuse mcp` also supports stdio.
Use this on Linux and macOS with the Phone Use backend running.

## Using the tools

Call `status` and `show_mirror` before operating the phone. On Windows,
`show_mirror` opens or restores the native WinUI mirror when available.
If Bluetooth drops, try `reconnect_bluetooth` once. When Screen Mirroring
stops or the Bluetooth connection stays down, follow the owner recovery steps
from `status` and recheck it before continuing. If the owner takes over, wait
for them to resume the session and tell you to continue.

Use `request_unlock` if the phone is locked. Saved-passcode unlock is currently
disabled. Ask the owner to unlock the phone and show the Home Screen, then call
`wait_for_unlock` once. Stop issuing tool calls until it returns. The MCP server
watches the live mirror without sending input and returns a single status.
Resume only on `unlocked`. On `timeout`, wait for the owner before trying again.
On `paused`, `mirror_stale`, or `mirror_unavailable`, follow the recovery steps
for the session or mirror. The default wait is 30 seconds and the maximum is 45.
Call `end_session` when finished.

`long_press`, `swipe`, `navigate`, `press_key` and `toggle_keyboard` cover
context menus, carousels, system gestures, key shortcuts and the on-screen
keyboard. `long_press` and `swipe` are refused when the screen near the start
point changed since the last look, so call `look` first. `navigate` allows only
the five built-in gestures: `home`, `switcher`, `control-center`,
`notifications` and `dismiss`.

## Discovery

`phoneuse commands` prints the agent command manifest. The MCP server reads the
same registry in `phoneuse/commands.py` for tool names, argument schemas, CLI
flags, and output types. To expose an agent command, add its declaration there.
The adapter handles plain text, structured JSON, and a PNG with JSON metadata.

## Running a sequence

After `status` and `show_mirror`, prefer the MCP-only `run_steps` tool for up
to six steps whose arguments you can supply up front, within 90 seconds. The
screen can be unseen: `tap_on` finds a live label and `expect` verifies the
transition. It accepts `open_app`, `tap_on`, `scroll_by`, `type_text`,
`press_key`, `navigate`, `back`, `find_in_scroll`, `wait_for`, `look`, and
`inspect_region` with their usual arguments. Put `look` or `inspect_region`
last. When a run stops early, its result includes `screen`, the current screen
text as `look` returns it, so the agent can recover without another call. It is
left out when the run ran out of time or fewer than 4 seconds remain. Use separate calls when the
next action depends on inspecting a result or getting owner approval.

```json
{
  "steps": [
    {"name": "open_app", "arguments": {"app": "Settings"}},
    {"name": "tap_on", "arguments": {"label": "General", "expect": "About"}},
    {"name": "look"}
  ]
}
```

With a search field already focused, you can type a query, submit it, and
wait for a known results label before reading the screen.

```json
{
  "steps": [
    {"name": "type_text", "arguments": {"text": "pizza"}},
    {"name": "press_key", "arguments": {"combo": "return"}},
    {"name": "wait_for", "arguments": {"label": "Restaurant"}},
    {"name": "look"}
  ]
}
```

The server validates the whole sequence first and runs it in order with a
shared 60-second deadline. A completed sequence returns brief step statuses
and the final result, including an image when the final step is `inspect_region`.
On failure, `stopped_at` is the one-based step number and `result` explains
why execution stopped. For an input step, `input_sent` is `true`, `false`, or
`null` when delivery is uncertain. Check the screen before retrying a step
whose input was or may have been sent.

## Example

1. Call `open_app` with `{"app":"DoorDash"}`.
2. Call `tap_on` with `{"label":"View cart","expect":"Checkout","timeout":8}`
   when those labels fit the current screen.
3. If `tap_on` returns `input_sent: true` with a timeout or stale status, check
   the screen before taking another action. `input_sent: null` means the link
   failed during the tap and its delivery is uncertain.
4. If the control is an icon, call `inspect_region` with normalized bounds and
   use the crop's full-screen bounds to map the tap position.

## Finding and tapping list items

Call `find_in_scroll` with `{"label":"Fries"}` on an open list. It checks the
current screen, then scrolls down in overlapping steps through the center of
the screen. It returns `found` with the label and its normalized `x` and `y`
positions, or `ambiguous` with candidate positions. `direction` can be `up` or
`down`. `max_scrolls` defaults to 6 and accepts 0 through 12. `timeout`
defaults to 30 seconds. The result reports the number of completed scrolls
and whether scroll input was sent. `input_sent: null` means the last scroll's
delivery is uncertain. `scroll_limit` means the configured number of scrolls
was used up.

Use `tap_on` with `{"label":"Add","near":"Fries"}` for a button on the same
row as a unique label. Supply `region` as `[left, top, right, bottom]` with
normalized coordinates to limit both the target and the anchor to that area.
When a scoped match is ambiguous, the tool stops before tapping. The `expect`
check searches the whole screen after the tap.

The same options are available in the CLI.

```sh
phoneuse find-in-scroll Fries --direction down --max-scrolls 4 --json
phoneuse tap-on Add --near Fries --region 0.05,0.2,0.95,0.85 --json
```
