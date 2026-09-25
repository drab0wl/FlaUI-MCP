using System.Diagnostics;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Identifiers;
using PlaywrightWindows.Mcp.Core.Diagnostics;
using PlaywrightWindows.Mcp.Core.Snapshots;

namespace PlaywrightWindows.Mcp.Core;

public enum SnapshotMode
{
    /// <summary>One UIA call per property per element (the original implementation).</summary>
    Live,
    /// <summary>
    /// Each element's children are fetched with one CacheRequest call that also returns every
    /// property the snapshot reads. Stops early at MaxNodes/MaxDepth like Live does.
    /// </summary>
    Cached,
    /// <summary>
    /// The whole subtree is fetched in a single CacheRequest call. Fewest round trips, but it
    /// can't stop early, so it reads everything below the root regardless of limits.
    /// </summary>
    CachedSubtree,
}

public sealed record SnapshotResult(string Text, int Nodes, bool Truncated, TimeSpan Elapsed, SnapshotMode Mode);

/// <summary>
/// Builds agent-friendly accessibility snapshots from UI Automation trees
/// </summary>
public class SnapshotBuilder
{
    private readonly ElementRegistry _elementRegistry;
    private readonly int _maxDepth;
    private readonly SnapshotMode _mode;

    public SnapshotBuilder(ElementRegistry elementRegistry, int maxDepth = 10, SnapshotMode? mode = null)
    {
        _elementRegistry = elementRegistry;
        _maxDepth = maxDepth;
        _mode = mode ?? DefaultMode;
    }

    /// <summary>FLAUI_MCP_SNAPSHOT_MODE=live|cached|subtree; default cached.</summary>
    public static SnapshotMode DefaultMode { get; } = ParseMode(Environment.GetEnvironmentVariable("FLAUI_MCP_SNAPSHOT_MODE"));

