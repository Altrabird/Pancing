using System;

namespace Pancing.Sim
{
    /// <summary>
    /// The handful of scalar helpers the simulation shares, ported from the tail
    /// of web/src/core/loop.js. Deliberately System.Math rather than UnityEngine
    /// .Mathf: Mathf is single-precision, and the tension solver's parity with
    /// the JavaScript build depends on staying in doubles throughout.
    /// </summary>
    public static class MathUtil
    {
        public static double Lerp(double a, double b, double t) => a + (b - a) * t;

        public static double Clamp(double x, double lo, double hi) => x < lo ? lo : (x > hi ? hi : x);

        public static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);

        public static double SmoothStep(double a, double b, double x)
        {
            double t = Clamp01((x - a) / (b - a));
            return t * t * (3 - 2 * t);
        }

        /// <summary>Frame-rate independent exponential approach. `rate` is per second.</summary>
        public static double Damp(double current, double target, double rate, double dt)
            => target + (current - target) * Math.Exp(-rate * dt);

        public static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

        /// <summary>
        /// JavaScript's Math.round: ties go toward +Infinity.
        ///
        /// System.Math.Round uses banker's rounding — Round(2.5) is 2, not 3 — so
        /// using it would quietly disagree with the reference build on every value
        /// that lands exactly on a half. That is not hypothetical: catch weights
        /// are rounded to the gram and sale prices to the ringgit, and a fish
        /// worth RM 6.5 would sell for a different amount in each engine.
        /// </summary>
        public static double JsRound(double x) => Math.Floor(x + 0.5);

        /// <summary>JsRound to a fixed number of decimals, e.g. Round(x, 10) for 0.1 steps.</summary>
        public static double RoundTo(double x, double scale) => JsRound(x * scale) / scale;
    }
}
