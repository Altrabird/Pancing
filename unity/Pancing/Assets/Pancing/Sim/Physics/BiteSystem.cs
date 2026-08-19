using System;

namespace Pancing.Sim
{
    public enum BiteState { Idle, Searching, Interest, Nibbling, Committed, Hooked, Spooked }

    public enum BiteEventType { None, Interest, Nibble, Committing, Bite, Hooked, Missed, Spooked, Whiff }

    public struct BiteEvent
    {
        public BiteEventType Type;
        public Species Species;
        public string Reason;
        public double Window;
        public double Quality;
        public double Timing;
        public int Remaining;

        public bool Any => Type != BiteEventType.None;
    }

    /// <summary>How good the presentation is, factor by factor. All multiplicative.</summary>
    public struct Presentation
    {
        public double LureMatch, DepthMatch, ActionMatch, Stealth, Total;
    }

    /// <summary>
    /// Bite detection — a port of web/src/physics/bite.js.
    ///
    /// A bite is not a coin flip on a timer. It is an attraction budget that fills
    /// or empties every tick based on whether what the player is doing matches
    /// what the fish that is actually down there wants:
    ///
    ///   SEARCHING  no candidate. Attraction accumulates from presentation quality.
    ///   INTEREST   a specific fish has been drawn from the catch table and is
    ///              inspecting. Bad presentation now loses it.
    ///   NIBBLING   discrete taps. The float twitches. Striking here is too early.
    ///   COMMITTED  the hookset window. Species-dependent, 320 ms for a Toman up
    ///              to 1.4 s for a prawn. Strike quality is how centred you were.
    ///   SPOOKED    cooldown; that fish is gone and the swim is quiet for a while.
    ///
    /// Presentation is scored from four independent factors, all multiplicative so
    /// a single bad choice can kill a bite outright.
    ///
    /// PARITY NOTE: the order and count of RNG draws in here is part of the
    /// contract with the JavaScript reference build, not an implementation detail.
    /// Where a draw looks redundant it is marked — do not "clean it up" without
    /// re-running shared/parity, because removing a draw shifts every subsequent
    /// number in the stream.
    /// </summary>
    public sealed class BiteSystem
    {
        /* --- tuning ---------------------------------------------------------- */
        //
        // Pacing. Measured against a good presentation these give roughly 4 s to
        // interest and 5–6 s more to the first nibble — about a 10 s bite cycle,
        // which leaves real waiting in the game without it becoming a screensaver.
        // A poor presentation scales both directly, so a wrong lure can stretch
        // the same cycle past a minute.

        /// <summary>Attraction needed before a candidate fish is drawn.</summary>
        public const double InterestThreshold = 1.6;
        /// <summary>Attraction needed to move from inspecting to mouthing the bait.</summary>
        public const double CommitThreshold = 3.2;
        /// <summary>Base fill rate; species Bite.Speed scales it.</summary>
        public const double FillRate = 0.62;
        /// <summary>Decay when presentation is poor.</summary>
        public const double DecayRate = 0.55;
        /// <summary>How long a spooked swim stays quiet, seconds.</summary>
        public const double SpookCooldownMin = 3.5, SpookCooldownMax = 7.0;
        /// <summary>Gap between individual nibble taps.</summary>
        public const double NibbleGapMin = 0.35, NibbleGapMax = 1.10;
        /// <summary>A tap lasts this long visually.</summary>
        public const double TapDuration = 0.16;
        /// <summary>Striking during a tap does not just miss — it spooks the fish.</summary>
        public const double EarlyStrikeSpook = 0.75;

        private readonly Rng _rng;
        private readonly double _lureMismatch;

        public BiteState State = BiteState.Idle;
        public double Attraction;
        public Species Candidate;
        public double Timer;
        public int NibblesLeft;
        public double NextTap;
        public double TapTimer;
        public bool Tapping;
        public double WindowLeft;
        public double WindowTotal;
        public double Cooldown;
        public double PresentationSmoothed;
        public Presentation LastScore;
        public double TimeSinceCast;
        public double StrikeQuality;

        public BiteSystem(Rng rng, double lureMismatch)
        {
            _rng = rng;
            _lureMismatch = lureMismatch;
            Reset();
        }

        public void Reset()
        {
            State = BiteState.Idle;
            Attraction = 0;
            Candidate = null;
            Timer = 0;
            NibblesLeft = 0;
            NextTap = 0;
            TapTimer = 0;
            Tapping = false;
            WindowLeft = 0;
            WindowTotal = 0;
            Cooldown = 0;
            PresentationSmoothed = 0;
            LastScore = default;
            StrikeQuality = 0;
            TimeSinceCast = 0;
        }

        /// <summary>Called when the lure settles; the swim starts paying attention.</summary>
        public void Begin()
        {
            if (State == BiteState.Spooked && Cooldown > 0) return;
            State = BiteState.Searching;
            Attraction = 0;
            Candidate = null;
            TimeSinceCast = 0;
        }

