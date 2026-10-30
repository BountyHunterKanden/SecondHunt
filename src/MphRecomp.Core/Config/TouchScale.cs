using System;
using System.Globalization;

namespace MphRecomp.Config
{
    // The touch controls' stylus aim speed (RecompSettings.TouchSens; TOUCH AIM: STYLUS, Input/StylusAim.cs), by the
    // finger's physical travel: 1.0 turns the view 10 degrees of yaw a centimetre of drag on any screen (pitch at the
    // DS's own ratio to yaw, 1138 / 2211). Since 10-24: the old rule rode
    // the controller's 1-15 sensitivity and its slowest (level 1) was 54 degrees a cm, faster than Fruity Prime's
    // fastest touch aim (its slider 0.01-3x on dp: 0.16 / 15.7 / 47 degrees a cm at min / default / max). This range
    // starts at 0.01 (0.1 degree a cm, slower than theirs), default 2.0 (20 a cm, near their default), and goes on to
    // 18 (the old fastest). A save from before has no value (0): Load maps its level onto this range (FromLevel).
    public static class TouchScale
    {
        public const float DegreesPerCm = 10f;
        public const float Default = 2f;
        public const float Min = 0.01f, Max = 18f;

        // 0.01, 0.02, 0.05, 0.1 - 3 by 0.1, by 0.5 to 10, by 1 to 18: 51 steps
        public static readonly float[] Steps = BuildSteps();

        private static float[] BuildSteps()
        {
            var steps = new System.Collections.Generic.List<float> { 0.01f, 0.02f, 0.05f };
            for (int i = 1; i <= 30; i++) steps.Add(i / 10f);
            for (int i = 7; i <= 20; i++) steps.Add(i / 2f);
            for (int i = 11; i <= 18; i++) steps.Add(i);
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

        // "2", "0.05", "10"
        public static string Text(float value) => Steps[Index(value)].ToString("0.##", CultureInfo.InvariantCulture);

        // an old save's controller level 1-15 (8 = the default) onto this range, keeping its place: the default to the
        // default, each level 1.2x the one below (1 -> 0.6, 15 -> 7), snapped to a step
        public static float FromLevel(int level) => Steps[Index(Default * MathF.Pow(1.2f, Math.Clamp(level, 1, 15) - 8))];

        // the speed a settings object asks for (an unset one from its controller level)
        public static float Of(RecompSettings s) => s.TouchSens > 0 ? Clamp(s.TouchSens) : FromLevel(s.GameSensitivity);
    }
}
