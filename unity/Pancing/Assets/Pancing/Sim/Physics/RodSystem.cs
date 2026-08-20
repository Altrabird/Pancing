using System;

namespace Pancing.Sim
{
    public enum TensionZone { Slack, Good, High, Danger }

    /// <summary>
    /// Rod, line and drag physics — a port of web/src/physics/rod.js.
    ///
    /// THE MODEL
    /// ---------
    /// Rod tip and line are two springs in series between the reel and the fish:
    ///
    ///      reel --[ drag clutch ]-- rod blank --tip--[ line ]-- fish
    ///
    /// The line is close to linear: it reaches its rated breaking force exactly
    /// when its strain reaches the material's rated stretch, giving a spring whose
    /// constant falls off with length, matching k = EA/L:
    ///
    ///      T_line(e) = testN * e / (stretch * lineOut)
    ///
    /// The rod is deliberately NOT linear. A real blank has a progressive taper —
    /// soft at the tip, stiff at the butt — so the harder you load it the less
    /// extra give you get. Modelled as a saturating exponential whose asymptote is
    /// the rod's maximum deflection:
    ///
    ///      e_rod(T) = maxDeflect * (1 - exp(-T / power))
    ///
    /// That single curve is what makes a rod feel like a rod. It is the shock
    /// absorber: a sudden lunge is soaked up by tip deflection before it ever
    /// reaches the line, which is why a soft rod protects light line and a
    /// broomstick snaps it.
    ///
    /// SOLVING IT
    /// ----------
    /// Both springs carry the same tension T and their extensions must sum to the
    /// overshoot between where the fish is and how much line is out:
    ///
    ///      e_line(T) + e_rod(T) = fishDist - lineOut
    ///
    /// The left side is strictly increasing in T, so instead of integrating a
    /// stiff ODE — which explodes with near-zero-stretch braid — we bisect for T
    /// directly. Unconditionally stable at any timestep, ~20 float ops.
    ///
    /// THE DRAG
    /// --------
    /// Above the clutch setting the spool slips and pays out line, which lengthens
    /// LineOut, which drops the strain, which drops T back toward the setting. The
    /// feedback loop IS the drag. Nothing else needs to enforce the ceiling.
    ///
    /// Everything here is double precision, and there is no UnityEngine reference
    /// anywhere in this assembly (see Pancing.Sim.asmdef, noEngineReferences).
    /// That is deliberate: it keeps the simulation headless exactly as the JS
    /// build is, and it is what lets shared/parity compile this file outside Unity
    /// and diff it against the JavaScript tick for tick.
    /// </summary>
    public sealed class RodSystem
    {
        public const double G = 9.81;

        /* --- tension bands, normalised against the line's breaking force ---- */

        public const double ZoneSlack = 0.08;
        public const double ZoneGood = 0.55;
        public const double ZoneHigh = 0.85;

        public static TensionZone ZoneFor(double loadFrac)
        {
            if (loadFrac < ZoneSlack) return TensionZone.Slack;
            if (loadFrac < ZoneGood) return TensionZone.Good;
            if (loadFrac < ZoneHigh) return TensionZone.High;
            return TensionZone.Danger;
        }

        /* --- tuning constants ---------------------------------------------- */

        /// <summary>Rod tip travel as a fraction of blank length at full bend.</summary>
        public const double MaxDeflectRatio = 0.34;
        /// <summary>Bisection bracket, as a multiple of breaking force.</summary>
        public const double SolveCeil = 3.0;
        public const int SolveIters = 22;
        /// <summary>How fast the spool gives line per newton of overload, m/s/N.</summary>
        public const double SlipGain = 0.055;
        /// <summary>A rough clutch grabs and lets go; a smooth one bleeds evenly.</summary>
        public const double SlipJudder = 0.55;
        /// <summary>Line wear starts here (fraction of breaking force).</summary>
        public const double WearFrom = 0.62;
        public const double WearRate = 0.55;
        /// <summary>Abrasion multiplier while the line is dragging across structure.</summary>
        public const double SnagWear = 3.4;
        /// <summary>Hook works loose on a slack line.</summary>
        public const double HookSlackRate = 0.20;
        /// <summary>Hook tears out under sustained overload.</summary>
        public const double HookTearFrom = 0.78;
        public const double HookTearRate = 0.42;
        /// <summary>Shape and depth of the retrieve penalty under load (never reaches
        /// zero; a slipping clutch is what actually stops you gaining line).</summary>
        public const double ReelStallExp = 2.0;
        public const double ReelLoadPenalty = 0.6;
        /// <summary>Minimum line out; you cannot reel the fish into the rod tip.</summary>
        public const double MinLineOut = 0.45;
        /// <summary>Landing distance.</summary>
        public const double LandDist = 1.1;

