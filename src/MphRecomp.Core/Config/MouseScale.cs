using System;
using System.Globalization;

namespace MphRecomp.Config
{
    // A PC mouse's aim speed (RecompSettings.MouseSens), on the scale most PC shooters share: the Source engine's
    // (Counter-Strike's m_yaw / m_pitch = 0.022), where one mouse count turns the view 0.022 degrees x the value, so a
    // value carries over from those games as is (same DPI). Default 2.5 = Counter-Strike's own. Every place that sets
    // it (OPTIONS > CONTROLS' KEYBOARD & MOUSE row, the gunship's OPTIONS slider on its KEYBOARD & MOUSE tab) steps
    // through the same Steps, so both menus offer exactly the same range.
    public static class MouseScale
    {
        public const float DegreesPerCount = 0.022f;
        public const float Default = 2.5f;
        public const float Min = 0.1f, Max = 20f;

        // 0.1 - 3 by 0.1 (where most players sit), by 0.5 to 10, by 1 to 20 (20 = 0.44 degrees a count, past the old
        // 1-15 scale's fastest, 0.36): 54 steps, none longer than 3 characters (the gunship slider's grab tab holds three)
        public static readonly float[] Steps = BuildSteps();

        private static float[] BuildSteps()
        {
            var steps = new System.Collections.Generic.List<float>();
            for (int i = 1; i <= 30; i++) steps.Add(i / 10f);
            for (int i = 7; i <= 20; i++) steps.Add(i / 2f);
            for (int i = 11; i <= 20; i++) steps.Add(i);
            return steps.ToArray();
        }

        // the step nearest a value (a hand-edited settings file may hold any number)
        public static int Index(float value)
        {
            int best = 0;
            for (int i = 1; i < Steps.Length; i++)
            {
                if (MathF.Abs(Steps[i] - value) < MathF.Abs(Steps[best] - value)) best = i;
            }
            return best;
        }

        public static float Step(float value, int delta) => Steps[Math.Clamp(Index(value) + delta, 0, Steps.Length - 1)];

        public static float Clamp(float value) => float.IsFinite(value) ? Math.Clamp(value, Min, Max) : Default;

        // "2.5", "0.1", "10"
        public static string Text(float value) => Steps[Index(value)].ToString("0.##", CultureInfo.InvariantCulture);

        // MphRead turns the view by (mouse units / 4) degrees (PlayerInput ProcessBiped / ProcessAlt), so this many of
        // its units per mouse count gives DegreesPerCount x value degrees
        public static float UnitsPerCount(float value) => Clamp(value) * DegreesPerCount * 4;
    }
}