    public static SnapshotMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "live" => SnapshotMode.Live,
        "subtree" or "cachedsubtree" => SnapshotMode.CachedSubtree,
        _ => SnapshotMode.Cached,
    };

    public SnapshotMode Mode => _mode;

    /// <summary>
    /// Live mode reads each element's runtime id (for stable refs). Off reproduces the original
    /// implementation's calls exactly, for benchmarking.
    /// </summary>
    public bool LiveRuntimeIds { get; init; } = true;

    public string BuildSnapshot(string windowHandle, AutomationElement root) => Build(windowHandle, root).Text;

    public SnapshotResult Build(string windowHandle, AutomationElement root, SnapshotLimits? limits = null)
    {
        limits ??= SnapshotLimits.Unbounded with { MaxDepth = _maxDepth };
        var sw = Stopwatch.StartNew();

        // Clear previous elements for this window
        _elementRegistry.ClearWindow(windowHandle);

        var walk = new Walk(windowHandle, limits);
        var mode = _mode;
        try
        {
            switch (mode)
            {
                case SnapshotMode.Live:
                    VisitLive(walk, root, 0);
                    break;
                default:
                    if (!TryVisitCached(walk, root, mode))
                    {
                        // The provider rejected the cache request (before anything was
                        // registered); fall back to plain reads.
                        mode = SnapshotMode.Live;
                        VisitLive(walk, root, 0);
                    }
                    break;
            }
        }
        finally
        {
            _elementRegistry.CompleteSnapshot(windowHandle, walk.Truncated);
        }

        if (walk.Truncated)
        {
            walk.Text.AppendLine(SnapshotFormat.TruncationNote(walk.Nodes, walk.TruncatedReason!));
        }

        var result = new SnapshotResult(walk.Text.ToString(), walk.Nodes, walk.Truncated, sw.Elapsed, mode);
        TimingLog.Shared.Detail("snapshot", result.Elapsed,
            $"handle={windowHandle} mode={mode} nodes={result.Nodes}{(result.Truncated ? " truncated" : "")}");
        return result;
    }

    private sealed class Walk
    {
        public Walk(string handle, SnapshotLimits limits)
        {
            Handle = handle;
            Limits = limits;
        }

        public string Handle { get; }
        public SnapshotLimits Limits { get; }
        public StringBuilder Text { get; } = new();
        public int Nodes { get; set; }
        public bool Truncated => TruncatedReason != null;
        public string? TruncatedReason { get; set; }

        /// <summary>False (and marks the walk truncated) once a limit is hit.</summary>
        public bool CanContinue()
        {
            if (Truncated) return false;
            if (Nodes >= Limits.MaxNodes) TruncatedReason = $"limit of {Limits.MaxNodes} elements";
            else if (Text.Length >= Limits.MaxChars) TruncatedReason = $"limit of {Limits.MaxChars} characters";
            else if (Limits.DeadlineUtc is { } deadline && DateTime.UtcNow >= deadline) TruncatedReason = "time limit";
            return !Truncated;
        }
    }

    /// <returns>False if the element is skipped (and its children with it).</returns>
    private bool Emit(Walk walk, AutomationElement element, SnapshotNode node, int depth)
    {
        var name = SnapshotFormat.DisplayName(node.Name, node.AutomationId);
        var role = SnapshotFormat.Role(node.ControlType);
        if (SnapshotFormat.ShouldSkip(name, role)) return false;

        var refId = _elementRegistry.Register(walk.Handle, element, node.RuntimeId);
        walk.Text.Append(SnapshotFormat.Indent(depth)).Append("- ")
            .AppendLine(SnapshotFormat.Line(refId, name, role, SnapshotFormat.States(node)));
        walk.Nodes++;
        return true;
    }

    // ---------- Live: the original per-property reads ----------

    private void VisitLive(Walk walk, AutomationElement element, int depth)
    {
        if (depth > walk.Limits.MaxDepth || !walk.CanContinue()) return;
        if (!Emit(walk, element, ReadLive(element, LiveRuntimeIds), depth)) return;

        try
        {
            foreach (var child in element.FindAllChildren())
            {
                VisitLive(walk, child, depth + 1);
            }
        }
        catch
        {
            // Some elements throw when accessing children
        }
    }

    internal static SnapshotNode ReadLive(AutomationElement element, bool includeRuntimeId = true)
    {
        string? name = null, automationId = null;
        try
        {
            name = element.Properties.Name.ValueOrDefault;
            if (string.IsNullOrWhiteSpace(name)) automationId = element.Properties.AutomationId.ValueOrDefault;
        }
        catch
        {
        }

        var controlType = ControlType.Unknown;
        try { controlType = element.Properties.ControlType.ValueOrDefault; } catch { }

        bool enabled = true, offscreen = false, readOnly = false, selected = false;
        ToggleState? toggle = null;
        ExpandCollapseState? expand = null;
        try
        {
            enabled = element.Properties.IsEnabled.ValueOrDefault;
            offscreen = element.Properties.IsOffscreen.ValueOrDefault;
            if (element.Patterns.Value.IsSupported)
                readOnly = element.Patterns.Value.Pattern.IsReadOnly.ValueOrDefault;
            if (element.Patterns.Toggle.IsSupported)
                toggle = element.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault;
            if (element.Patterns.SelectionItem.IsSupported)
                selected = element.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault;
            if (element.Patterns.ExpandCollapse.IsSupported)
                expand = element.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.ValueOrDefault;
        }
        catch
        {
            // Ignore state query errors
        }

        string? runtimeId = null;
        if (includeRuntimeId)
        {
            try { runtimeId = RuntimeIdKey(element.Properties.RuntimeId.ValueOrDefault); } catch { }
        }

        return new SnapshotNode(name, automationId, controlType, enabled, offscreen, readOnly, toggle, selected, expand)
        {
            RuntimeId = runtimeId,
        };
    }

    // ---------- Cached ----------

    /// <summary>Every property <see cref="ReadCached"/> reads, fetched in the same call as the element.</summary>
    public static CacheRequest CreateCacheRequest(AutomationBase automation, TreeScope scope)
    {
        var p = automation.PropertyLibrary;
        var request = new CacheRequest
        {
            TreeScope = scope,
            // Full: the elements stay usable for live calls (clicks, patterns) after the walk.
            AutomationElementMode = AutomationElementMode.Full,
        };
        foreach (var id in new[]
                 {
                     p.Element.Name,
                     p.Element.AutomationId,
                     p.Element.ControlType,
                     p.Element.IsEnabled,
                     p.Element.IsOffscreen,
                     p.Element.RuntimeId,
                     p.PatternAvailability.IsValuePatternAvailable,
                     p.PatternAvailability.IsTogglePatternAvailable,
                     p.PatternAvailability.IsSelectionItemPatternAvailable,
                     p.PatternAvailability.IsExpandCollapsePatternAvailable,
                     p.PatternAvailability.IsInvokePatternAvailable,
                     p.Value.IsReadOnly,
                     p.Toggle.ToggleState,
                     p.SelectionItem.IsSelected,
                     p.ExpandCollapse.ExpandCollapseState,
                 })
        {
            request.Add(id);
        }
        return request;
    }

    private bool TryVisitCached(Walk walk, AutomationElement root, SnapshotMode mode)
    {
        var automation = root.Automation;
        var scope = mode == SnapshotMode.CachedSubtree ? TreeScope.Subtree : TreeScope.Element;
        var request = CreateCacheRequest(automation, scope);

        using (request.Activate())
        {
            AutomationElement? cachedRoot;
            try
            {
                cachedRoot = root.FrameworkAutomationElement.GetUpdatedCache();
            }
            catch
            {
                return false;
            }
            if (cachedRoot == null) return false;

            if (mode == SnapshotMode.CachedSubtree)
            {
                VisitCachedSubtree(walk, cachedRoot, automation, 0);
            }
            else
            {
                VisitCached(walk, cachedRoot, automation, 0);
            }
            return true;
        }
    }

    private void VisitCached(Walk walk, AutomationElement element, AutomationBase automation, int depth)
    {
        if (depth > walk.Limits.MaxDepth || !walk.CanContinue()) return;
        if (!Emit(walk, element, ReadCached(element, automation), depth)) return;

        AutomationElement[] children;
        try
        {
            // One call: the children and all their snapshot properties.
            children = element.FindAllChildren();
        }
        catch
        {
            return;
        }
        foreach (var child in children)
        {
            VisitCached(walk, child, automation, depth + 1);
        }
    }

    private void VisitCachedSubtree(Walk walk, AutomationElement element, AutomationBase automation, int depth)
    {
        if (depth > walk.Limits.MaxDepth || !walk.CanContinue()) return;
        if (!Emit(walk, element, ReadCached(element, automation), depth)) return;

        AutomationElement[] children;
        try
        {
            children = element.CachedChildren;
        }
        catch
        {
            return;
        }
        foreach (var child in children)
        {
            VisitCachedSubtree(walk, child, automation, depth + 1);
        }
    }

    /// <summary>Reads the snapshot properties from the element's cache (no cross-process calls).</summary>
    internal static SnapshotNode ReadCached(AutomationElement element, AutomationBase automation)
    {
        var p = automation.PropertyLibrary;
        var e = element.FrameworkAutomationElement;

        var name = Get<string>(e, p.Element.Name);
        var automationId = string.IsNullOrWhiteSpace(name) ? Get<string>(e, p.Element.AutomationId) : null;

        var hasValue = Get<bool>(e, p.PatternAvailability.IsValuePatternAvailable);
        var hasToggle = Get<bool>(e, p.PatternAvailability.IsTogglePatternAvailable);
        var hasSelection = Get<bool>(e, p.PatternAvailability.IsSelectionItemPatternAvailable);
        var hasExpand = Get<bool>(e, p.PatternAvailability.IsExpandCollapsePatternAvailable);

        return new SnapshotNode(
            name,
            automationId,
            Get(e, p.Element.ControlType, ControlType.Unknown),
            Get(e, p.Element.IsEnabled, true),
            Get<bool>(e, p.Element.IsOffscreen),
            hasValue && Get<bool>(e, p.Value.IsReadOnly),
            hasToggle ? Get(e, p.Toggle.ToggleState, ToggleState.Off) : null,
            hasSelection && Get<bool>(e, p.SelectionItem.IsSelected),
            hasExpand ? Get(e, p.ExpandCollapse.ExpandCollapseState, ExpandCollapseState.LeafNode) : null)
        {
            RuntimeId = RuntimeIdKey(Get<int[]>(e, p.Element.RuntimeId)),
        };
    }

    private static T? Get<T>(FrameworkAutomationElementBase element, PropertyId id, T? fallback = default)
    {
        try
        {
            return element.TryGetPropertyValue<T>(id, out var value) ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    internal static string? RuntimeIdKey(int[]? runtimeId) =>
        runtimeId is { Length: > 0 } ? string.Join(".", runtimeId) : null;
}
