using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using PlaywrightWindows.Mcp.Core.Actions;
using PlaywrightWindows.Mcp.Core.Batch;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// "Make it so" actions: check/uncheck, expand/collapse, select an option, set a value. Each
/// reads the current state first and does nothing if it's already right, so repeating one is
/// safe (unlike a click, which flips a checkbox back). They run through
/// <see cref="ClickExecutor.RunAsync"/>, so one that opens a modal dialog doesn't hang.
/// </summary>
public sealed class StateActions
{
    private static readonly TimeSpan ExpandSettle = TimeSpan.FromMilliseconds(150);

    private readonly ClickExecutor _executor;

    public StateActions(ClickExecutor executor)
    {
        _executor = executor;
    }

    public Task<ClickResult> SetCheckedAsync(AutomationElement element, string refId, bool check, ActionRunOptions? options = null) =>
        _executor.RunAsync(element, refId, check ? "check" : "uncheck", name => SetChecked(element, name, check), options);

    public Task<ClickResult> SetExpandedAsync(AutomationElement element, string refId, bool expand, ActionRunOptions? options = null) =>
        _executor.RunAsync(element, refId, expand ? "expand" : "collapse", name => SetExpanded(element, name, expand), options);

    /// <param name="option">Item to select inside a combo box, list, tree or tab list; null selects the element itself.</param>
    public Task<ClickResult> SelectAsync(AutomationElement element, string refId, string? option, ActionRunOptions? options = null) =>
        _executor.RunAsync(element, refId, option == null ? "select" : $"select \"{option}\" in",
            name => option == null ? SelectSelf(element, name) : SelectOption(element, name, option), options);

    public Task<ClickResult> SetValueAsync(AutomationElement element, string refId, string value, ActionRunOptions? options = null) =>
        _executor.RunAsync(element, refId, "set value of", name => SetValue(element, name, value), options);

    private static string SetChecked(AutomationElement element, string name, bool check)
    {
        var want = check ? ToggleState.On : ToggleState.Off;
        if (element.Patterns.Toggle.TryGetPattern(out var toggle))
        {
            // Up to three toggles: a three-state box goes Off -> On -> Indeterminate -> Off.
            for (var i = 0; i < 3; i++)
            {
                var state = toggle.ToggleState.Value;
                if (state == want)
                {
                    return i == 0 ? $"{name} is already {Describe(want)}" : $"{name} is now {Describe(want)}";
                }
                toggle.Toggle();
            }
            throw new InvalidOperationException($"{name} did not become {Describe(want)} (now {Describe(toggle.ToggleState.Value)}).");
        }

        if (element.Patterns.SelectionItem.TryGetPattern(out var item))
        {
            if (!check)
            {
                throw new InvalidOperationException($"{name} is a radio button: select another option instead of unchecking it.");
            }
            if (item.IsSelected.Value) return $"{name} is already selected";
            item.Select();
            return $"Selected {name}";
        }

        throw new InvalidOperationException($"{name} can't be checked (no Toggle or SelectionItem pattern). Use click.");
    }

    private static string Describe(ToggleState state) => state switch
    {
        ToggleState.On => "checked",
        ToggleState.Off => "unchecked",
        _ => "indeterminate",
    };

    private static string SetExpanded(AutomationElement element, string name, bool expand)
    {
        if (!element.Patterns.ExpandCollapse.TryGetPattern(out var pattern))
        {
            throw new InvalidOperationException($"{name} can't be expanded or collapsed (no ExpandCollapse pattern).");
        }
        var state = pattern.ExpandCollapseState.Value;
        if (state == ExpandCollapseState.LeafNode)
        {
            throw new InvalidOperationException($"{name} has nothing to expand.");
        }
        if (expand ? state == ExpandCollapseState.Expanded : state == ExpandCollapseState.Collapsed)
        {
            return $"{name} is already {(expand ? "expanded" : "collapsed")}";
        }
        if (expand) pattern.Expand(); else pattern.Collapse();
        return $"{(expand ? "Expanded" : "Collapsed")} {name}";
    }

    private static string SelectSelf(AutomationElement element, string name)
    {
        if (element.Patterns.SelectionItem.TryGetPattern(out var item))
        {
            if (item.IsSelected.Value) return $"{name} is already selected";
            TryScrollIntoView(element);
            item.Select();
            return $"Selected {name}";
        }
        throw new InvalidOperationException(
            $"{name} isn't selectable (no SelectionItem pattern). To pick an option inside it, pass 'option'.");
    }

