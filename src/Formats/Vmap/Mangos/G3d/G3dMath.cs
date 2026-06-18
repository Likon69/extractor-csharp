using System.Runtime.InteropServices;

namespace MaNGOS.Extractor.Formats.Vmap.Mangos.G3d;

/// <summary>
/// Faithful port of the tiny subset of G3D math primitives consumed by the
/// BIH builder and the TileAssembler phase-2 (vmap-extractor).
///
/// Reference: mangostwo-server/dep/g3dlite/G3D/g3dmath.{h,cpp}, Vector3.{h,cpp},
/// AABox.{h,cpp}. Every method here is a literal 1:1 translation of the G3D
/// source so that the BIH output is byte-for-byte identical to the C++
/// reference extractor.
///
/// Determinism notes (DO NOT change without re-diffing against the C++ output):
///   - <see cref="FuzzyEq(double,double)"/> uses an adaptive epsilon
///     (fuzzyEpsilon * (|a| + 1.0)) and short-circuits on a == b first.
///   - <see cref="G3dVector3.PrimaryAxis"/> compares absolute values cast to
///     double with strict &gt; (tie-break order Z &gt; Y &gt; X).
/// </summary>
public static class G3dMath
{
    /// <summary>
    /// G3D <c>#define fuzzyEpsilon (0.00001f)</c> (g3dmath.h:119).
    /// Kept as a float literal to match the C++ macro exactly; it is promoted
    /// to double at every use site (as the C++ macro is).
    /// </summary>
    public const float FuzzyEpsilon = 0.00001f;

    /// <summary>G3D <c>double inf()</c> → std::numeric_limits&lt;double&gt;::infinity().</summary>
    public static double Inf => double.PositiveInfinity;

    /// <summary>G3D <c>float finf()</c> → std::numeric_limits&lt;float&gt;::infinity().</summary>
    public static float FInf => float.PositiveInfinity;

    /// <summary>G3D <c>float fnan()</c> → std::numeric_limits&lt;float&gt;::quiet_NaN().</summary>
    public static float FNaN => float.NaN;

    /// <summary>
    /// G3D <c>inline double eps(double a, double b)</c> (g3dmath.h:792-804).
    /// Adaptive epsilon: fuzzyEpsilon * (|a| + 1.0), capped to the raw
    /// fuzzyEpsilon when |a|+1 == +inf. The b argument is intentionally
    /// ignored, exactly like the C++ (see the (void)b; comment there).
    /// </summary>
    public static double Eps(double a, double b)
    {
        _ = b; // intentionally ignored, matching C++
        double aa = Abs(a) + 1.0;
        if (aa == double.PositiveInfinity)
        {
            return FuzzyEpsilon; // promoted to double
        }
        return FuzzyEpsilon * aa;
    }

    /// <summary>
    /// G3D <c>inline bool fuzzyEq(double a, double b)</c> (g3dmath.h:806-808).
    /// Short-circuits on exact equality, then uses adaptive epsilon with &lt;=.
    /// </summary>
    public static bool FuzzyEq(double a, double b)
    {
        return (a == b) || (Abs(a - b) <= Eps(a, b));
    }

    /// <summary>G3D <c>inline bool fuzzyNe(double a, double b)</c>.</summary>
    public static bool FuzzyNe(double a, double b)
    {
        return !FuzzyEq(a, b);
    }

    /// <summary>G3D <c>inline double abs(double fValue)</c> (g3dmath.h:598-600).</summary>
    public static double Abs(double fValue) => double.Abs(fValue);
}

