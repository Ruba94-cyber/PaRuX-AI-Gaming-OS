using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace PaRuX.UITests.Infrastructure;

internal static class UiWait
{
    /// <summary>
    /// A UIA call can transiently time out (COMException 0x80131505) while the app is busy laying out; that is a not-yet-observable
    /// state, not a verdict. The enclosing wait stays bounded and still fails with a TimeoutException.
    /// </summary>
    private static bool Probe(Func<bool> condition)
    {
        try { return condition(); }
        catch (System.Runtime.InteropServices.COMException) { return false; }
        catch (FlaUI.Core.Exceptions.ElementNotAvailableException) { return false; }
    }

    internal static void For(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        DateTime until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < until)
        {
            if (Probe(condition)) return;
            Thread.Sleep(100); // bounded polling is required because UIA exposes no change event for every WinUI property.
        }
        throw new TimeoutException("Timed out waiting for " + description + ".");
    }

    /// <summary>
    /// Waits for a condition that has to hold on two consecutive polls (about 150 ms apart). A single true read can be a transient frame:
    /// a control that is enabled between two layout passes, an element that is on screen only while a page transition scrolls.
    /// The wait stays bounded and still fails with a TimeoutException when the state never settles.
    /// </summary>
    internal static void ForStable(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        DateTime until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        bool previous = false;
        while (DateTime.UtcNow < until)
        {
            bool current = Probe(condition);
            if (current && previous) return;
            previous = current;
            Thread.Sleep(150);
        }
        throw new TimeoutException("Timed out waiting for a stable " + description + ".");
    }

    /// <summary>Asserts that a control is enabled once its page has settled. Controls are disabled while a page loads or a job runs, and
    /// the read must not race that transition. A control that never becomes enabled still fails the assertion.</summary>
    internal static void AssertEnabled(AutomationElement element, string? message = null)
    {
        try { ForStable(() => element.Properties.IsEnabled.ValueOrDefault, "enabled state", TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { Xunit.Assert.True(false, message ?? "The control stayed disabled: " + DescribeForFailure(element)); }
    }

    /// <summary>Waits until the element is on screen (not off screen) on two consecutive reads.</summary>
    internal static void ForOnScreen(AutomationElement element, string description, TimeSpan? timeout = null) =>
        ForStable(() => !element.Properties.IsOffscreen.ValueOrDefault, description + " on screen", timeout);

    private static string DescribeForFailure(AutomationElement element)
    {
        try { return element.Properties.AutomationId.ValueOrDefault + " / " + element.Properties.Name.ValueOrDefault; }
        catch (Exception) { return "(element no longer available)"; }
    }

    /// <summary>
    /// Sends an input action (key press or click) and waits for its observable effect. SendInput can be lost while the XAML island is still
    /// taking keyboard focus after a UIA Focus(); when the input never produced the effect it is sent again
    /// (bounded), so a lost input is not reported as a product failure and a delivered one is never repeated.
    /// </summary>
    internal static void InputUntil(Action sendKey, Func<bool> effect, string description, int attempts = 3)
    {
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            sendKey();
            DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(attempt == attempts ? 20 : 2);
            while (DateTime.UtcNow < until)
            {
                if (Probe(effect)) return;
                Thread.Sleep(100);
            }
        }

        throw new TimeoutException("Timed out waiting for " + description + ".");
    }

    /// <summary>Expands a collapsed WinUI Expander. Collapsed expander content is not part of the UIA tree, so controls
    /// inside it can only be found after this. Idempotent when already expanded.</summary>
    internal static void ExpandById(AutomationElement root, string expanderId)
    {
        AutomationElement expander = For<AutomationElement>(() =>
            root.FindFirstDescendant(cf => cf.ByAutomationId(expanderId)), "expander " + expanderId);
        For(() =>
        {
            var pattern = expander.Patterns.ExpandCollapse.PatternOrDefault;
            if (pattern == null) return false;
            if (pattern.ExpandCollapseState.ValueOrDefault != FlaUI.Core.Definitions.ExpandCollapseState.Expanded) pattern.Expand();
            return pattern.ExpandCollapseState.ValueOrDefault == FlaUI.Core.Definitions.ExpandCollapseState.Expanded;
        }, "expander " + expanderId + " to expand");
    }

    /// <summary>AutomationId of the currently selected item of a ComboBox (null when nothing is selected).</summary>
    internal static string? SelectedItemId(AutomationElement combo)
    {
        var selection = combo.Patterns.Selection.PatternOrDefault?.Selection.ValueOrDefault;
        return selection is { Length: > 0 } ? selection[0].Properties.AutomationId.ValueOrDefault : null;
    }

    private static T For<T>(Func<T?> find, string description) where T : class
    {
        T? result = null;
        For(() => (result = find()) != null, description);
        return result!;
    }

    /// <summary>A ContentDialog button (PrimaryButton / SecondaryButton / CloseButton). Those ids are not unique: an InfoBar close button
    /// is also "CloseButton". Only a button whose parent is the dialog's Popup window counts; null while no dialog is open.</summary>
    internal static AutomationElement? DialogButton(AutomationElement root, string id)
    {
        foreach (AutomationElement candidate in root.FindAllDescendants(cf => cf.ByAutomationId(id)))
            if (candidate.Parent?.Properties.ClassName.ValueOrDefault == "Popup") return candidate;
        return null;
    }

    internal static AutomationElement ById(AutomationElement root, string id)
    {
        AutomationElement? element = null;
        For(() =>
        {
            EnsureModeTabFor(root, id);
            element = root.FindFirstDescendant(cf => cf.ByAutomationId(id));
            if (element == null) { PageObjects.GamingSectionsPage.Reveal(root, id); PageObjects.OnboardingDisclosurePage.Reveal(root, id); }
            return element != null;
        }, "UI element " + id);
        return element ?? throw new InvalidOperationException("Missing UI element: " + id + ".");
    }

    internal static AutomationElement ById(AutomationElement root, string id, ControlType type)
    {
        AutomationElement? element = null;
        For(() =>
        {
            EnsureModeTabFor(root, id);
            element = root.FindFirstDescendant(cf => cf.ByAutomationId(id).And(cf.ByControlType(type)));
            if (element == null) { PageObjects.GamingSectionsPage.Reveal(root, id); PageObjects.OnboardingDisclosurePage.Reveal(root, id); }
            return element != null;
        }, "UI element " + id + " (" + type + ")");
        return element ?? throw new InvalidOperationException("Missing UI element: " + id + " (" + type + ").");
    }

    /// <summary>The AI | Gaming mode tabs hide the sidebar items of the other tab, and hidden items are not in the UIA tree.
    /// Before looking up a Navigation_* item, select the tab that owns it. Settings and Help are visible in both tabs.</summary>
    internal static void EnsureModeTabFor(AutomationElement root, string id)
    {
        // Console / TV is a gaming control: its footer button lives in the Gaming tab only.
        bool gamingOnly = id == "MainWindow_ConsoleMode_Button";
        if (!gamingOnly && !id.StartsWith("Navigation_", StringComparison.Ordinal)) return;
        if (id is "Navigation_Settings_Item" or "Navigation_Help_Item") return;
        string tabId = gamingOnly || id == "Navigation_GameLibrary_Item" ? "MainWindow_ModeTab_Gaming" : "MainWindow_ModeTab_AI";
        try
        {
            AutomationElement? tab = root.FindFirstDescendant(cf => cf.ByAutomationId(tabId));
            var selection = tab?.Patterns.SelectionItem.PatternOrDefault;
            if (selection != null)
            {
                if (!selection.IsSelected.ValueOrDefault) selection.Select();
            }
            else
            {
                // SelectorBarItem may expose only Invoke.
                tab?.Patterns.Invoke.PatternOrDefault?.Invoke();
            }
        }
        catch (Exception) { /* the tab strip is not reachable yet; the caller keeps polling */ }
    }
}