    private static string SelectOption(AutomationElement container, string name, string option)
    {
        var expanded = false;
        var item = FindItem(container, option);

        // Virtualized lists only create items near the viewport; ask the container for it.
        if (item == null && container.Patterns.ItemContainer.TryGetPattern(out var items))
        {
            try
            {
                var found = items.FindItemByProperty(null!, container.Automation.PropertyLibrary.Element.Name, option);
                if (found != null)
                {
                    if (found.Patterns.VirtualizedItem.TryGetPattern(out var virtualized)) virtualized.Realize();
                    item = found;
                }
            }
            catch
            {
                // Not all providers implement it properly; fall through.
            }
        }

        // Combo boxes (WPF especially) create their items when the drop-down opens.
        if (item == null && container.Patterns.ExpandCollapse.TryGetPattern(out var dropDown)
            && dropDown.ExpandCollapseState.ValueOrDefault == ExpandCollapseState.Collapsed)
        {
            dropDown.Expand();
            expanded = true;
            Thread.Sleep(ExpandSettle);
            item = FindItem(container, option);
        }

        try
        {
            if (item == null)
            {
                var names = ItemNames(container);
                throw new InvalidOperationException(
                    $"No option \"{option}\" in {name}." +
                    (names.Count > 0 ? $" Options: {string.Join(", ", names.Select(n => $"\"{n}\""))}." : ""));
            }

            var itemName = item.Properties.Name.ValueOrDefault ?? option;
            if (!item.Patterns.SelectionItem.TryGetPattern(out var selectable))
            {
                throw new InvalidOperationException($"\"{itemName}\" in {name} isn't selectable.");
            }
            if (selectable.IsSelected.ValueOrDefault)
            {
                return $"\"{itemName}\" is already selected in {name}";
            }
            TryScrollIntoView(item);
            selectable.Select();
            return $"Selected \"{itemName}\" in {name}";
        }
        finally
        {
            if (expanded && container.Patterns.ExpandCollapse.TryGetPattern(out var close)
                && close.ExpandCollapseState.ValueOrDefault == ExpandCollapseState.Expanded)
            {
                try { close.Collapse(); } catch { }
            }
        }
    }

    private static ConditionBase Selectable(AutomationElement container) =>
        new PropertyCondition(container.Automation.PropertyLibrary.PatternAvailability.IsSelectionItemPatternAvailable, true);

    private static AutomationElement? FindItem(AutomationElement container, string option)
    {
        var candidates = container.FindAll(TreeScope.Descendants, Selectable(container));
        return NameMatch.Pick(candidates, option, e => Try(() => e.Properties.Name.ValueOrDefault));
    }

    private static IReadOnlyList<string> ItemNames(AutomationElement container)
    {
        try
        {
            return container.FindAll(TreeScope.Descendants, Selectable(container))
                .Select(e => Try(() => e.Properties.Name.ValueOrDefault))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct()
                .Take(30)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string SetValue(AutomationElement element, string name, string value)
    {
        if (element.Patterns.RangeValue.TryGetPattern(out var range))
        {
            if (!ValueParsing.TryParseNumber(value, out var number))
            {
                throw new InvalidOperationException($"{name} takes a number (it's a range from {range.Minimum.Value} to {range.Maximum.Value}).");
            }
            var (min, max) = (range.Minimum.Value, range.Maximum.Value);
            if (number < min || number > max)
            {
                throw new InvalidOperationException($"{number} is outside {name}'s range {min}..{max}.");
            }
            range.SetValue(number);
            return $"Set {name} to {range.Value.ValueOrDefault.ToString(CultureInfo.InvariantCulture)}";
        }
        if (element.Patterns.Value.TryGetPattern(out var text))
        {
            if (text.IsReadOnly.ValueOrDefault) throw new InvalidOperationException($"{name} is read-only.");
            text.SetValue(value);
            return $"Set {name} to \"{value}\"";
        }
        throw new InvalidOperationException($"{name} has no value to set (no RangeValue or Value pattern).");
    }

    private static void TryScrollIntoView(AutomationElement element)
    {
        try
        {
            if (element.Patterns.ScrollItem.TryGetPattern(out var scroll)) scroll.ScrollIntoView();
        }
        catch
        {
            // Best effort: selecting usually works without it.
        }
    }

    private static T? Try<T>(Func<T> read)
    {
        try { return read(); } catch { return default; }
    }
}