        /// <summary>Everything the bite system needs to know about this tick.</summary>
        public struct Ctx
        {
            public LureSpec Lure;
            public LineSpec Line;
            public Spot Spot;
            public double LureDepthNorm;
            public double RetrieveRate;
            public double Noise;
            public double SpotActivity;
            public double Jerk;
            public bool Struck;
            /// <summary>Pulls a species from the catch table on demand.</summary>
            public Func<Species> DrawCandidate;
        }

        /// <summary>
        /// Score how good the current presentation is, 0..~2. Exposed separately so
        /// the HUD can show the player *why* nothing is biting — a dead spot should
        /// be diagnosable, not mysterious.
        /// </summary>
        public Presentation ScorePresentation(in Ctx ctx, Species species)
        {
            // 1. Does this fish want this lure at all?
            double lureMatch = species != null
                ? species.LureMultiplier(ctx.Lure.Id, _lureMismatch)
                : 1.0;

            // 2. Is the bait in the right part of the water column?
            double depthMatch = 1;
            if (species != null)
            {
                double lo = species.DepthLo, hi = species.DepthHi;
                if (ctx.LureDepthNorm < lo)
                    depthMatch = 1 - MathUtil.SmoothStep(0, lo + 0.18, lo - ctx.LureDepthNorm);
                else if (ctx.LureDepthNorm > hi)
                    depthMatch = 1 - MathUtil.SmoothStep(0, (1 - hi) + 0.18, ctx.LureDepthNorm - hi);
                depthMatch = MathUtil.Clamp(depthMatch, 0.08, 1);
            }

            // 3. Does the retrieve suit the fish? Predators want movement; bottom
            //    feeders want the bait to sit still. Lure.Action is how much
            //    movement the lure generates on its own.
            double motion = MathUtil.Clamp01(ctx.RetrieveRate * 0.9 + ctx.Lure.Action * 0.8);
            double wantsMotion = species != null
                ? MathUtil.Clamp01(species.Fight.Aggression * 0.75 + 0.12)
                : 0.5;
            double actionMatch = 1 - Math.Abs(motion - wantsMotion) * 0.85;

            // 4. Stealth. Visible line in clear water on a cautious fish is the
            //    classic reason a good spot goes dead.
            double caution = species != null ? species.Bite.Caution : 0.35;
            double seen = ctx.Line.Visibility * ctx.Spot.WaterClarity;
            double stealth = MathUtil.Clamp(1 - seen * caution * 1.35 - ctx.Noise * caution * 0.8, 0.10, 1);

            LastScore = new Presentation
            {
                LureMatch = lureMatch,
                DepthMatch = depthMatch,
                ActionMatch = actionMatch,
                Stealth = stealth,
                Total = lureMatch * depthMatch * MathUtil.Clamp(actionMatch, 0.12, 1) * stealth,
            };
            return LastScore;
        }

        public BiteEvent Update(double dt, in Ctx ctx)
        {
            TimeSinceCast += dt;

            if (Cooldown > 0)
            {
                Cooldown -= dt;
                if (Cooldown <= 0 && State == BiteState.Spooked)
                {
                    State = BiteState.Searching;
                    Attraction = 0;
                }
            }

            if (State == BiteState.Idle || State == BiteState.Hooked) return default;

            // Striking with nothing there costs you: it yanks the lure and puts the
            // swim on edge. Cheap to do, so it needs a cost or strike-spam wins.
            if (ctx.Struck && (State == BiteState.Searching || State == BiteState.Interest))
            {
                bool had = State == BiteState.Interest;
                Attraction = Math.Max(0, Attraction - (had ? 1.6 : 0.6));
                if (had) return Spook("premature");
                return new BiteEvent { Type = BiteEventType.Whiff };
            }

            var score = ScorePresentation(ctx, Candidate);
            PresentationSmoothed = MathUtil.Damp(PresentationSmoothed, score.Total, 6, dt);

            switch (State)
            {
                case BiteState.Searching: return TickSearching(dt, ctx, score);
                case BiteState.Interest: return TickInterest(dt, ctx, score);
                case BiteState.Nibbling: return TickNibbling(dt, ctx);
                case BiteState.Committed: return TickCommitted(dt, ctx);
                default: return default;
            }
        }

        private BiteEvent TickSearching(double dt, in Ctx ctx, in Presentation score)
        {
            // Nothing specific is down there yet, so score against a neutral fish
            // and let the spot's own richness set the pace.
            double rate = FillRate * score.Total * (0.7 + ctx.SpotActivity * 0.6);
            Attraction += rate * dt;

            if (Attraction >= InterestThreshold)
            {
                var species = ctx.DrawCandidate?.Invoke();
                if (species == null) { Attraction = 0; return default; }
                Candidate = species;
                Attraction = InterestThreshold;
                State = BiteState.Interest;
                Timer = 0;
                return new BiteEvent { Type = BiteEventType.Interest, Species = species };
            }
            return default;
        }

