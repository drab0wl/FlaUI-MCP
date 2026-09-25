using FlaUI.Core.AutomationElements;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Maps element refs (like "w1e5") to AutomationElements
/// Refs are scoped to windows and regenerated on each snapshot
/// </summary>
public class ElementRegistry
{
    private readonly RefTable<AutomationElement> _table = new();

    /// <summary>
    /// Clear all elements for a window (called before new snapshot)
    /// </summary>
    public void ClearWindow(string windowHandle) => _table.BeginSnapshot(windowHandle);

    /// <summary>
    /// Register an element and return its ref
    /// </summary>
    /// <param name="stableKey">
    /// Identity of the element across snapshots (its UIA runtime id). An element seen in the
    /// previous snapshot of the window keeps its ref.
    /// </param>
    public string Register(string windowHandle, AutomationElement element, string? stableKey = null) =>
        _table.Register(windowHandle, element, stableKey);

    /// <summary>
    /// Register an element found outside a snapshot (by a selector). If the latest snapshot of
    /// the window already has it, its existing ref is returned.
    /// </summary>
    public string RegisterFound(string windowHandle, AutomationElement element, string? stableKey) =>
        _table.RegisterFound(windowHandle, element, stableKey);

    /// <summary>
    /// Call after a snapshot. A truncated snapshot keeps the refs it didn't reach, so a bounded
    /// snapshot (e.g. after a click) doesn't invalidate refs from an earlier full one.
    /// </summary>
    public void CompleteSnapshot(string windowHandle, bool truncated) => _table.CompleteSnapshot(windowHandle, truncated);

    /// <summary>
    /// Get an element by its ref
    /// </summary>
    public AutomationElement? GetElement(string refId) => _table.Get(refId);

    /// <summary>
    /// Check if a ref exists
    /// </summary>
    public bool HasElement(string refId) => _table.Get(refId) != null;

    /// <summary>Refs look like "w3e12"; "w3" is the window the element was snapshotted from.</summary>
    public static string? WindowHandleOf(string refId)
    {
        var match = System.Text.RegularExpressions.Regex.Match(refId, @"^(w\d+)e\d+$");
        return match.Success ? match.Groups[1].Value : null;
    }
}

/// <summary>
/// Ref bookkeeping behind <see cref="ElementRegistry"/>, generic so it can be unit tested.
/// </summary>
public sealed class RefTable<T> where T : class
{
    private readonly object _gate = new();
    private readonly Dictionary<string, T> _items = new();
    private readonly Dictionary<string, WindowRefs> _windows = new();

    private sealed class WindowRefs
    {
        public int Counter;
        public Dictionary<string, string> KeyToRef = new();
        public Dictionary<string, string> PreviousKeyToRef = new();
        public Dictionary<string, T> Previous = new();
        public HashSet<string> Current = new();
    }

    public void BeginSnapshot(string window)
    {
        lock (_gate)
        {
            var w = WindowFor(window);
            var prefix = window + "e";
            w.Previous = _items.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var key in w.Previous.Keys) _items.Remove(key);
            w.PreviousKeyToRef = w.KeyToRef;
            w.KeyToRef = new Dictionary<string, string>();
            w.Current = new HashSet<string>();
            // Nothing to keep stable against: number from 1 as before.
            if (w.PreviousKeyToRef.Count == 0) w.Counter = 0;
        }
    }

    public string Register(string window, T item, string? stableKey = null)
    {
        lock (_gate)
        {
            var w = WindowFor(window);
            string? refId = null;
            if (stableKey != null
                && w.PreviousKeyToRef.TryGetValue(stableKey, out var previous)
                && !w.Current.Contains(previous))
            {
                refId = previous;
            }
            refId ??= $"{window}e{++w.Counter}";

            if (stableKey != null) w.KeyToRef.TryAdd(stableKey, refId);
            w.Current.Add(refId);
            _items[refId] = item;
            return refId;
        }
    }

    public string RegisterFound(string window, T item, string? stableKey)
    {
        lock (_gate)
        {
            var w = WindowFor(window);
            if (stableKey != null && w.KeyToRef.TryGetValue(stableKey, out var existing))
            {
                _items[existing] = item;
                return existing;
            }
            var refId = $"{window}e{++w.Counter}";
            if (stableKey != null) w.KeyToRef[stableKey] = refId;
            _items[refId] = item;
            return refId;
        }
    }

    public void CompleteSnapshot(string window, bool truncated)
    {
        lock (_gate)
        {
            var w = WindowFor(window);
            if (truncated)
            {
                foreach (var (refId, item) in w.Previous)
                {
                    if (w.Current.Contains(refId)) continue;
                    _items.TryAdd(refId, item);
                }
                foreach (var (key, refId) in w.PreviousKeyToRef)
                {
                    if (!w.Current.Contains(refId)) w.KeyToRef.TryAdd(key, refId);
                }
            }
            w.Previous = new Dictionary<string, T>();
            w.PreviousKeyToRef = new Dictionary<string, string>();
        }
    }

    public T? Get(string refId)
    {
        lock (_gate) return _items.TryGetValue(refId, out var item) ? item : null;
    }

    private WindowRefs WindowFor(string window)
    {
        if (!_windows.TryGetValue(window, out var w)) _windows[window] = w = new WindowRefs();
        return w;
    }
}
