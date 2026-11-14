using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;

namespace PaRuX.UITests.PageObjects;

internal sealed class SettingsCategoriesPage
{
    private readonly Window _window;
    private readonly MainWindowPage _main;

    internal static readonly string[] Categories =
    {
        "General", "Models", "Chat Behavior", "Your PC apps", "Media Generation",
        "Automation", "Agents", "Integrations", "Gaming & AI Sharing",
        "PaRuX Platform", "Privacy & Network", "Data", "Advanced"
    };

    internal SettingsCategoriesPage(Window window)
    {
        _window = window;
        _main = new MainWindowPage(window);
    }

    internal static string CategoryButtonId(string category) =>
        "Settings_Category_" + string.Concat(category.Where(char.IsLetterOrDigit)) + "_Button";

    internal AutomationElement GetCategoryButton(string category) =>
        UiWait.ById(_window, CategoryButtonId(category), ControlType.Button);

    internal void SelectCategory(string category)
    {
        _main.OpenSettingsCategory(CategoryButtonId(category));
    }

    internal AutomationElement? FindSectionHeading(string heading)
    {
        return _window.FindFirstDescendant(cf =>
            cf.ByControlType(ControlType.Text).And(cf.ByName(heading)));
    }

    internal AutomationElement WaitForSectionHeadingVisible(string heading, string context = "")
    {
        AutomationElement? element = null;
        // Two consecutive on-screen reads: a heading is briefly on screen while the page is still scrolling to its final offset.
        UiWait.ForStable(() =>
        {
            element = FindSectionHeading(heading);
            return element != null && !element.Properties.IsOffscreen.ValueOrDefault;
        }, "heading '" + heading + "' visible " + context);
        return element!;
    }

    internal void WaitForSectionHeadingHidden(string heading, string context = "")
    {
        UiWait.For(() =>
        {
            var element = FindSectionHeading(heading);
            return element == null || element.Properties.IsOffscreen.ValueOrDefault;
        }, "heading '" + heading + "' hidden " + context);
    }

    internal static void ScrollIntoView(AutomationElement element)
    {
        try { element.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); }
        catch { }
    }

    internal void ScrollSettingsIntoView(AutomationElement element)
    {
        var host = UiWait.ById(_window, "SettingsContentScroll", ControlType.Pane);
        bool VisibleInViewport()
        {
            if (!element.Properties.IsOffscreen.TryGetValue(out bool offscreen) || offscreen ||
                !element.Properties.BoundingRectangle.TryGetValue(out var bounds) ||
                !host.Properties.BoundingRectangle.TryGetValue(out var viewport)) return false;
            var intersection = System.Drawing.Rectangle.Intersect(bounds, viewport);
            return bounds.Width > 0 && bounds.Height > 0 && intersection.Width > 0 && intersection.Height > 0;
        }

        if (VisibleInViewport()) return;
        try
        {
            element.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
        }
        catch
        {
            // Use the settings content scroll host if the child cannot scroll itself.
        }
        if (VisibleInViewport()) return;

        var scroll = host.Patterns.Scroll.PatternOrDefault
            ?? throw new InvalidOperationException("SettingsContentScroll does not expose UIA ScrollPattern.");
        for (int percent = 0; percent <= 100; percent += 5)
        {
            scroll.SetScrollPercent(-1, percent);
            try
            {
                UiWait.For(VisibleInViewport, "settings control in view", TimeSpan.FromMilliseconds(400));
                return;
            }
            catch (TimeoutException) { }
        }
        throw new TimeoutException("The settings control could not be scrolled into view.");
    }
}
