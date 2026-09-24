# Changelog

All notable changes to FlaUI-MCP will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Modal dialog handling. `windows_click` no longer hangs when a click opens a modal dialog: the UIA call runs on its own thread while a Win32 monitor watches the target process for new dialogs. The click returns as soon as a dialog appears, with the dialog's window handle, and becomes a pending operation (`op1`, ...) that finishes when the dialog closes.
- `windows_click` `mode`: `auto` (default), `invoke` (UIA patterns only), `input` (real mouse click at the element's clickable point, bringing its window forward first and refusing if another app covers the point). `mode` is also accepted by `windows_batch` click actions.
- Every tool result ends with a status block listing open modal/Win32 dialogs in the apps being automated, pending clicks, and clicks that finished since the last call.
- `windows_dialogs`: list dialogs (Win32 only, works while UIA is blocked).
- `windows_dialog`: read, press buttons, set edit text, or close HWND-based dialogs (MessageBox, `#32770`, WinForms) with window messages, bypassing UI Automation.
- `windows_wait`: wait for a pending click to finish, a dialog to open, or a dialog to close.
- `FLAUI_MCP_UIA_TRANSACTION_TIMEOUT_MS` environment variable to cap UIA call time.
- Test apps: MessageBox and input-bearing modal dialogs (WinForms and WPF); `ModalDialogTests` integration tests; unit tests for dialog classification, the action runner, the status block and Win32 button matching.

### Changed
- `windows_batch` stops after a click that opens a dialog or doesn't return, instead of running later actions against a blocked app.
- `windows_snapshot` fails fast with guidance, rather than timing out, when a pending click is holding the app's UIA provider.
- `windows_screenshot` with a window handle captures the window's screen rectangle from its HWND, so it works while UIA is blocked.
- Window handles are stable: registering the same window again returns the same handle.

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