/// <summary>
/// Minimal port of <c>G3D::Vector3</c> restricted to the operations consumed
/// by BIH and TileAssembler. Reference: Vector3.{h,cpp}. Layout is
/// <c>[X, Y, Z]</c> as 3 contiguous floats (Sequential) so that indexing by
/// axis matches the C++ <c>((float*)this)[i]</c> semantics.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct G3dVector3
{
    public enum Axis : int
    {
        X_AXIS = 0,
        Y_AXIS = 1,
        Z_AXIS = 2,
        DETECT_AXIS = -1,
    }

    public readonly float X;
    public readonly float Y;
    public readonly float Z;

    public G3dVector3(float x, float y, float z) { X = x; Y = y; Z = z; }

    /// <summary>
    /// Indexer matching G3D <c>operator[](int i)</c> which casts this to a
    /// float* and reads element i. Index 0 = X, 1 = Y, 2 = Z.
    /// </summary>
    public float this[int i] => i switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        _ => throw new IndexOutOfRangeException($"G3dVector3[{i}]"),
    };

    /// <summary>
    /// G3D <c>Vector3::primaryAxis()</c> (Vector3.cpp:81-104). CRITICAL for BIH
    /// determinism: comparisons are strict &gt; performed on doubles, and the
    /// tie-break order is Z &gt; Y &gt; X (the else branches win on equality).
    /// Do NOT change &gt; to &gt;=, do NOT compute in float.
    /// </summary>
    public Axis PrimaryAxis()
    {
        Axis a = Axis.X_AXIS;

        double nx = G3dMath.Abs(X); // float → double promotion, then fabs
        double ny = G3dMath.Abs(Y);
        double nz = G3dMath.Abs(Z);

        if (nx > ny)
        {
            if (nx > nz)
            {
                a = Axis.X_AXIS;
            }
            else
            {
                a = Axis.Z_AXIS;
            }
        }
        else
        {
            if (ny > nz)
            {
                a = Axis.Y_AXIS;
            }
            else
            {
                a = Axis.Z_AXIS;
            }
        }

        return a;
    }

    /// <summary>G3D <c>Vector3::min(const Vector3&amp;)</c> (Vector3.h:758-760).</summary>
    public G3dVector3 Min(G3dVector3 v) => new(Math.Min(v.X, X), Math.Min(v.Y, Y), Math.Min(v.Z, Z));

    /// <summary>G3D <c>Vector3::max(const Vector3&amp;)</c> (Vector3.h:762-764).</summary>
    public G3dVector3 Max(G3dVector3 v) => new(Math.Max(v.X, X), Math.Max(v.Y, Y), Math.Max(v.Z, Z));

    public static G3dVector3 operator +(G3dVector3 a, G3dVector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static G3dVector3 operator -(G3dVector3 a, G3dVector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
}

/// <summary>
/// Minimal port of <c>G3D::AABox</c> restricted to what BIH + TileAssembler
/// need. Reference: AABox.{h,cpp}. Like the C++ class this is a mutable value
/// type so that <c>merge()</c> can mutate <c>lo</c>/<c>hi</c> in place — note
/// that BIH/Subdivide copies AABounds around by value, just like the C++.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct G3dAaBox
{
    public G3dVector3 Lo;
    public G3dVector3 Hi;

    /// <summary>
    /// G3D <c>AABox::set(lo, hi)</c> (AABox.h:62-69). Copies lo/hi verbatim,
    /// NO reordering (the debugAssert is compiled out in release). Caller must
    /// guarantee lo &lt;= hi per component.
    /// </summary>
    public void Set(G3dVector3 low, G3dVector3 high) { Lo = low; Hi = high; }

    public G3dVector3 Low() => Lo;
    public G3dVector3 High() => Hi;

    /// <summary>G3D <c>AABox::merge(const AABox&amp; a)</c> (AABox.h:72-76).</summary>
    public void Merge(G3dAaBox a)
    {
        Lo = Lo.Min(a.Lo);
        Hi = Hi.Max(a.Hi);
    }

    /// <summary>G3D <c>AABox::merge(const Vector3&amp; a)</c> (AABox.h:78-82).</summary>
    public void Merge(G3dVector3 a)
    {
        Lo = Lo.Min(a);
        Hi = Hi.Max(a);
    }

    /// <summary>
    /// G3D <c>AABox::operator+(const Vector3&amp; v)</c> (AABox.h:246-251).
    /// Translates BOTH lo and hi by v. Used by TileAssembler.cpp:137 to shift
    /// the worldspawn bound by (533.33333f*32, 533.33333f*32, 0.f).
    /// </summary>
    public static G3dAaBox operator +(G3dAaBox box, G3dVector3 v)
    {
        G3dAaBox o;
        o.Lo = box.Lo + v;
        o.Hi = box.Hi + v;
        return o;
    }
}