        private BiteEvent TickInterest(double dt, in Ctx ctx, in Presentation score)
        {
            Timer += dt;
            var sp = Candidate;
            double speed = sp.Bite.Speed;

            // Now the score is against a real fish, and a mismatched lure actively
            // repels rather than merely failing to attract.
            if (score.Total < 0.35)
            {
                Attraction -= DecayRate * (0.35 - score.Total) * 4 * dt;
                if (Attraction <= 0.15) return Spook("lost-interest");
            }
            else
            {
                Attraction += FillRate * score.Total * speed * dt;
            }

            // A sudden jerk of the rod while a cautious fish is inspecting scares
            // it. PARITY: the jerk test short-circuits, so the draw only happens
            // when the jerk is actually large.
            if (ctx.Jerk > 0.6 && _rng.Next() < sp.Bite.Caution * ctx.Jerk * dt * 3)
            {
                return Spook("startled");
            }

            if (Attraction >= CommitThreshold)
            {
                // PARITY: `extra` is drawn before the nibble count, matching the JS
                // evaluation order.
                int extra = _rng.Next() < sp.Bite.Caution ? 1 : 0;   // cautious fish test more
                NibblesLeft = _rng.Int(sp.Bite.NibbleMin, sp.Bite.NibbleMax) + extra;
                State = BiteState.Nibbling;
                NextTap = _rng.Float(NibbleGapMin, NibbleGapMax) * 0.5;
                TapTimer = 0;
                Tapping = false;
                return new BiteEvent { Type = BiteEventType.Committing, Species = sp };
            }
            return default;
        }

        private BiteEvent TickNibbling(double dt, in Ctx ctx)
        {
            var sp = Candidate;

            if (ctx.Struck)
            {
                // Struck on a tap. Classic beginner mistake — the fish has the bait
                // in its lips, not its throat.
                //
                // PARITY: the JavaScript draws here and then spooks either way, so
                // the branch is cosmetic but the draw is not. Keeping it preserves
                // the stream; deleting it desynchronises every later roll.
                _rng.Next();
                return Spook("struck-early");
            }

            if (Tapping)
            {
                TapTimer -= dt;
                if (TapTimer <= 0)
                {
                    Tapping = false;
                    NextTap = _rng.Float(NibbleGapMin, NibbleGapMax);
                    NibblesLeft--;
                    if (NibblesLeft <= 0)
                    {
                        State = BiteState.Committed;
                        WindowTotal = sp.Bite.Window;
                        WindowLeft = WindowTotal;
                        return new BiteEvent { Type = BiteEventType.Bite, Species = sp, Window = WindowTotal };
                    }
                }
                return default;
            }

            NextTap -= dt;
            if (NextTap <= 0)
            {
                Tapping = true;
                TapTimer = TapDuration;
                return new BiteEvent { Type = BiteEventType.Nibble, Species = sp, Remaining = NibblesLeft };
            }
            return default;
        }

        private BiteEvent TickCommitted(double dt, in Ctx ctx)
        {
            var sp = Candidate;
            WindowLeft -= dt;

            if (ctx.Struck)
            {
                // Quality peaks slightly after the window opens — the fish needs a
                // beat to turn with the bait — and falls off toward the end.
                double elapsed = WindowTotal - WindowLeft;
                double ideal = WindowTotal * 0.42;
                double off = Math.Abs(elapsed - ideal) / (WindowTotal * 0.62);
                double quality = MathUtil.Clamp01(1 - off * off);
                State = BiteState.Hooked;
                StrikeQuality = quality;
                return new BiteEvent
                {
                    Type = BiteEventType.Hooked, Species = sp,
                    Quality = quality, Timing = elapsed,
                };
            }

            if (WindowLeft <= 0)
            {
                // Too slow. The fish spits it and is now suspicious of this bait.
                return Spook("too-slow", true);
            }
            return default;
        }

        private BiteEvent Spook(string reason, bool missed = false)
        {
            var sp = Candidate;
            State = BiteState.Spooked;
            Cooldown = _rng.Float(SpookCooldownMin, SpookCooldownMax);
            Attraction = 0;
            Candidate = null;
            Tapping = false;
            NibblesLeft = 0;
            return new BiteEvent
            {
                Type = missed ? BiteEventType.Missed : BiteEventType.Spooked,
                Reason = reason,
                Species = sp,
            };
        }

        /// <summary>Snapshot for the HUD.</summary>
        public struct Telemetry
        {
            public BiteState State;
            public double Attraction, AttractionPct, Presentation;
            public Presentation Score;
            public Species Candidate;
            public bool Tapping;
            public double WindowLeft, WindowPct, Cooldown;
            public int NibblesLeft;
        }

        public Telemetry GetTelemetry() => new Telemetry
        {
            State = State,
            Attraction = Attraction,
            AttractionPct = MathUtil.Clamp01(Attraction / CommitThreshold),
            Presentation = PresentationSmoothed,
            Score = LastScore,
            Candidate = Candidate,
            Tapping = Tapping,
            WindowLeft = WindowLeft,
            WindowPct = WindowTotal > 0 ? MathUtil.Clamp01(WindowLeft / WindowTotal) : 0,
            Cooldown = Cooldown,
            NibblesLeft = NibblesLeft,
        };
    }
}
