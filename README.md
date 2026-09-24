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
| `windows_snapshot` | Get accessibility tree with element refs |
| `windows_click` | Click an element by ref |
| `windows_type` | Type text into an element |
| `windows_send_keys` | Send key presses or key chords (for example `Ctrl+A`) |
| `windows_fill` | Clear and fill a text field |
| `windows_get_text` | Get text content of an element |
| `windows_screenshot` | Capture window/element as PNG |
| `windows_list_windows` | List all open windows |
| `windows_focus` | Bring a window to foreground |
| `windows_close` | Close a window |
| `windows_batch` | Execute multiple actions in one call |
| `windows_dialogs` | List open dialogs and pending clicks (Win32, works while UIA is blocked) |
| `windows_dialog` | Read/press/type into/close a Win32 dialog without UI Automation |
| `windows_wait` | Wait for a pending click to finish, or a dialog to open or close |

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
│  - Builds agent-friendly accessibility snapshots                │
│  - Maps element refs ↔ AutomationElements                       │
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
