# Changelog

All notable changes to FlaUI-MCP will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `windows_screenshot` `output`: `file` saves the PNG (to `savePath`, else a temp file) and returns its path and size; `preview` also returns a JPEG of at most 800px. The default stays `image`.
- Forgiving names: selectors (batch, `windows_find`, the new tools), menu paths and options match loosely ("Save As" finds "Save &As...", shortcut text ignored), and a miss lists the closest names with refs.
- `windows_set`: checked / expanded / selected / option / value, acting only if the state differs. Options open combo boxes when needed and realize virtualized list items. Also batch actions `check`, `uncheck`, `expand`, `collapse`, `select`, `set_value`.
- `windows_menu`: invoke a menu command by path, including sub-menus and context menus; returns dialog handles like a click. Also batch action `menu`.
- `windows_read_table`: grids, list views and tables as tab-separated rows with a ref per row, paged with `start` / `maxRows`.
- Keyboard guard: typing and key presses are refused when the automated app isn't in the foreground (after trying to bring the target forward). `FLAUI_MCP_KEYBOARD_GUARD=0` turns it off.
- Server instructions: a short guide to using the tools together, sent in the `initialize` result. `FLAUI_MCP_INSTRUCTIONS=off` or a file path overrides it.
- Test app: WinForms menu bar, status bar and context menu.

