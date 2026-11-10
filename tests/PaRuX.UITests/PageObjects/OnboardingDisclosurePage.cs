using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace PaRuX.UITests.PageObjects;

/// <summary>
/// First-run onboarding keeps "Manual setup, models and downloads" collapsed until the person opens it (progressive disclosure).
/// Collapsed Expander content is not in the UIA tree, so a test that names a control inside it must open the Expander first.
/// <c>UiWait.ById</c> calls <see cref="Reveal"/> only after a lookup missed, so a control that is already visible is never disturbed.
/// </summary>
internal static class OnboardingDisclosurePage
{
    internal const string ManualSetupExpander = "Onboarding_ManualSetup_Expander";

    internal static bool IsInsideManualSetup(string id) =>
        id.StartsWith("FirstModel", StringComparison.Ordinal)
        || id is "ComponentsStatusText" or "ComponentsProgress" or "ComponentsList"
            or "Onboarding_VerifyLocalChat_Button" or "Onboarding_VerifyLocalChat_Status";

    internal static void Reveal(AutomationElement root, string id)
    {
        if (!IsInsideManualSetup(id)) return;
        try
        {
            var pattern = root.FindFirstDescendant(cf => cf.ByAutomationId(ManualSetupExpander))?.Patterns.ExpandCollapse.PatternOrDefault;
            if (pattern != null && pattern.ExpandCollapseState.ValueOrDefault == ExpandCollapseState.Collapsed) pattern.Expand();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                       or FlaUI.Core.Exceptions.ElementNotAvailableException
                                       or FlaUI.Core.Exceptions.PatternNotSupportedException) { }
    }
}
