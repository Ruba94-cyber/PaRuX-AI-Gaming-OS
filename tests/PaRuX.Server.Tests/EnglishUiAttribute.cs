using System.Globalization;
using System.Reflection;
using Xunit.Sdk;

namespace PaRuX.Server.Tests;

/// <summary>
/// Pins the UI language of the test thread to English for tests that assert the English wording of text that goes through
/// <c>UiText.Pick</c>. Without it such a test passes on an English machine and fails on an Italian one, because the UI language
/// follows the thread culture unless the app set it. Only this thread's culture is touched, never the process-wide default.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class EnglishUiAttribute : BeforeAfterTestAttribute
{
    private CultureInfo? _previous;

    public override void Before(MethodInfo methodUnderTest)
    {
        _previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    }

    public override void After(MethodInfo methodUnderTest)
    {
        if (_previous is not null) CultureInfo.CurrentUICulture = _previous;
    }
}
