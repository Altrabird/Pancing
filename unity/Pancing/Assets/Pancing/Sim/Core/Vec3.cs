using System;

namespace Pancing.Sim
{
    /// <summary>
    /// A double-precision 3-vector.
    ///
    /// Not UnityEngine.Vector3, for two reasons: this assembly has no engine
    /// reference by design, and Vector3 is single-precision — the ballistics and
    /// the tension solver are compared against the JavaScript reference build to
    /// twelve decimal places, which float cannot hold. The renderer converts to
    /// Vector3 at the boundary, where the precision no longer matters because a
    /// pixel is not that small.
    /// </summary>
    public struct Vec3
    {
        public double X, Y, Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        /// <summary>Horizontal distance from the origin — the one that means "cast distance".</summary>
        public double FlatLength => MathUtil.Hypot(X, Z);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double k) => new Vec3(a.X * k, a.Y * k, a.Z * k);

        public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
    }
}
