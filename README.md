# FlaUI-MCP

An MCP (Model Context Protocol) server that enables AI agents to automate Windows desktop applications using accessibility APIs - the same way Playwright automates browsers.

[![Build](https://github.com/shanselman/FlaUI-MCP/actions/workflows/build.yml/badge.svg)](https://github.com/shanselman/FlaUI-MCP/actions/workflows/build.yml)
[![GitHub release](https://img.shields.io/github/v/release/shanselman/FlaUI-MCP)](https://github.com/shanselman/FlaUI-MCP/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

## Why This Exists

When Playwright's MCP server automates browsers, it provides:
- `browser_snapshot` → Structured accessibility tree with element refs
- `browser_click ref="..."` → Click by ref, not coordinates

**FlaUI-MCP brings the same pattern to Windows desktop apps:**
- `windows_snapshot` → Accessibility tree with refs like `w1e5`
- `windows_click ref="w1e5"` → Click element by ref

No screenshot parsing. No coordinate guessing. Just semantic element references.

## Quick Demo

```
Agent: Calculate 3 × 3

1. windows_launch { "app": "calc.exe" }
   → Window handle: w1

2. windows_snapshot { "handle": "w1" }
   → - window "Calculator" [ref=w1]
       - button "Three" [ref=w1e43]
       - button "Multiply by" [ref=w1e35]
       - button "Equals" [ref=w1e38]
       - text "Display is 0" [ref=w1e15]

3. windows_batch { "actions": [
     {"action": "click", "ref": "w1e43"},
     {"action": "click", "ref": "w1e35"},
     {"action": "click", "ref": "w1e43"},
     {"action": "click", "ref": "w1e38"},
     {"action": "snapshot", "handle": "w1"}
   ]}
   → 1. click: Invoked Three
     2. click: Invoked Multiply by
     3. click: Invoked Three
     4. click: Invoked Equals
     5. snapshot: ... "Display is 9" ...
```

Refs aren't required: `windows_batch` can find elements itself, so a flow can run without
a snapshot first, and action results end with a snapshot of what the app shows next:

```
windows_batch { "actions": [
  {"action": "click", "name": "Three", "role": "button", "handle": "w1", "noDialog": true},
  {"action": "click", "name": "Multiply by", "handle": "w1", "noDialog": true},
  {"action": "click", "name": "Three", "role": "button", "handle": "w1", "noDialog": true},
  {"action": "click", "name": "Equals", "handle": "w1", "noDialog": true},
  {"action": "wait", "until": "text_contains", "nameContains": "Display is", "handle": "w1", "text": "9"}
]}
→ 1. click: button "Three" in w1 -> w1e43: Invoked Three
  ...
  5. wait: name*="Display is" in w1 (w1e15) text contains "9"

  --- after batch: foreground window w1 "Calculator" (changes: 0 added, 0 removed, 1 changed) ---
  changed:
    - text "Display is 9" [ref=w1e15]  (was: text "Display is 3" [ref=w1e15])
```

## Installation

### Prerequisites
- Windows 10/11
- .NET 8.0 Runtime

### Download Release

Download the latest release from [Releases](https://github.com/shanselman/FlaUI-MCP/releases) and extract to a folder.

Choose the ZIP that matches your machine:

| Asset | Use when |
|-------|----------|
| `FlaUI-MCP-win-x64-*-self-contained.zip` | 64-bit Windows, no .NET runtime required |
| `FlaUI-MCP-win-x64-*.zip` | 64-bit Windows with .NET 8 Runtime already installed |
| `FlaUI-MCP-win-arm64-*-self-contained.zip` | Windows on ARM64, no .NET runtime required |
| `FlaUI-MCP-win-arm64-*.zip` | Windows on ARM64 with .NET 8 Runtime already installed |

### Configure MCP Client

Add to your MCP configuration (e.g., `~/.copilot/mcp-config.json`):

```json
{
  "mcpServers": {
    "windows": {
      "type": "local",
      "command": "C:\\path\\to\\FlaUI-MCP.exe",
      "tools": ["*"]
    }
  }
}
```

Or using `dotnet run`:

```json
{
  "mcpServers": {
    "windows": {
      "type": "local",
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\src\\FlaUI.Mcp"]
    }
  }
}
```

## Available Tools

| Tool | Description |
|------|-------------|
| `windows_launch` | Launch a Windows application |
| `windows_snapshot` | Get accessibility tree with element refs (refs stay the same across snapshots for unchanged elements); `ref` for one part, `depth`, `compact` |
| `windows_click` | Click an element by ref; never hangs on a modal dialog; result includes a snapshot of what opened |
| `windows_type` | Type text into an element |
| `windows_send_keys` | Send key presses or key chords (for example `Ctrl+A`) |
| `windows_fill` | Clear and fill a text field; result includes a snapshot of the app |
| `windows_get_text` | Get text content of an element |
| `windows_screenshot` | Capture window/element as PNG |
| `windows_list_windows` | List all open windows |
| `windows_focus` | Bring a window to foreground |
| `windows_close` | Close a window |
| `windows_batch` | Execute multiple actions in one call: refs or selectors, condition waits, dialog actions |
| `windows_dialogs` | List open dialogs and pending clicks (Win32, works while UIA is blocked) |
| `windows_dialog` | Read/press/type into/close a Win32 dialog without UI Automation; press results include a snapshot of what the app shows next |
| `windows_wait` | Wait for a pending click to finish, or a dialog to open or close |
| `windows_find` | Find elements by name / automation id / role and get their refs, without reading a whole snapshot |
| `windows_set` | Make an element checked / expanded / selected, pick an option, or set a value; does nothing if it already is |
| `windows_menu` | Choose a menu command by path (`File > Save As...`), including context menus |
| `windows_read_table` | Read a grid, list view or table as tab-separated rows with a ref per row |
| `windows_run_flow` | Run a saved flow (a batch saved with `saveAs`), or list the saved ones |

`windows_screenshot` supports an optional `background: true` argument when a
window `handle` is provided. This uses native background capture when available
and falls back to the normal screenshot path if Windows returns a blank frame.
It can also save screenshots with `savePath`, which must be an absolute local
`.png` path. Existing files are not replaced unless `overwrite: true` is set.

Tool calls have a 30-second timeout so a blocked UI Automation provider or modal
dialog returns an actionable error instead of hanging the MCP server forever.

### Tool Examples

Send a keyboard chord to a target element:

```json
{
  "ref": "w1e5",
  "chord": "Ctrl+A"
}
```

Send a sequence of key presses or chords:

```json
{
  "keys": ["Ctrl+A", "Delete", "Enter"]
}
```

Capture a window using opt-in background capture:

```json
{
  "handle": "w1",
  "background": true
}
```

Save a screenshot and get back only its path (for clients that truncate inline images), or the
path plus a small JPEG preview:

```json
{ "handle": "w1", "output": "file" }
{ "handle": "w1", "output": "preview" }
```

Save a screenshot to disk without replacing existing files:

```json
{
  "handle": "w1",
  "savePath": "C:\\Temp\\capture.png"
}
```

Replace an existing screenshot file explicitly:

```json
{
  "handle": "w1",
  "savePath": "C:\\Temp\\capture.png",
  "overwrite": true
}
```

Run a whole dialog flow in one `windows_batch` call. Each action targets a `ref` or a
selector: `name`, `nameContains`, `automationId`, `role` (as snapshots print it), plus
`handle` for the window to search and `index` when several elements match:

```json
{
  "actions": [
    { "action": "click", "name": "Delete", "role": "button", "handle": "w1" },
    { "action": "wait", "until": "dialog_open" },
    { "action": "dialog_press", "button": "yes" },
    { "action": "wait", "until": "text_contains", "automationId": "StatusLabel", "handle": "w1", "text": "Deleted" }
  ]
}
```

| Batch action | Fields |
|--------------|--------|
| `click` | `ref` or selector; `mode` (`auto`/`invoke`/`input`); `noDialog: true` or `settleMs` to skip/shorten the 250 ms dialog watch |
| `type` | `text`; optional `ref` or selector to focus first |
| `fill` | `ref` or selector; `value` |
| `keys` | `keys` (array of chords like `Ctrl+Shift+B`, `Down`, `Enter`) or `chord`; optional `ref` or selector to focus first. A dialog it opens stops the batch like a click's |
| `check` / `uncheck` | `ref` or selector; no-op if already so |
| `expand` / `collapse` | `ref` or selector; no-op if already so |
| `select` | `ref` or selector; `option` to pick an item inside it, or none to select the element itself |
| `set_value` | `ref` or selector; `value` (number for sliders/spinners, text for text boxes) |
| `menu` | `path` (`["File", "Save As..."]` or `"File > Save As..."`); optional `handle`, or a `ref` / selector for a context menu; `mode: "input"` to click items with the mouse |
| `wait` | `ms`, or `until` + `timeoutMs` (default 5000): `dialog_open`, `dialog_closed` (`handle`, default the last dialog), `element` / `element_gone` (selector), `text_contains` (selector + `text`, case-insensitive), `idle` (`handle`, `stableMs` default 500) |
| `snapshot` | optional `handle`; `compact` |
| `dialog_press` | `button` (text, `c3` ref, or `ok`/`cancel`/`yes`/`no`/...); optional `handle` (default the last dialog) |
| `dialog_set_text` | `text`; optional `control` (`c4`, default the first edit box) and `handle` |

`handle: "$dialog"` in a selector means the dialog the batch last saw open. A click that opens
a dialog stops the batch unless the next action is `wait` with `until: "dialog_open"`. A batch
has a 24-second budget; if it runs out, the result says which action to continue from.

### Configuration

These are for whoever runs the server. What the model can change (post-action output,
compact snapshots, screenshot output) is a tool argument, never an environment variable.

| Environment variable | Effect |
|----------------------|--------|
| `FLAUI_MCP_SNAPSHOT_MODE` | `cached` (default), `subtree`, or `live` (the original per-property reads) |
| `FLAUI_MCP_TIMING` | `0` to turn off the `[timing]` lines on stderr |
| `FLAUI_MCP_INSTRUCTIONS` | `off` to send no server instructions, or a path to a text file to send instead of the built-in ones |
| `FLAUI_MCP_KEYBOARD_GUARD` | `0` to let keyboard input go to whatever window is in the foreground |
| `FLAUI_MCP_UIA_TRANSACTION_TIMEOUT_MS` | Make UIA calls against a blocked provider fail after this long |

### Safety and Limitations

- Keyboard input is focus-dependent. When you use `windows_send_keys`, the tool
  focuses the supplied `ref` first when possible, but Windows still sends keys to
  the active keyboard focus.
- `windows_screenshot` `background: true` is only valid with a window `handle`.
  If native background capture returns a blank frame, FlaUI-MCP falls back to the
  normal capture path.
- `savePath` accepts absolute local `.png` paths only. UNC paths, device paths,
  non-PNG extensions, and existing files without `overwrite: true` are rejected.
- Desktop integration tests require an interactive Windows session because they
  launch real WinForms and WPF windows.
- A timeout error means the MCP request returned, but a blocked Windows UI
  Automation provider or modal dialog may still need to be dismissed before
  retrying the operation.

## Modal Dialogs

UI Automation pattern calls are synchronous. When a click handler calls `ShowDialog()` or
`MessageBox.Show()`, providers that run the handler inline (WinForms, Win32) don't return
from `Invoke()` until the dialog closes. A naive client hangs.

FlaUI-MCP handles this:

- **Clicks never hang.** `windows_click` runs the UIA call on its own thread while a Win32
  monitor (`EnumWindows`, filtered to the target process) watches for new dialogs. When one
  appears, the tool returns immediately with the dialog's handle, and the click becomes a
  pending operation that finishes when the dialog closes.
- **Every result carries status.** Any tool's result ends with the open dialogs, pending
  clicks, and clicks that finished since the last call, so the agent can't miss a dialog.
- **Dialogs are ordinary windows.** Snapshot one with `windows_snapshot handle=w7` and act on
  its refs like any other window.
- **`mode=input` avoids the problem.** A real mouse click leaves no UIA call outstanding, so
  the dialog's UIA tree is fully usable. It needs the window visible and an unlocked session.
  The click is refused if another app covers the target point.
- **A Win32 fallback for when UIA is blocked.** `windows_dialog` reads a classic dialog's
  controls and presses buttons with window messages (`WM_COMMAND`/`BM_CLICK`,
  `WM_SETTEXT`), so it works even while the app's provider is stuck. It handles MessageBox,
  `#32770` dialogs and WinForms forms. WPF and task-dialog content has no child HWNDs, so
  use `windows_snapshot` for those.

```
1. windows_click { "ref": "w1e12" }
   → The click "Delete" (pattern) opened a dialog. The app is waiting for it, so the
     click is pending as op1.
     Dialog: w4 "Confirm Delete" [modal] [win32]
2. windows_dialog { "handle": "w4" }
   → - c1 text "Delete the selected item?"
     - c2 button "&Yes" [default] id=6
     - c3 button "&No" id=7
3. windows_dialog { "handle": "w4", "action": "press", "button": "yes" }
   → Pressed c2 "&Yes" in w4. The dialog closed.
     --- status ---
     Finished since last call:
     - op1: click "Delete" (pattern) -> Invoked Delete
```

Set `FLAUI_MCP_UIA_TRANSACTION_TIMEOUT_MS` (for example `5000`) to make UIA calls against a
blocked provider fail after that long instead of the UIA default.

## Fewer Round Trips

Driving an app one tool call at a time is slow: each step costs a model turn. FlaUI-MCP cuts
the number of calls:

- **Action results show what happened.** `windows_click`, `windows_fill`, `windows_dialog`
  `action=press` and `windows_batch` end with the state of the window you'll most likely act
  on next: the dialog the action opened, else the app's foreground window. If you've seen that
  window before, you get only what changed, with refs:

  ```
  --- after click: foreground window w1 "Editor" (changes: 1 added, 1 removed, 1 changed) ---
  added:
    in group "Find" [ref=w1e20]:
      - text "3 matches" [ref=w1e412]
  removed:
    - text "Searching..." [ref=w1e409]
  changed:
    - button "Next" [ref=w1e22]  (was: button "Next" [ref=w1e22] [disabled])
  ```

  The first time a window is seen there's nothing to compare with, so you get a one-line summary
  (`first look, 812 elements; use windows_find ...`) and the window is recorded; the next action
  there reports changes. A dialog the action opened gets a small snapshot (60 elements), since
  it's usually what you act on next. Refs from earlier snapshots stay valid either way.
  `postSnapshot: "full"` shows a capped snapshot instead of the summary; `postSnapshot: false`
  turns it off.
  While a pending click blocks the app's UI Automation, you get the dialog's Win32 controls.
- **Search instead of reading.** On big windows (IDEs, Office) a full snapshot is thousands of
  lines. `windows_find` returns just the matches, each with the named elements it's inside:

  ```
  windows_find { "nameContains": "Build", "role": "menuitem" }
  → in w1:
    - menuitem "Build Solution" [ref=w1e88]  (in menubar "MenuBar" > menuitem "Build")
  ```

  `windows_snapshot` takes `ref` to read one part of a window (refs elsewhere stay valid),
  `depth` to stop early, and `compact: true` to leave out offscreen elements and unnamed
  groups that only wrap one element.
- **`windows_batch` finds elements itself.** Actions can target `name`, `nameContains`,
  `automationId` and `role` (as printed in snapshots) instead of a ref, optionally limited to a
  window with `handle` (`"$dialog"` is the dialog the batch last saw open). Waits can be for a
  condition: `until` = `dialog_open`, `dialog_closed`, `element`, `element_gone`,
  `text_contains`. `dialog_press` and `dialog_set_text` drive a dialog with Win32 messages.
  `keys` sends chords (`["Ctrl+Shift+B"]`) to the focused element or a ref/selector.
- **Expected dialogs don't stop a batch.** A click that opens a dialog still stops the batch,
  unless the next action is `wait` with `until: "dialog_open"`.
- **No settle wait when there's nothing to wait for.** Clicks watch for a dialog for 250 ms
  after returning. `noDialog: true` (or `settleMs`) on a batch click, or `settleMs` on
  `windows_click`, skips or shortens that; a click followed by `until: "dialog_open"` skips it.

```json
{ "actions": [
  { "action": "click", "role": "tab", "name": "Dialogs", "handle": "w1", "noDialog": true },
  { "action": "click", "name": "Delete", "role": "button", "handle": "w1" },
  { "action": "wait", "until": "dialog_open" },
  { "action": "dialog_press", "button": "yes" },
  { "action": "wait", "until": "text_contains", "automationId": "StatusLabel", "handle": "w1", "text": "Deleted" }
] }
```

**Say what you want, not how to click it.**

- **Names are forgiving.** Selectors, menu paths and options match loosely: `"Save As"`
  finds `"Save &As..."`, `"ok"` finds `"&OK"`, shortcut text is ignored. When nothing matches,
  the error lists the closest names with refs, so a wrong guess costs no extra call:
  `No element matches "Setings". Closest: button "Settings" [ref=w1e14].`
- **`windows_set` puts an element into a state** and does nothing if it's already there, so
  repeating it is safe (clicking a checkbox twice unchecks it):

  ```json
  { "name": "Word wrap", "checked": true }
  { "name": "Status", "role": "combobox", "option": "Pending" }
  { "name": "Volume", "value": 75 }
  { "name": "Fruits", "role": "treeitem", "expanded": false }
  ```

  `option` opens a combo box if its items only exist while open, and asks virtualized lists
  for items that aren't loaded. A missing option lists the ones that exist.
- **`windows_menu` walks a menu path** in one call, including sub-menus and context menus
  (pass a ref or selector to right-click that element first). If the command opens a dialog,
  it returns with the dialog's handle like a click does:

  ```json
  { "path": "File > Recent > Report.txt" }
  { "nameContains": "Report", "role": "listitem", "path": ["Delete"] }
  ```

- **`windows_read_table` reads grids as data:** headers, then one tab-separated line per row
  starting with the row's ref. Page with `start` / `maxRows`.

  ```
  grid "Test Data" [ref=w1e30]: rows 1-3 of 50
  ref	Select	ID	Name	Category
  w1e31	False	ITEM-001	Test Item 1	Alpha
  ```

- **Keys only go to the app.** `windows_type`, `windows_send_keys`, keyboard fills and batch
  `type` / `keys` check that the app being automated is in the foreground (bringing the target
  element's window forward if needed) and refuse otherwise, so a stray focus change can't send
  keystrokes into another program.

All of these are also `windows_batch` actions: `check`, `uncheck`, `expand`, `collapse`,
`select` (`option`), `set_value` (`value`) and `menu` (`path`). Like `click`, a dialog they open
stops the batch unless the next action is `wait until=dialog_open`.

**Waiting, prompts and repeats.**

- **Waits react to the app.** `until` waits subscribe to UI Automation events (windows opening
  and closing, changes inside the window), so they finish the moment the condition holds
  instead of on the next poll; polling every 250 ms remains as a fallback.
- **`until: "idle"`** (batch `wait`, and `windows_wait`) waits until the app answers messages
  promptly, no progress bar is part-way, and the UI hasn't changed for `stableMs` (500 ms by
  default). Use it after "Build", "Open", "Search" when there's no particular text to wait for.
- **`onDialog`** gives a batch standing answers for prompts that may appear:
  `"onDialog": [{"titleContains": "Save changes", "press": "no"}]` (`title`, `titleContains`
  and/or `textContains`, plus `press`). A matching dialog is answered and the batch carries on;
  any other dialog still stops it.
- **Long lists and trees** only create the items near the viewport. When a name isn't found,
  selectors and `windows_find` ask the list for the item and load it (UI Automation's
  ItemContainer / VirtualizedItem), so items far down a list can be found and acted on.
- **`windows_screenshot annotate=true`** draws the refs of buttons, fields and items onto the
  image and lists them, for apps whose accessibility tree is too thin to work from text alone.
- **Saved flows.** Add `saveAs` to a batch that worked and it's kept as a flow in
  `%APPDATA%\flaui-mcp\flows\<name>.json`: refs become selectors, window handles are dropped,
  and values listed in `params` become arguments. `windows_run_flow` runs one (`args` fills the
  parameters) or lists them; the files are plain JSON you can read, edit and keep in git.

  ```json
  { "saveAs": "open-recent", "description": "Open a recent file", "params": { "file": "Report.txt" },
    "actions": [ { "action": "menu", "path": "File > Recent > Report.txt" } ] }

  { "name": "open-recent", "args": { "file": "Notes.md" } }
  ```

**Faster snapshots.** Snapshots read each element's properties (name, automation id, control
type, enabled/offscreen, pattern availability and toggle/selection/expand/read-only state)
through a UI Automation `CacheRequest`, so each element's children arrive with all their
properties in one cross-process call instead of about ten. `FLAUI_MCP_SNAPSHOT_MODE` picks
`cached` (default), `subtree` (the whole tree in one call; can't stop early at the element
limit) or `live` (the original per-property reads).

**Server instructions.** At startup the server gives the client a short guide (about 270
tokens, in `src/FlaUI.Mcp/Mcp/ServerInstructions.cs`) to how the tools fit together: find
instead of snapshot, `windows_set` / `windows_menu` / `windows_read_table` instead of clicking
around, one `windows_batch` for several steps, and what to do about dialogs. Clients such as
Claude Code add it to the model's system prompt. Set `FLAUI_MCP_INSTRUCTIONS` to `off`, or to a
file with your own text.

**Measuring.** Each tool call writes a line to stderr (never stdout, the MCP channel):

```
[timing] snapshot 412ms handle=w1 mode=Cached nodes=812
[timing] tool=windows_snapshot exec=415ms status=2ms gap=6120ms
```

`exec` is the tool's own time, `status` is the status-block scan, and `gap` is the time since
the previous result was returned: the model's turn. A per-tool summary is written every 20
calls and at exit. `FLAUI_MCP_TIMING=0` turns it off. To compare snapshot modes on a real
window:

```powershell
FlaUI.Mcp.exe --bench-snapshot "Visual Studio" 5
```

## How It Works

### The Accessibility Snapshot

When you call `windows_snapshot`, you get a structured text tree:

```
- window "Calculator" [ref=w1e1]
  - group "Number pad" [ref=w1e39]
    - button "Seven" [ref=w1e47]
    - button "Eight" [ref=w1e48]
    - button "Nine" [ref=w1e49]
  - text "Display is 0" [ref=w1e15]
```

This comes from **Windows UI Automation** - the same API screen readers use. Each element has:
- **Role** (button, text, group, textbox)
- **Name** ("Seven", "Display is 0")
- **Ref** (w1e47) - a handle for interaction
- **State** ([disabled], [readonly], [checked])

### Why Not Screenshots?

| Approach | Pros | Cons |
|----------|------|------|
| **Accessibility Tree** | Semantic, precise, fast, works at any resolution | Requires UI Automation support |
| **Screenshot + Vision** | Works with any app | Slow, expensive, imprecise, resolution-dependent |

FlaUI-MCP uses accessibility because it's what screen readers use - it's designed for programmatic UI interaction.

## Building from Source

```powershell
# Clone
git clone https://github.com/shanselman/FlaUI-MCP.git
cd FlaUI-MCP

# Build
dotnet build src/FlaUI.Mcp

# Run
dotnet run --project src/FlaUI.Mcp
```

### Testing

```powershell
# Unit tests
dotnet test tests\FlaUI.Mcp.Tests

# Desktop integration tests; requires an interactive Windows session
dotnet test tests\FlaUI.Mcp.IntegrationTests
```

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  AI Agent (GitHub Copilot, Claude, etc.)                        │
│  - Calls MCP tools: windows_snapshot, windows_click, etc.       │
└─────────────────────────────────────────────────────────────────┘
                              │ MCP Protocol (JSON-RPC over stdio)
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  FlaUI-MCP Server (.NET 8)                                      │
│  - Implements MCP tool handlers                                 │
│  - Builds agent-friendly accessibility snapshots (CacheRequest) │
│  - Maps element refs ↔ AutomationElements (stable across calls) │
│  - Finds elements by selector; watches for dialogs with Win32   │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  FlaUI Library (github.com/FlaUI/FlaUI)                         │
│  - UIA3Automation for modern apps (WPF, UWP, Win32)            │
│  - Control patterns: Invoke, Value, Toggle, Selection           │
│  - Tree walking and element discovery                           │
└─────────────────────────────────────────────────────────────────┘
```

## Supported Applications

Works with any Windows application that supports UI Automation:
- ✅ Win32 apps (Notepad, Explorer, etc.)
- ✅ WPF applications
- ✅ WinForms applications  
- ✅ UWP/Store apps (Calculator, Settings, etc.)
- ⚠️ Electron apps (partial - depends on accessibility implementation)
- ❌ Games (typically no UI Automation support)

## Contributing

Contributions welcome! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## License

MIT License - see [LICENSE](LICENSE) for details.

## Acknowledgments

- [FlaUI](https://github.com/FlaUI/FlaUI) - The excellent .NET UI Automation library this project is built on
- [Playwright](https://playwright.dev/) - Inspiration for the snapshot/ref interaction model
- [Model Context Protocol](https://modelcontextprotocol.io/) - The protocol that makes this possible
