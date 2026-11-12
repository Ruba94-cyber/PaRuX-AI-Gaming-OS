using System;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// XAML half of the inline English/Italian convention. The English text stays in the normal attribute
    /// (<c>Content="Save"</c>); the Italian text sits right after it in an attached property
    /// (<c>loc:Loc.ItContent="Salva"</c>). When Italian is selected the setter overwrites the English value
    /// as the element is parsed, otherwise it does nothing. Because attributes are applied in document order,
    /// the Italian attribute must follow the English one (a source-contract test enforces this).
    /// Empty Italian falls back to English. AutomationIds are never touched.
    /// </summary>
    public static class Loc
    {
        private static DependencyProperty Make(string name, string target) =>
            DependencyProperty.RegisterAttached(name, typeof(string), typeof(Loc),
                new PropertyMetadata(null, (d, e) => Apply(d, target, e.NewValue as string)));

        public static readonly DependencyProperty ItTextProperty = Make("ItText", "Text");
        public static readonly DependencyProperty ItContentProperty = Make("ItContent", "Content");
        public static readonly DependencyProperty ItHeaderProperty = Make("ItHeader", "Header");
        public static readonly DependencyProperty ItPlaceholderProperty = Make("ItPlaceholder", "PlaceholderText");
        public static readonly DependencyProperty ItOnContentProperty = Make("ItOnContent", "OnContent");
        public static readonly DependencyProperty ItOffContentProperty = Make("ItOffContent", "OffContent");
        public static readonly DependencyProperty ItTitleProperty = Make("ItTitle", "Title");
        public static readonly DependencyProperty ItToolTipProperty = Make("ItToolTip", "ToolTip");
        public static readonly DependencyProperty ItNameProperty = Make("ItName", "AutomationName");
        public static readonly DependencyProperty ItHelpTextProperty = Make("ItHelpText", "AutomationHelpText");

        public static string? GetItText(DependencyObject d) => (string?)d.GetValue(ItTextProperty);
        public static void SetItText(DependencyObject d, string? v) => d.SetValue(ItTextProperty, v);
        public static string? GetItContent(DependencyObject d) => (string?)d.GetValue(ItContentProperty);
        public static void SetItContent(DependencyObject d, string? v) => d.SetValue(ItContentProperty, v);
        public static string? GetItHeader(DependencyObject d) => (string?)d.GetValue(ItHeaderProperty);
        public static void SetItHeader(DependencyObject d, string? v) => d.SetValue(ItHeaderProperty, v);
        public static string? GetItPlaceholder(DependencyObject d) => (string?)d.GetValue(ItPlaceholderProperty);
        public static void SetItPlaceholder(DependencyObject d, string? v) => d.SetValue(ItPlaceholderProperty, v);
        public static string? GetItOnContent(DependencyObject d) => (string?)d.GetValue(ItOnContentProperty);
        public static void SetItOnContent(DependencyObject d, string? v) => d.SetValue(ItOnContentProperty, v);
        public static string? GetItOffContent(DependencyObject d) => (string?)d.GetValue(ItOffContentProperty);
        public static void SetItOffContent(DependencyObject d, string? v) => d.SetValue(ItOffContentProperty, v);
        public static string? GetItTitle(DependencyObject d) => (string?)d.GetValue(ItTitleProperty);
        public static void SetItTitle(DependencyObject d, string? v) => d.SetValue(ItTitleProperty, v);
        public static string? GetItToolTip(DependencyObject d) => (string?)d.GetValue(ItToolTipProperty);
        public static void SetItToolTip(DependencyObject d, string? v) => d.SetValue(ItToolTipProperty, v);
        public static string? GetItName(DependencyObject d) => (string?)d.GetValue(ItNameProperty);
        public static void SetItName(DependencyObject d, string? v) => d.SetValue(ItNameProperty, v);
        public static string? GetItHelpText(DependencyObject d) => (string?)d.GetValue(ItHelpTextProperty);
        public static void SetItHelpText(DependencyObject d, string? v) => d.SetValue(ItHelpTextProperty, v);

        private static void Apply(DependencyObject d, string target, string? italian)
        {
            if (!UiText.IsItalian || string.IsNullOrWhiteSpace(italian)) return;
            switch (target)
            {
                case "ToolTip": ToolTipService.SetToolTip(d, italian); return;
                case "AutomationName": AutomationProperties.SetName(d, italian); return;
                case "AutomationHelpText": AutomationProperties.SetHelpText(d, italian); return;
            }
            var dp = d.GetType().GetProperty(target + "Property",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) as DependencyProperty;
            if (dp != null) d.SetValue(dp, italian);
        }
    }
}