        /* --- configured gear ------------------------------------------------ */

        private RodSpec _rod;
        private ReelSpec _reel;
        private LineSpec _line;

        private double _power;        // N for full bend
        private double _maxDeflect;   // m of tip travel
        private double _testN;        // breaking force, N
        private double _stretch;
        private double _retrieveBase;
        private double _dragMax;
        private double _dragSmooth;
        private double _dragCeil;

        /* --- live state ----------------------------------------------------- */

        public double LineOut;
        public double Tension;
        public double SmoothTension;
        public double Bend;
        public double TipGive;
        public double SlipRate;
        public double LineIntegrity = 1.0;
        public double HookHold = 1.0;
        public double DragFrac = 0.55;
        public double Drag;
        public TensionZone Zone = TensionZone.Slack;
        public double LoadFrac;
        public bool Broke;
        public double ReelGain;
        public bool Slipping;
        public double PeakTension;
        private double _judder;

        public RodSystem() { Reset(); }

        public void Configure(GearSet gear)
        {
            _rod = gear.Rod;
            _reel = gear.Reel;
            _line = gear.Line;

            _power = _rod.Power;
            _maxDeflect = _rod.Length * MaxDeflectRatio;
            _testN = _line.Test * G;
            _stretch = Math.Max(_line.Stretch, 0.005);
            _retrieveBase = _reel.Retrieve;
            _dragMax = _reel.Drag;
            _dragSmooth = _reel.DragSmooth;

            // The clutch is NOT clamped to the line's strength. A reel that can
            // apply more drag than the line can take is exactly how anglers break
            // off, and taking that away would remove the central risk decision:
            // winding the drag up beats the fish faster and gets you closer to a
            // snap. The UI warns via DragUnsafe instead of the model quietly
            // protecting the player.
            _dragCeil = _dragMax;
            SetDragFrac(DragFrac);
        }

        public void Reset()
        {
            LineOut = 0;
            Tension = 0;
            SmoothTension = 0;
            Bend = 0;
            TipGive = 0;
            SlipRate = 0;
            LineIntegrity = 1;
            HookHold = 1;
            Drag = 0;
            Zone = TensionZone.Slack;
            LoadFrac = 0;
            Broke = false;
            ReelGain = 0;
            Slipping = false;
            _judder = 0;
            PeakTension = 0;
        }

        /// <summary>Fresh line and a fresh hook, e.g. after a break-off or a landed fish.</summary>
        public void Respool(double lineOut = 0)
        {
            LineOut = lineOut;
            Tension = 0;
            SmoothTension = 0;
            LineIntegrity = 1;
            HookHold = 1;
            Broke = false;
            PeakTension = 0;
            SlipRate = 0;
            Bend = 0;
        }

        public void SetDragFrac(double f)
        {
            DragFrac = MathUtil.Clamp01(f);
            Drag = _dragCeil != 0 ? 0.06 * _dragCeil + DragFrac * 0.94 * _dragCeil : 0;
        }

        public void AdjustDrag(double delta) => SetDragFrac(DragFrac + delta);

        /* --- the solver ------------------------------------------------------ */

        /// <summary>Line extension at tension T (metres).</summary>
        public double LineExtension(double t)
            => (t * _stretch * Math.Max(LineOut, MinLineOut)) / _testN;

        /// <summary>Rod tip deflection at tension T (metres). Saturating: progressive taper.</summary>
        public double RodDeflection(double t)
            => _maxDeflect * (1.0 - Math.Exp(-t / _power));

