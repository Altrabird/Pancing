namespace Pancing.Sim
{
    /// <summary>
    /// The gear fields the physics actually consumes, pulled out of the richer
    /// GearItem records so the solver has no business knowing what a rod costs.
    /// Units are real-ish so the physics stays intuitive to tune:
    ///
    ///   Power      N    force at the rod tip that produces full bend
    ///   Stiffness  N/m  spring rate of the blank; higher = less shock absorption
    ///   Test       kg   line breaking strain (converted to N by the solver)
    ///   Stretch    -    fraction of length the line yields before load builds
    ///   Drag       N    maximum clutch force before the spool slips
    ///   Retrieve   m/s  bare line speed at zero load
    /// </summary>
    public struct RodSpec
    {
        public string Id;
        public double Power;
        public double Stiffness;
        public double Length;
        public double CastPower;
        public double Sensitivity;
    }

    public struct ReelSpec
    {
        public string Id;
        public double Drag;
        public double Retrieve;
        public double DragSmooth;
    }

    public struct LineSpec
    {
        public string Id;
        public double Test;
        public double Stretch;
        public double Abrasion;
        public double Visibility;
    }

    /// <summary>
    /// Lure behaviour. `Action` drives the bite FSM: how much movement the lure
    /// generates on its own, which is what decides whether a predator that wants
    /// motion or a bottom feeder that wants stillness is interested.
    /// </summary>
    public struct LureSpec
    {
        public string Id;
        /// <summary>0..1, where in the water column it works. 0.02 floats, 0.8 sits on the bed.</summary>
        public double Sink;
        public double Action;
        public double Noise;
        public double Spook;
        /// <summary>Shifts the size distribution's mean toward the top of the range.</summary>
        public double SizeBias;
    }

    /// <summary>The four equipped items, resolved for the simulation.</summary>
    public struct GearSet
    {
        public RodSpec Rod;
        public ReelSpec Reel;
        public LineSpec Line;
        public LureSpec Lure;
    }
}
