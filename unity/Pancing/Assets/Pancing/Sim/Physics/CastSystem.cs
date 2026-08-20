using System;

namespace Pancing.Sim
{
    public enum CastPhase { Idle, Charging, Flying, Sinking, Settled }

    /// <summary>
    /// Casting: charge, release, ballistic flight, splashdown, sink.
    /// A port of web/src/physics/cast.js.
    ///
    /// The charge is a sawtooth that runs past 1.0 into an overload band.
    /// Releasing inside the sweet spot gives full distance and tight accuracy;
    /// holding past it gives you a backlash — more distance, much worse accuracy,
    /// and a chance of a bird's nest that costs you the cast. That single curve is
    /// the whole risk decision at the front of every fishing loop.
    /// </summary>
    public sealed class CastSystem
    {
        private const double AirDrag = 0.11;
        private const double Gravity = 9.81;
        /// <summary>Launch speed at full charge with a reference rod, m/s.</summary>
        private const double LaunchSpeed = 22.5;

        /// <summary>Charge ramps to 1.0 over this many seconds, then overloads.</summary>
        public const double ChargeTime = 1.15;
        public const double OverloadTime = 0.45;
        /// <summary>Releasing within this much of 1.0 counts as a perfect cast.</summary>
        public const double PerfectBand = 0.09;

        private readonly Rng _rng;

        public CastPhase Phase = CastPhase.Idle;
        public double Charge;
        public double Overload;
        public Vec3 Pos;
        public Vec3 Vel;
        public double AimYaw;
        /// <summary>Radians above horizontal.</summary>
        public double AimPitch = 0.62;
        public double SinkDepth;
        public double TargetDepth;
        /// <summary>0..1, how clean the release was.</summary>
        public double Quality;
        public bool Backlash;
        public double Distance;

        public CastSystem(Rng rng) { _rng = rng; }

        public bool BeginCharge()
        {
            if (Phase != CastPhase.Idle) return false;
            Phase = CastPhase.Charging;
            Charge = 0;
            Overload = 0;
            Backlash = false;
            return true;
        }

        /// <summary>Returns true when the charge has run right through the overload
        /// band and the cast has gone off by itself — badly.</summary>
        public bool UpdateCharge(double dt)
        {
            if (Phase != CastPhase.Charging) return false;
            if (Charge < 1)
            {
                Charge = Math.Min(1, Charge + dt / ChargeTime);
            }
            else
            {
                Overload = Math.Min(1, Overload + dt / OverloadTime);
                if (Overload >= 1) return true;
            }
            return false;
        }

        public struct Release
        {
            public double Power;
            public double Quality;
            public bool Backlash;
            public bool Perfect;
            public bool Ok;
        }

        /// <param name="tip">Rod tip world position.</param>
        /// <param name="wind">Lateral wind, m/s.</param>
        public Release DoRelease(Vec3 tip, GearSet gear, double wind = 0)
        {
            if (Phase != CastPhase.Charging) return default;

            double over = Overload;
            double raw = Charge + over * 0.28;                       // overload adds reach
            double nearPerfect = 1 - MathUtil.Clamp01(Math.Abs(Charge - 1) / PerfectBand);
            Quality = over > 0 ? MathUtil.Clamp01(nearPerfect * (1 - over * 0.9)) : nearPerfect;

            // Backlash chance climbs steeply through the overload band.
            Backlash = over > 0 && _rng.Next() < over * over * 0.55;

            double rodPower = gear.Rod.CastPower;
            // A heavy sinking lure casts further than a bag of fluff.
            double lureMass = 0.45 + gear.Lure.Sink * 0.55;
            // Tuned so the starter bamboo rod reaches ~14 m and the top rod ~34 m,
            // which is the full width of the fishable water. Range goes as speed²,
            // so this constant is sensitive — measure, don't guess.
            double speed = LaunchSpeed * raw * rodPower * MathUtil.Lerp(0.85, 1.12, lureMass);

            // Accuracy: spread in radians, worsened by wind, overload and a soft rod.
            double spread = (0.030 + (1 - Quality) * 0.085 + over * 0.10)
                          * MathUtil.Lerp(1.25, 0.85, gear.Rod.CastPower);
            double yaw = AimYaw + _rng.Normal(0, spread);
            double pitch = MathUtil.Clamp(AimPitch + _rng.Normal(0, spread * 0.55), 0.12, 1.35);

            double horiz = Math.Cos(pitch) * speed;
            Pos = tip;
            Vel = new Vec3(
                Math.Sin(yaw) * horiz + wind * 0.35,
                Math.Sin(pitch) * speed,
                Math.Cos(yaw) * horiz);

            if (Backlash)
            {
                // Bird's nest: the spool locks mid-flight and the lure drops short.
                Vel.X *= 0.34; Vel.Y *= 0.42; Vel.Z *= 0.34;
            }

            Phase = CastPhase.Flying;
            Charge = 0;
            Overload = 0;
            return new Release
            {
                Power = raw,
                Quality = Quality,
                Backlash = Backlash,
                Perfect = Quality > 0.86 && !Backlash,
                Ok = true,
            };
        }

