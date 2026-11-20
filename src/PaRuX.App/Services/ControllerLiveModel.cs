using System;

namespace PaRuX.Services
{
    /// <summary>Pure helpers behind the live controller view: which control just moved, and where a stick dot sits. No UI, no device access.</summary>
    public static class ControllerLiveModel
    {
        /// <summary>
        /// Names the control that changed between two readings ("button 3", "axis 2") so the owner can find the index to type into
        /// the mapping boxes. Buttons win over axes; an axis must move by at least <paramref name="axisDelta"/> to count (noise is ignored).
        /// Returns null when nothing changed.
        /// </summary>
        public static string? DescribeChange(bool[]? previousButtons, double[]? previousAxes, bool[]? buttons, double[]? axes, double axisDelta = 0.35)
        {
            if (buttons is not null)
                for (int i = 0; i < buttons.Length; i++)
                {
                    bool before = previousButtons is not null && i < previousButtons.Length && previousButtons[i];
                    if (buttons[i] && !before) return "button " + i;
                }
            if (axes is not null && previousAxes is not null)
                for (int i = 0; i < axes.Length && i < previousAxes.Length; i++)
                    if (Math.Abs(axes[i] - previousAxes[i]) >= axisDelta) return "axis " + i;
            return null;
        }

        /// <summary>
        /// Guided mapping ("press the control you want to use"): the index of the button that is newly pressed since the baseline, or
        /// of the axis that moved furthest (at least <paramref name="axisDelta"/>) since the baseline. Null while nothing qualifies.
        /// Comparing with a baseline taken when the step began ignores a stick that rests off-centre.
        /// </summary>
        public static int? DetectIndex(bool[]? baselineButtons, double[]? baselineAxes, bool[]? buttons, double[]? axes, bool wantButton, double axisDelta = 0.5)
        {
            if (wantButton)
            {
                if (buttons is null) return null;
                for (int i = 0; i < buttons.Length; i++)
                    if (buttons[i] && !(baselineButtons is not null && i < baselineButtons.Length && baselineButtons[i])) return i;
                return null;
            }
            if (axes is null) return null;
            int? best = null;
            double bestDelta = axisDelta;
            for (int i = 0; i < axes.Length; i++)
            {
                double before = baselineAxes is not null && i < baselineAxes.Length ? baselineAxes[i] : 0;
                double delta = Math.Abs(axes[i] - before);
                if (delta >= bestDelta) { best = i; bestDelta = delta; }
            }
            return best;
        }

        /// <summary>Stick position for the axis pair starting at <paramref name="firstAxis"/>, each clamped to -1..1; (0,0) when the axes do not exist.</summary>
        public static (double X, double Y) StickPoint(double[]? axes, int firstAxis)
        {
            if (axes is null || firstAxis < 0 || firstAxis + 1 >= axes.Length) return (0, 0);
            static double Clamp(double v) => double.IsFinite(v) ? Math.Clamp(v, -1, 1) : 0;
            return (Clamp(axes[firstAxis]), Clamp(axes[firstAxis + 1]));
        }
    }
}