### Changed
- Action results default to changes only. The first action in a window never seen before returns a one-line summary instead of a snapshot (the window is recorded, so later actions report changes); a newly opened dialog gets a small snapshot (60 elements). `postSnapshot: "full"` restores snapshots when there's nothing to compare.
- Behaviour the model chooses is set only by tool arguments, not environment variables: removed `FLAUI_MCP_POST_SNAPSHOT`, `FLAUI_MCP_POST_SNAPSHOT_DIFF`, `FLAUI_MCP_POST_SNAPSHOT_MAX_NODES` / `_MAX_CHARS`, `FLAUI_MCP_SNAPSHOT_COMPACT` (use `postSnapshot` and `compact`).
- Tool descriptions rewritten to be about half as long, and to point to the better tool where there is one (e.g. `windows_find` over snapshots on big windows).
- Modal dialog handling. `windows_click` no longer hangs when a click opens a modal dialog: the UIA call runs on its own thread while a Win32 monitor watches the target process for new dialogs. The click returns as soon as a dialog appears, with the dialog's window handle, and becomes a pending operation (`op1`, ...) that finishes when the dialog closes.
- `windows_click` `mode`: `auto` (default), `invoke` (UIA patterns only), `input` (real mouse click at the element's clickable point, bringing its window forward first and refusing if another app covers the point). `mode` is also accepted by `windows_batch` click actions.
- Every tool result ends with a status block listing open modal/Win32 dialogs in the apps being automated, pending clicks, and clicks that finished since the last call.
- `windows_dialogs`: list dialogs (Win32 only, works while UIA is blocked).
- `windows_dialog`: read, press buttons, set edit text, or close HWND-based dialogs (MessageBox, `#32770`, WinForms) with window messages, bypassing UI Automation.
- `windows_wait`: wait for a pending click to finish, a dialog to open, or a dialog to close.
- `FLAUI_MCP_UIA_TRANSACTION_TIMEOUT_MS` environment variable to cap UIA call time.
- Action results include post-action state: `windows_click`, `windows_fill`, `windows_dialog` press and `windows_batch` append a bounded snapshot of the dialog the action opened, or the app's foreground window. `postSnapshot: false` turns it off.
- `windows_batch` selectors (`name`, `nameContains`, `automationId`, `role`, `handle`, `index`), condition waits (`until`: `dialog_open`, `dialog_closed`, `element`, `element_gone`, `text_contains`), and `dialog_press` / `dialog_set_text` actions. A click followed by `wait until=dialog_open` no longer stops the batch.
- Post-action results list what changed (added / removed / changed elements, with refs) when the window has been snapshotted before, instead of repeating its first 150 elements.
- `windows_find`: search by name / nameContains / automationId / role and get refs plus each match's named ancestors, without a full snapshot.
- `windows_snapshot` `ref` (snapshot one element's subtree; other refs stay valid), `depth`, and `compact` (hide offscreen elements and unnamed single-child groups;). Post-action snapshots are always compact.
- `windows_batch` `keys` action (chords such as `Ctrl+Shift+B`); like a click, a dialog it opens stops the batch unless the next action waits for one.
- `settleMs` on `windows_click`, and `noDialog` / `settleMs` on batch clicks, to skip or shorten the 250 ms dialog-settle wait.
- Per-tool timing on stderr (`[timing] ...`: execution, status-block time, and the gap between calls), with a periodic per-tool summary. `FLAUI_MCP_TIMING=0` disables it.
- `FlaUI.Mcp.exe --bench-snapshot "<title>"` compares snapshot modes on a live window.
- Test apps: MessageBox and input-bearing modal dialogs (WinForms and WPF); `ModalDialogTests` integration tests; unit tests for dialog classification, the action runner, the status block and Win32 button matching.

### Changed
- `windows_batch` stops after a click that opens a dialog or doesn't return, instead of running later actions against a blocked app.
- `windows_snapshot` fails fast with guidance, rather than timing out, when a pending click is holding the app's UIA provider.
- `windows_screenshot` with a window handle captures the window's screen rectangle from its HWND, so it works while UIA is blocked.
- Window handles are stable: registering the same window again returns the same handle.
- Snapshots read element properties through a UIA `CacheRequest` (one call per element's children instead of about ten per element). `FLAUI_MCP_SNAPSHOT_MODE=live|cached|subtree` picks the strategy.
- Element refs are stable across snapshots: an element seen in the previous snapshot of a window keeps its ref (matched by runtime id), and a bounded snapshot keeps the refs it didn't reach.

## [0.2.0] - 2026-07-08

### Fixed
- Screenshots are now correct on scaled displays (DPI > 100%). Added `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` as the first call in the process entry point so UIA coordinates match physical pixels.
- Tool execution now times out after 30 seconds instead of hanging indefinitely when UI Automation blocks.

### Added
- Solution file (`FlaUI.Mcp.slnx`)
- xUnit test project (`tests/FlaUI.Mcp.Tests`) with DPI regression test
- `windows_send_keys` MCP tool for sending key presses and key chords.
- Opt-in `background` mode for `windows_screenshot` handle captures, with blank-frame fallback to the normal capture path.
- `savePath` and `overwrite` options for `windows_screenshot`, with local PNG path validation and atomic writes.
- Desktop integration test project with WinForms and WPF test applications.

## [0.1.0] - 2024-02-02

### Added
- Initial release
- **Core MCP Tools:**
  - `windows_launch` - Launch Windows applications
  - `windows_snapshot` - Capture accessibility tree with element refs
  - `windows_click` - Click elements by ref (uses Invoke pattern when available)
  - `windows_type` - Type text into elements
  - `windows_fill` - Clear and fill text fields
  - `windows_get_text` - Get element text content
  - `windows_screenshot` - Capture window/element screenshots
  - `windows_list_windows` - List all open windows
  - `windows_focus` - Bring window to foreground
  - `windows_close` - Close windows
  - `windows_batch` - Execute multiple actions in a single call

- **Architecture:**
  - MCP protocol handler (JSON-RPC over stdio)
  - Element registry for ref ↔ AutomationElement mapping
  - Snapshot builder for agent-friendly accessibility tree format
  - Session manager for tracking launched applications

- **Documentation:**
  - README with installation and usage instructions
  - GitHub Actions for CI/CD
  - MIT License

### Technical Details
- Built on [FlaUI](https://github.com/FlaUI/FlaUI) for Windows UI Automation
- Uses UIA3 for modern app support (WPF, UWP, Win32)
- Targets .NET 8.0-windows
- Prefers control patterns (Invoke, Value, Toggle) over mouse simulation
