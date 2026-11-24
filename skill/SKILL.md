---
name: phoneuse
description: Use the Phone Use MCP tools (or the phoneuse CLI) to view and operate the owner's iPhone from this computer, and ask Apple Intelligence on it. Explains every tool and when to use it.
---

# Phone Use

Phone Use shows the owner's iPhone on this PC (AirPlay Screen Mirroring) and
sends touches and keys to it as a Bluetooth keyboard and pointer. Nothing runs
on the phone for control. Every MCP tool is also a `phoneuse` CLI command
(`phoneuse commands` lists them). Run the CLI from a shell when MCP isn't
connected.

## Start, stop and recover

Call `status` and `show_mirror` before operating the phone. Follow the
recovery steps in `status` if the connection drops. Call `end_session` when
you are finished.

- **Phone is locked.** Use `request_unlock`. For a manual unlock, ask the
  owner, then call `wait_for_unlock` once and make no other tool calls until it
  returns. Resume only on `unlocked`. On a timeout, wait for the owner before
  retrying.
- **The owner's part.** The owner handles approvals, passcodes and takeover.
  Never ask for or enter a passcode. Never tap Allow on a permission prompt.
- **MCP server unavailable on Windows.** Open **Phone Use** from Start, or run
  `windows/dist/PhoneUseMirror.exe` from a checkout. Opening the app starts
  the server. Closing the window keeps it running in the tray, and **Quit** in
  the tray menu stops it. After reopening Phone Use, reconnect the MCP client
  or start a new chat if the tools are still unavailable.

## The tools

Coordinates are fractions of the screen from 0 to 1, from the top-left
corner. Take positions from a recent `look` or `inspect_region`. If the screen
moved since then, look again.

**Session and connection**

| Tool | CLI | Use it to |
|---|---|---|
| `status` | `status` | Check the mirror, Bluetooth and owner takeover. The first call, and again after anything goes wrong. |
| `show_mirror` | `view` | Show the owner the mirror before you act. |
| `reconnect_bluetooth` | `reconnect` | Reconnect a paired phone after `status` shows Bluetooth down. Try it once. |
| `request_unlock` | `unlock` | Ask the owner to approve a saved-passcode unlock. |
| `wait_for_unlock` | `wait-for-unlock` | Wait, without sending input, while the owner unlocks by hand. |
| `end_session` | `done` | Give control back to the owner. Always call it at the end. |

**Seeing the screen**

| Tool | CLI | Use it to |
|---|---|---|
| `look` | `text` | Read the visible text with positions (OCR). Cheap, so use it first. |
| `inspect_region` | `inspect-region` | Get a crop image plus OCR for icons, images, or text that `look` misreads. |
| `wait_for` | `wait-for` | Wait for a label to appear, without touching anything. |
| `find_in_scroll` | `find-in-scroll` | Scroll a list until a label shows up, then return its position. |

**Acting**

| Tool | CLI | Use it to |
|---|---|---|
| `open_app` | `open` | Open an app by its name (through search). |
| `tap_on` | `tap-on` | Tap a unique label. Use `near` or `region` to narrow it, and `expect` to check that the next screen came. This is the preferred tap. |
| `tap` | `tap` | Tap a position, when the target has no readable label. |
| `long_press` | `longpress` | Press and hold, for context menus. |
| `swipe` | `swipe` | Drag, for carousels, sliders and dismissing sheets. |
| `scroll_by` | `scroll-by` | Scroll by part of a screen. A positive value shows what is below. |
| `type_text` | `type` | Replace the focused field's text. |
| `press_key` | `key` | Press a key or a key combination: return, esc, tab, cmd+a, cmd+space. |
| `toggle_keyboard` | `keyboard` | Show or hide the on-screen keyboard. |
| `back` | `back` | Go back in the current app. |
| `navigate` | `recipe` | Use a system gesture: home, switcher, control-center, notifications or dismiss. |
| `run_steps` | (MCP only) | Batch up to four steps in 60 seconds. Stops on failure or uncertainty. |

If a result says input "was sent" or "may have been sent", look at the screen
before you retry, so nothing happens twice. Avoid tapping the Dynamic Island,
because that can stop Screen Mirroring.

## Batch routine actions

Prefer `run_steps` when you can supply the steps and their arguments up front.
The app can be unopened and the screen can be unseen. `tap_on` looks for a
unique label on the live screen, `expect` verifies the transition, and
`wait_for` checks that a label appears. Put `look` or `inspect_region` last.

- Open an app and read it with `open_app` → `look`.
- Browse a feed with `scroll_by` → `look`.
- Open Settings, tap **General** with `expect: "About"`, then read the screen.
- Type and submit a search once you know the search field has focus.

Use a separate call when you need to choose the next action from the screen
or get owner approval. If a batch stops after sending input, inspect the
current screen before resuming from the unfinished step.

## Asking Apple Intelligence: `siriai`

`siriai` types a prompt on the phone and returns the visible reply. You must
choose a model. Without one, the tool refuses and lists the choices.

| `model` (MCP) / flag (CLI) | What answers | Sees | Use for |
|---|---|---|---|
| `siri-plus-context` / `--siri-plus-context` | Siri, typed to in the Siri app | The owner's personal context (messages, mail, calendar, what's on screen), plus app intents, so it can act in apps | Questions about the owner's own data, or getting something done on the phone |
| `on-device` / `--on-device` | The on-device model, through the **PhoneUse On-Device** shortcut | Only the prompt | Quick, private text work: rewrite, summarize, classify |
| `cloud` / `--cloud` | Private Cloud Compute, through **PhoneUse Cloud** | Only the prompt, and it may also look things up on the web | Harder questions that should stay private |
| `cloud-pro` / `--cloud-pro` | Apple's largest cloud model, through **PhoneUse Cloud Pro** | Only the prompt | Hard reasoning. It is slower and has tight daily limits. |

- Shortcut models can't read the owner's data or run app actions. If you need
  either, use `siri-plus-context`, and put any text the model needs
  inside the prompt.
- Siri can send, buy and change things. Get the owner's OK before any prompt
  that would do so.
- Prompts are one line, at most a few sentences, using US-keyboard characters
  only.
- **If a model's shortcut is missing,** `siriai` says so and gives the exact
  steps to build it. Ask the owner whether they want to make it or want you to
  make it with the tools above. Then follow the steps exactly: the shortcut
  name, the input prompt `phoneuse model prompt` and the alert title
  `phoneuse model answer` must match character for character.

## More

`RECIPES.md` covers gestures and saved recipes in depth (CLI `gesture`,
`recipe save`, `--exact` drags).
