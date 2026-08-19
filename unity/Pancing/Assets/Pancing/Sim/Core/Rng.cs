using System;
using System.Collections.Generic;

namespace Pancing.Sim
{
    /// <summary>
    /// Seeded deterministic RNG — a bit-exact port of web/src/core/rng.js
    /// (sfc32 seeded through splitmix32, with an FNV-1a string hasher).
    ///
    /// PARITY CONTRACT
    /// ---------------
    /// The integer stream this produces must match the JavaScript build bit for
    /// bit, for every seed. That is what lets one seed describe the same session
    /// in both engines and lets the balance numbers from tools/simulate.mjs
    /// still mean something here.
    ///
    /// JavaScript does its 32-bit integer maths through Math.imul, `|0` and
    /// `>>> 0`. All three are two's-complement operations on 32 bits, so plain
    /// wrapping `uint` arithmetic in C# reproduces them exactly — `|0` and
    /// `>>> 0` differ only in how the same 32 bits are *interpreted*, and we
    /// only ever interpret at the very end, in Next().
    ///
    /// Transcendentals (Normal() uses Log/Sqrt/Cos) are IEEE-754 doubles and may
    /// differ from V8 in the last ULP. Parity tests compare the raw stream
    /// exactly and derived floats with a tolerance. See shared/parity/.
    /// </summary>
    public sealed class Rng
    {
        private uint _a, _b, _c, _d;

        public uint Seed { get; private set; }

        public Rng(uint seed)
        {
            Init(seed);
        }

        public Rng(string seed)
        {
            Init(HashSeed(seed));
        }

        private void Init(uint s)
        {
            // Four decorrelated words from one seed, via splitmix32.
            uint x = s;
            _a = SplitMix32(ref x);
            _b = SplitMix32(ref x);
            _c = SplitMix32(ref x);
            _d = SplitMix32(ref x);
            Seed = s;
        }

        private static uint SplitMix32(ref uint x)
        {
            unchecked
            {
                x += 0x9e3779b9u;
                uint z = x;
                z = (z ^ (z >> 16)) * 0x21f0aaadu;
                z = (z ^ (z >> 15)) * 0x735a2d97u;
                return z ^ (z >> 15);
            }
        }

        /// <summary>FNV-1a over the UTF-16 code units, matching charCodeAt.</summary>
        public static uint HashSeed(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619u;
                }
                return h;
            }
        }

        /// <summary>Uniform in [0, 1).</summary>
        public double Next()
        {
            unchecked
            {
                uint t = _a + _b + _d;
                _d = _d + 1u;
                _a = _b ^ (_b >> 9);
                _b = _c + (_c << 3);
                _c = (_c << 21) | (_c >> 11);
                _c = _c + t;
                return t / 4294967296.0;
            }
        }

        /// <summary>The raw 32-bit word behind Next(), for parity testing.</summary>
        public uint NextWord()
        {
            unchecked
            {
                uint t = _a + _b + _d;
                _d = _d + 1u;
                _a = _b ^ (_b >> 9);
                _b = _c + (_c << 3);
                _c = (_c << 21) | (_c >> 11);
                _c = _c + t;
                return t;
            }
        }

        public double Float(double min = 0.0, double max = 1.0) => min + Next() * (max - min);

        public int Int(int min, int max) => (int)Math.Floor(Float(min, max + 1));

        public bool Bool(double p = 0.5) => Next() < p;

        public T Pick<T>(IReadOnlyList<T> arr) => arr[(int)Math.Floor(Next() * arr.Count)];

        /// <summary>
        /// Box-Muller, one value per call. The spare is deliberately discarded so
        /// the stream advances by a fixed amount and stays replay-stable.
        /// </summary>
        public double Normal(double mean = 0.0, double sd = 1.0)
        {
            double u = 0.0;
            while (u == 0.0) u = Next();
            double v = Next();
            return mean + sd * Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v);
        }

        /// <summary>Normal clipped to [min, max] by resampling, with a bailout to clamping.</summary>
        public double NormalClamped(double mean, double sd, double min, double max, int tries = 12)
        {
            for (int i = 0; i < tries; i++)
            {
                double x = Normal(mean, sd);
                if (x >= min && x <= max) return x;
            }
            return Math.Min(max, Math.Max(min, Normal(mean, sd)));
        }

        /// <summary>Weighted pick over (key, weight) pairs. Returns default if all weights &lt;= 0.</summary>
        public TKey Weighted<TKey>(IReadOnlyList<KeyValuePair<TKey, double>> entries)
        {
            double total = 0.0;
            foreach (var e in entries) if (e.Value > 0) total += e.Value;
            if (total <= 0) return default;

            double roll = Next() * total;
            foreach (var e in entries)
            {
                if (e.Value <= 0) continue;
                roll -= e.Value;
                if (roll <= 0) return e.Key;
            }
            return entries[entries.Count - 1].Key;
        }

        /// <summary>
        /// Weighted pick returning the winning INDEX, or -1 when every weight is
        /// zero or negative.
        ///
        /// The index form exists because the value form cannot distinguish "picked
        /// the first entry" from "picked nothing" for value types — an enum's
        /// default is a real member of the enum, so `Weighted(...) ?? Fallback`
        /// silently becomes "always the zeroth state". The fight AI needs that
        /// distinction, and it needs the caller's ordering preserved.
        /// </summary>
        public int WeightedIndex(IReadOnlyList<double> weights)
        {
            double total = 0.0;
            for (int i = 0; i < weights.Count; i++) if (weights[i] > 0) total += weights[i];
            if (total <= 0) return -1;

            double roll = Next() * total;
            for (int i = 0; i < weights.Count; i++)
            {
                if (weights[i] <= 0) continue;
                roll -= weights[i];
                if (roll <= 0) return i;
            }
            return weights.Count - 1;
        }

        /// <summary>Independent sub-stream, for isolating systems from each other's draws.</summary>
        public Rng Fork(string label) => new Rng(HashSeed($"{Seed}:{label}"));
    }
}