        /// <summary>
        /// Bisect for the tension that makes the two series springs absorb exactly
        /// `overshoot` metres. Monotonic, so bisection always converges.
        /// </summary>
        public double SolveTension(double overshoot)
        {
            if (overshoot <= 0) return 0;
            double lo = 0;
            double hi = _testN * SolveCeil;
            // If even the ceiling cannot absorb the overshoot the line is past
            // breaking anyway; returning the ceiling lets the damage pass handle it.
            for (int i = 0; i < SolveIters; i++)
            {
                double mid = 0.5 * (lo + hi);
                double e = LineExtension(mid) + RodDeflection(mid);
                if (e < overshoot) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>Inputs to one simulation step.</summary>
        public struct Ctx
        {
            /// <summary>Metres from rod tip to hook.</summary>
            public double FishDist;
            /// <summary>0..1 requested retrieve.</summary>
            public double ReelInput;
            /// <summary>Line is currently rubbing on a snag.</summary>
            public bool OnStructure;
            /// <summary>Additional steady pull, e.g. current or dead weight.</summary>
            public double ExtraLoad;
            public bool AllowSlip;
            /// <summary>
            /// Skip the hook-hold damage pass. Set while a lure is soaking with
            /// nothing on it: a slack line works a hook loose only when the hook is
            /// in a fish. Without this the hook-hold readout drained to critical
            /// while the player was just waiting for a bite.
            ///
            /// Defaults false so every existing caller — including the parity
            /// harness — keeps the original behaviour.
            /// </summary>
            public bool SuppressHookWear;

            public static Ctx Default => new Ctx { AllowSlip = true };
        }

        /// <summary>Per-step report the game layer reacts to.</summary>
        public struct Step
        {
            public double Tension;
            public double LoadFrac;
            public TensionZone Zone;
            public double Bend;
            public double LineOut;
            public bool Slipping;
            public double SlipRate;
            public bool Snapped;
            public bool HookLost;
            public bool Landed;
            public bool OverloadedRod;
        }

        public Step Update(double dt, Ctx ctx)
        {
            double fishDist = ctx.FishDist;

            if (LineOut <= 0) LineOut = Math.Max(fishDist, MinLineOut);

            // 1. Reel in. Two separate things are going on and conflating them is
            //    wrong: a loaded reel winds *slower* (the gearbox is fighting the
            //    fish), but a reel whose clutch is actually slipping wins *nothing*
            //    — line leaves the spool as fast as the handle puts it back. So the
            //    handle stays useful right up to the slip point, and goes dead the
            //    instant the drag gives.
            double load = Drag > 0 ? MathUtil.Clamp01(Tension / Drag) : 0;
            double efficiency = Slipping
                ? 0
                : MathUtil.Clamp(1 - ReelLoadPenalty * Math.Pow(load, ReelStallExp), 0.22, 1);
            ReelGain = ctx.ReelInput * _retrieveBase * efficiency;
            if (ReelGain > 0)
            {
                LineOut = Math.Max(MinLineOut, LineOut - ReelGain * dt);
            }

            // 2. Solve the series springs for the tension the geometry demands.
            double overshoot = fishDist - LineOut;
            double T = SolveTension(overshoot) + ctx.ExtraLoad;

            // 3. Drag clutch. Anything above the setting slips line off the spool;
            //    the resulting longer LineOut is what actually relieves the tension,
            //    so the ceiling is enforced by the feedback loop, not by a clamp.
            Slipping = false;
            SlipRate = 0;
            if (ctx.AllowSlip && T > Drag && Drag > 0)
            {
                double excess = T - Drag;
                // A rough clutch stutters; jitter is deterministic in tension so it
                // reads as texture in the rumble/needle rather than as noise.
                _judder += dt * (6 + excess * 0.4);
                double judder = 1 + (1 - _dragSmooth) * SlipJudder * Math.Sin(_judder * 9.0);
                SlipRate = excess * SlipGain * judder;
                LineOut += SlipRate * dt;
                Slipping = true;
                // Re-solve after paying out: the tension the player feels this frame
                // is the post-slip one, which is what a real drag delivers.
                T = SolveTension(fishDist - LineOut) + ctx.ExtraLoad;
            }

            Tension = T;
            PeakTension = Math.Max(PeakTension, T);
            SmoothTension = MathUtil.Damp(SmoothTension, T, 14, dt);
            LoadFrac = _testN > 0 ? T / _testN : 0;
            Zone = ZoneFor(LoadFrac);

            // 4. Rod bend, for the renderer and for the feel of the tension meter.
            TipGive = RodDeflection(T);
            Bend = _maxDeflect > 0 ? TipGive / _maxDeflect : 0;

            // 5. Damage. Line wear is superlinear past the wear threshold, so
            //    sitting at 0.9 of breaking strain kills you much faster than 0.7.
            bool snapped = false;
            if (LoadFrac > WearFrom)
            {
                double over = (LoadFrac - WearFrom) / (1 - WearFrom);
                double abrasion = ctx.OnStructure ? SnagWear * (1 - _line.Abrasion * 0.6) : 1;
                LineIntegrity -= over * over * WearRate * abrasion * dt;
            }
            else if (ctx.OnStructure)
            {
                LineIntegrity -= 0.18 * (1 - _line.Abrasion * 0.6) * dt;
            }
            LineIntegrity = MathUtil.Clamp01(LineIntegrity);

            if (LoadFrac >= 1 || LineIntegrity <= 0)
            {
                snapped = true;
                Broke = true;
            }

            // 6. Hook hold. Two ways to lose a fish that is still attached: let the
            //    line go slack so the hook backs out, or bury the rod and tear the
            //    hole open.
            double hookWear = 0;
            if (ctx.SuppressHookWear)
            {
                // Nothing on the end; leave the hook untouched.
            }
            else if (LoadFrac < ZoneSlack)
            {
                hookWear += HookSlackRate * (1 - LoadFrac / ZoneSlack);
            }
            if (!ctx.SuppressHookWear && LoadFrac > HookTearFrom)
            {
                hookWear += HookTearRate * (LoadFrac - HookTearFrom) / (1 - HookTearFrom);
            }
            if (hookWear > 0) HookHold = MathUtil.Clamp01(HookHold - hookWear * dt);

            return new Step
            {
                Tension = Tension,
                LoadFrac = LoadFrac,
                Zone = Zone,
                Bend = Bend,
                LineOut = LineOut,
                Slipping = Slipping,
                SlipRate = SlipRate,
                Snapped = snapped,
                HookLost = HookHold <= 0,
                Landed = LineOut <= LandDist && fishDist <= LandDist * 1.2,
                OverloadedRod = Tension > _power * 2.4,
            };
        }

        /// <summary>Extra hook damage from a discrete event (a head-shake, a jump, a snag hit).</summary>
        public void ShockHook(double amount) => HookHold = MathUtil.Clamp01(HookHold - amount);

        /// <summary>Damage the line directly, e.g. a rock strike during a run.</summary>
        public void Abrade(double amount) => LineIntegrity = MathUtil.Clamp01(LineIntegrity - amount);

        /// <summary>Snapshot for the HUD, already normalised so the UI does no maths.</summary>
        public struct Telemetry
        {
            public double Tension, Smooth, LoadFrac, DragFrac, DragN, DragLoad;
            /// <summary>Clutch is set harder than the line can take: a surge will break you.</summary>
            public bool DragUnsafe;
            public double DragVsLine, Bend, LineOut, LineIntegrity, HookHold, SlipRate, TestN, Peak;
            public TensionZone Zone;
            public bool Slipping;
        }

        public Telemetry GetTelemetry() => new Telemetry
        {
            Tension = Tension,
            Smooth = SmoothTension,
            LoadFrac = LoadFrac,
            DragFrac = DragFrac,
            DragN = Drag,
            DragLoad = Drag > 0 ? MathUtil.Clamp01(Tension / Drag) : 0,
            DragUnsafe = Drag > _testN * 0.8,
            DragVsLine = _testN > 0 ? Drag / _testN : 0,
            Zone = Zone,
            Bend = Bend,
            LineOut = LineOut,
            LineIntegrity = LineIntegrity,
            HookHold = HookHold,
            Slipping = Slipping,
            SlipRate = SlipRate,
            TestN = _testN,
            Peak = PeakTension,
        };
    }
}