        /// <summary>
        /// Where a cast released right now would land, in metres out.
        ///
        /// Integrates the SAME ballistic step the real flight uses rather than
        /// approximating with a range formula, so the aim line cannot quietly
        /// disagree with what the lure actually does. Deterministic: no RNG, so it
        /// draws the centre line of the cast, not the shot. Accuracy spread,
        /// crosswind and backlash all still move the real splashdown — which is
        /// exactly the risk the charge meter is asking you to take.
        /// </summary>
        public static double PredictDistance(double charge, double overload, in GearSet gear,
                                             double aimPitch, double tipHeight)
        {
            double raw = charge + overload * 0.28;
            if (raw <= 0) return 0;

            double lureMass = 0.45 + gear.Lure.Sink * 0.55;
            double speed = LaunchSpeed * raw * gear.Rod.CastPower * MathUtil.Lerp(0.85, 1.12, lureMass);
            double pitch = MathUtil.Clamp(aimPitch, 0.12, 1.35);

            // Solved in the vertical plane containing the aim: with no spread the
            // trajectory never leaves it.
            double vy = Math.Sin(pitch) * speed;
            double vz = Math.Cos(pitch) * speed;
            double y = tipHeight, z = 0;
            const double dt = 1.0 / 120.0;

            for (int i = 0; i < 3000; i++)
            {
                if (y <= 0 && vy < 0) break;
                double sp = Math.Sqrt(vy * vy + vz * vz);
                double drag = AirDrag * sp;
                vy += (-Gravity - drag * vy) * dt;
                vz += -drag * vz * dt;
                y += vy * dt;
                z += vz * dt;
            }
            return Math.Max(0, z);
        }

        public enum FlightEvent { None, Splash, DryLand }

        public struct FlightResult
        {
            public FlightEvent Event;
            public double Impact;
            public Vec3 Pos;
        }

        /// <param name="waterY">Water surface height.</param>
        public FlightResult UpdateFlight(double dt, double waterY, double wind = 0)
        {
            if (Phase != CastPhase.Flying) return default;

            double speed = Math.Sqrt(Vel.X * Vel.X + Vel.Y * Vel.Y + Vel.Z * Vel.Z);
            double drag = AirDrag * speed;
            Vel.X += (-drag * Vel.X + wind * 0.8) * dt;
            Vel.Y += (-Gravity - drag * Vel.Y) * dt;
            Vel.Z += -drag * Vel.Z * dt;

            Pos.X += Vel.X * dt;
            Pos.Y += Vel.Y * dt;
            Pos.Z += Vel.Z * dt;

            if (Pos.Y <= waterY && Vel.Y < 0)
            {
                Pos.Y = waterY;
                Phase = CastPhase.Sinking;
                SinkDepth = 0;
                Distance = MathUtil.Hypot(Pos.X, Pos.Z);
                return new FlightResult { Event = FlightEvent.Splash, Impact = speed, Pos = Pos };
            }
            // Landed on the bank.
            if (Pos.Y <= 0 && Vel.Y < 0)
            {
                Pos.Y = 0;
                Phase = CastPhase.Settled;
                return new FlightResult { Event = FlightEvent.DryLand, Pos = Pos };
            }
            return default;
        }

        /// <summary>
        /// Sink the lure toward its working depth. Floating lures (popper, frog)
        /// stop at the surface; bottom baits keep going until they find the bed.
        /// Returns true on the tick the lure settles.
        /// </summary>
        public bool UpdateSink(double dt, LureSpec lure, double bedDepth)
        {
            if (Phase != CastPhase.Sinking) return false;
            TargetDepth = bedDepth * MathUtil.Clamp01(lure.Sink);
            double rate = 0.35 + lure.Sink * 1.25;
            SinkDepth = Math.Min(TargetDepth, SinkDepth + rate * dt);
            if (SinkDepth >= TargetDepth - 1e-3)
            {
                Phase = CastPhase.Settled;
                return true;
            }
            return false;
        }

        /// <summary>Retrieve pulls the lure back toward the angler and lifts it in the water.</summary>
        public double Retrieve(double dt, double rate, Vec3 tipPos)
        {
            double dx = tipPos.X - Pos.X;
            double dz = tipPos.Z - Pos.Z;
            double d = MathUtil.Hypot(dx, dz);
            if (d < 1e-4) return 0;
            double move = Math.Min(d, rate * dt);
            Pos.X += (dx / d) * move;
            Pos.Z += (dz / d) * move;
            // Moving line lifts the lure; a fast retrieve fishes shallower.
            SinkDepth = Math.Max(0, SinkDepth - rate * dt * 0.42);
            Distance = MathUtil.Hypot(Pos.X, Pos.Z);
            return move;
        }

        public void Reset()
        {
            Phase = CastPhase.Idle;
            Charge = 0;
            Overload = 0;
            SinkDepth = 0;
            Backlash = false;
            Vel = Vec3.Zero;
        }

        public struct ChargeMeter
        {
            public double Value, Overload;
            public bool InSweetSpot, Charging;
        }

        /// <summary>0..1 meter fill for the HUD, plus whether we are in the danger band.</summary>
        public ChargeMeter Meter() => new ChargeMeter
        {
            Value = Charge,
            Overload = Overload,
            InSweetSpot = Charge >= 1 - PerfectBand && Overload == 0,
            Charging = Phase == CastPhase.Charging,
        };
    }
}
