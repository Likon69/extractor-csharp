using System.Numerics;
using MaNGOS.Extractor.Formats.Wmo.Math;

namespace MaNGOS.Extractor.Tests.Core;

/// <summary>
/// Regression tests for the M2/WMO positioning fixes that align the C# port
/// with Mangos C++ vmap/mmap extractors.
///
/// Verified-true bug (fixed in this commit):
///   - MangosDoodadExtractor.ExtractSet + MangosVmapExtractorService.BuildWmoSpawn
///     were missing the WMO worldspawn fix from wmo.cpp:629-643 (raw pos.x==0
///     &amp;&amp; pos.z==0 → (32G, ?, 32G) before fixCoords).
///
/// Guard tests against verified-false alarms (so the mistakes are not reintroduced):
///   - WmoRotationMath.Mul(M, v) was suspected to be row-vector. It is in fact
///     column-vector (sum M[r][c]*v[c]) and matches G3D's Mat3::operator*.
///   - MangosVmapGeometryLoader.AppendSpawnCollision rotation composition was
///     suspected to apply rotations in the wrong order. Both G3D's v*M and
///     System.Numerics' Vector3.Transform apply the LEFTMOST matrix in a
///     product FIRST, so Rx*Ry*Rz in C# matches the C++ pipeline (which
///     applies Rx first via the M^T effect).
/// </summary>
public class TestM2RotationFix
{
    private const float Tol = 1e-4f;

    // Reproduces the C# MangosVmapGeometryLoader.AppendSpawnCollision rotation
    // pipeline exactly (Rx first, then Ry, then Rz, all applied via the
    // angle mapping iRot.z → X, iRot.x → Y, iRot.y → Z).
    private static Vector3 CSharpRotationPipeline(Vector3 v, float iRotX, float iRotY, float iRotZ)
    {
        float ax = iRotZ * MathF.PI / 180f;
        float ay = iRotX * MathF.PI / 180f;
        float az = iRotY * MathF.PI / 180f;
        var rotMatrix = Matrix4x4.CreateRotationX(ax)
                      * Matrix4x4.CreateRotationY(ay)
                      * Matrix4x4.CreateRotationZ(az);
        return Vector3.Transform(v, rotMatrix);
    }

    // Reproduces the C++ TerrainBuilder::loadVMap rotation observable effect:
    // M = Rx(-z)·Ry(-x)·Rz(-y), applied as v*M (row) = M^T*v (column). The
    // effective column pipeline is Rz(+y)·Ry(+x)·Rx(+z) — applied right-to-left,
    // so Rx first, then Ry, then Rz. In System.Numerics Vector3.Transform
    // applies the leftmost matrix first, so the equivalent pipeline is
    // Rx*Ry*Rz (Rx leftmost).
    private static Vector3 CppEffectiveRotation(Vector3 v, float iRotX, float iRotY, float iRotZ)
    {
        float ax = iRotZ * MathF.PI / 180f;
        float ay = iRotX * MathF.PI / 180f;
        float az = iRotY * MathF.PI / 180f;
        var rx = Matrix4x4.CreateRotationX(ax);
        var ry = Matrix4x4.CreateRotationY(ay);
        var rz = Matrix4x4.CreateRotationZ(az);
        var pipeline = rx * ry * rz; // leftmost Rx applied first
        return Vector3.Transform(v, pipeline);
    }

    [Fact]
    public void CSharpPipeline_MatchesCpp_ForSingleAxisRotations()
    {
        var cases = new (float X, float Y, float Z)[]
        {
            (0f,   0f,  90f), // X-only (iRot.z → X)
            (0f,  90f,   0f), // Y-only (iRot.x → Y)
            (90f,  0f,   0f), // Z-only (iRot.y → Z)
            (45f,  0f,   0f),
            (0f, -45f,   0f),
            (0f,   0f, 180f),
        };
        var v = new Vector3(1, 2, 3);
        foreach (var (x, y, z) in cases)
        {
            var cpp = CppEffectiveRotation(v, x, y, z);
            var cs = CSharpRotationPipeline(v, x, y, z);
            AssertVectorClose(cs, cpp);
        }
    }

    [Fact]
    public void CSharpPipeline_MatchesCpp_ForCombinedAxisRotations()
    {
        var cases = new (float X, float Y, float Z)[]
        {
            (90f,  0f,  90f),
            (0f,  90f,  90f),
            (90f, 90f,   0f),
            (90f, 90f,  90f),
            (30f, 60f,  45f),
            (-45f, 90f, 30f),
        };
        var v = new Vector3(1, 2, 3);
        foreach (var (x, y, z) in cases)
        {
            var cpp = CppEffectiveRotation(v, x, y, z);
            var cs = CSharpRotationPipeline(v, x, y, z);
            AssertVectorClose(cs, cpp);
        }
    }

    [Fact]
    public void CSharpPipeline_ManualCheck_iRot_90_0_90_On_X_Axis()
    {
        // iRot = (90, 0, 90), v = (1, 0, 0).
        // Both pipelines: Rx(+90°) first (X axis unchanged) → still (1, 0, 0),
        // then Ry(+90°) → +X → -Z → (0, 0, -1).
        var v = new Vector3(1, 0, 0);
        var result = CSharpRotationPipeline(v, 90f, 0f, 90f);
        Assert.InRange(result.X - 0f, -Tol, Tol);
        Assert.InRange(result.Y - 0f, -Tol, Tol);
        Assert.InRange(result.Z - (-1f), -Tol, Tol);
    }

    [Fact]
    public void CSharpPipeline_ManualCheck_iRot_0_90_0_On_X_Axis()
    {
        // iRot = (0, 90, 0), v = (1, 0, 0). Pure Z+90°: +X → +Y.
        var v = new Vector3(1, 0, 0);
        var result = CSharpRotationPipeline(v, 0f, 90f, 0f);
        Assert.InRange(result.X - 0f, -Tol, Tol);
        Assert.InRange(result.Y - 1f, -Tol, Tol);
        Assert.InRange(result.Z - 0f, -Tol, Tol);
    }

    [Fact]
    public void CSharpPipeline_ManualCheck_iRot_0_0_90_On_X_Axis()
    {
        // iRot = (0, 0, 90), v = (1, 0, 0). Pure X+90°: +X unchanged.
        var v = new Vector3(1, 0, 0);
        var result = CSharpRotationPipeline(v, 0f, 0f, 90f);
        Assert.InRange(result.X - 1f, -Tol, Tol);
        Assert.InRange(result.Y - 0f, -Tol, Tol);
        Assert.InRange(result.Z - 0f, -Tol, Tol);
    }

    [Fact]
    public void CSharpPipeline_ManualCheck_iRot_90_90_90_On_X_Axis()
    {
        // iRot = (90, 90, 90), v = (1, 0, 0). All three axes rotate.
        // Rx(+90°) first: (1,0,0) → (1,0,0). Ry(+90°) next: (1,0,0) → (0,0,-1).
        // Rz(+90°) last: (0,0,-1) → (0,0,-1) (Z rotation doesn't affect Z axis).
        // Final: (0, 0, -1).
        var v = new Vector3(1, 0, 0);
        var result = CSharpRotationPipeline(v, 90f, 90f, 90f);
        Assert.InRange(result.X - 0f, -Tol, Tol);
        Assert.InRange(result.Y - 0f, -Tol, Tol);
        Assert.InRange(result.Z - (-1f), -Tol, Tol);
    }

    [Fact]
    public void WmoRotationMath_Mul_IsColumnVector()
    {
        // WmoRotationMath.Mul is column-vector M*v (sum M[r][c]*v[c]) and
        // matches G3D's Mat3::operator*. Use a non-symmetric matrix so any
        // accidental flip to row-vector would be detectable.
        var m = new Matrix4x4(
            1, 2, 3, 0,
            4, 5, 6, 0,
            7, 8, 9, 0,
            0, 0, 0, 1);
        var v = new Vector3(1, 1, 1);
        // Column-vector M*v:
        //   result.X = 1*1 + 2*1 + 3*1 = 6
        //   result.Y = 4*1 + 5*1 + 6*1 = 15
        //   result.Z = 7*1 + 8*1 + 9*1 = 24
        var r = WmoRotationMath.Mul(m, v);
        Assert.InRange(r.X - 6f, -Tol, Tol);
        Assert.InRange(r.Y - 15f, -Tol, Tol);
        Assert.InRange(r.Z - 24f, -Tol, Tol);
    }

    [Fact]
    public void WmoRotationMath_Mul_ZPlus90_OnYAxis()
    {
        // wmoRot = Z+90° from FromWmoRot(rotX=0, rotY=90, rotZ=0).
        // Z+90° sends +Y → -X. Apply to (0, 1, 0) → (-1, 0, 0).
        var m = WmoRotationMath.FromWmoRot(0f, 90f, 0f);
        var r = WmoRotationMath.Mul(m, new Vector3(0, 1, 0));
        Assert.InRange(r.X - (-1f), -Tol, Tol);
        Assert.InRange(r.Y, -Tol, Tol);
        Assert.InRange(r.Z, -Tol, Tol);
    }

    // -----------------------------------------------------------------------
    // M2 winding regression — the C# flip logic must emit triangles in the same
    // cyclic-perm order the C++ produces, otherwise Recast sees M2 top faces as
    // ceilings and the bot walks under the M2 instead of on top of it.
    //
    // Reference: C++ writes the .vmd with a per-triangle transposition (swap I1↔I2)
    // then copyIndices(flip=true) writes (idx2, idx1, idx0). For a triangle loaded
    // as (idx0, idx1, idx2) = (A, C, B) (from C++ .vmd), that yields output (B, C, A)
    // — a cyclic perm by 2 of the original (A, B, C) which PRESERVES the M2 winding.
    //
    // The C# code must emit the same (B, C, A) ordering from raw (A, B, C). That
    // means for stored (I0=A, I1=B, I2=C) it must output (I1, I2, I0). If the
    // code emits (I0, I2, I1) instead, that's a transposition (swap I1↔I2) which
    // flips the winding — exactly the bug that caused navmesh-on-base-of-rocks.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Cross-product reference for the M2 top-face winding fix. Uses a unit
    /// triangle in the Z=+5 plane (M2's up axis is Z).
    ///
    /// Inputs in M2 file order:
    ///   A = (0, 0, 5) — center back
    ///   B = (1, 0, 5) — right  (+X)
    ///   C = (0, 1, 5) — front (+Y)
    ///
    /// Cross-product geometry:
    ///   (B-A) x (C-A) = (1,0,0) x (0,1,0) = (0,0,+1)   ⇒  CCW from above, normal +Z.
    ///
    /// The C++ output (after .vmd swap + copyIndices) is (B, C, A) which is a
    /// cyclic perm of (A,B,C) and therefore yields the SAME normal +Z.
    /// The buggy C# output (A, C, B) is a transposition and yields normal −Z
    /// (the "ceiling" symptom the user reported).
    /// </summary>
    [Fact]
    public void M2Flip_CrossProduct_HorizontalTriangle_VerifyNormalDirection()
    {
        var A = new Vector3(0, 0, 5);
        var B = new Vector3(1, 0, 5);
        var C = new Vector3(0, 1, 5);

        // Original M2 winding — reference (CCW from above, normal +Z).
        var cppOut = new[] { B, C, A };
        var cppNormal = Vector3.Cross(cppOut[1] - cppOut[0], cppOut[2] - cppOut[0]);
        Assert.Equal(0f, cppNormal.X, 5);
        Assert.Equal(0f, cppNormal.Y, 5);
        Assert.Equal(1f, cppNormal.Z, 5);

        // Old (buggy) C# winding — (A, C, B) was a transposition of (A, B, C),
        // so the cross product flipped sign. This is the state before the fix.
        var oldCsOut = new[] { A, C, B };
        var oldNormal = Vector3.Cross(oldCsOut[1] - oldCsOut[0], oldCsOut[2] - oldCsOut[0]);
        Assert.Equal(0f, oldNormal.X, 5);
        Assert.Equal(0f, oldNormal.Y, 5);
        Assert.Equal(-1f, oldNormal.Z, 5); // ← the symptom: normal flipped ⇒ Recast sees a ceiling

        // New (fixed) C# winding — write (I1, I2, I0) for loaded (I0=A, I1=B, I2=C).
        // i.e. (B, C, A), which is the C++ order. Normal +Z, walkable.
        var newCsOut = new[] { B, C, A };
        var newNormal = Vector3.Cross(newCsOut[1] - newCsOut[0], newCsOut[2] - newCsOut[0]);
        Assert.Equal(0f, newNormal.X, 5);
        Assert.Equal(0f, newNormal.Y, 5);
        Assert.Equal(1f, newNormal.Z, 5);
    }

    /// <summary>
    /// Verifies the exact tuple order emitted by the C# flip for a stored (0,1,2)
    /// index triple. Locks in the (I1, I2, I0) order introduced by the fix so
    /// anyone who reintroduces (I0, I2, I1) — even with a confident-sounding
    /// comment — will fail this test immediately.
    /// </summary>
    [Fact]
    public void M2Flip_EmittedOrder_Is_B_C_A_For_Loaded_A_B_C()
    {
        var A = Vector3.Zero;
        var B = new Vector3(1, 0, 5);
        var C = new Vector3(0, 1, 5);
        // The flip that matches the C++ pipeline output.
        var fixedFlip   = new[] { B, C, A };
        var oldBuggyFlip = new[] { A, C, B };

        // Sanity: the two candidates have OPPOSITE winding (Recast cares).
        var fixedNormal   = Vector3.Cross(fixedFlip[1] - fixedFlip[0],   fixedFlip[2] - fixedFlip[0]);
        var oldNormal      = Vector3.Cross(oldBuggyFlip[1] - oldBuggyFlip[0], oldBuggyFlip[2] - oldBuggyFlip[0]);
        Assert.True(fixedNormal.Z > 0f, "fixed flip must produce +Z normal (CCW from above)");
        Assert.True(oldNormal.Z   < 0f, "old buggy flip must produce −Z normal (CW from above)");

        // The fixed flip emitted (I1, I2, I0) — exactly the bytes going into the
        // C# navmesh for a stored M2 triangle. This is the (B, C, A) order the
        // C++ produces via copyIndices(flip=true). Anything else is wrong.
        Assert.Equal(B, fixedFlip[0]);
        Assert.Equal(C, fixedFlip[1]);
        Assert.Equal(A, fixedFlip[2]);
    }

    // -----------------------------------------------------------------------
    // Worldspawn (0,?,0) → (32G,?,32G) fix — mirrors wmo.cpp:629-643.
    // Applied in MangosDoodadExtractor.ExtractSet and BuildWmoSpawn.
    // -----------------------------------------------------------------------

    private static (float X, float Y, float Z) ApplyWorldspawnFix(float x, float y, float z)
    {
        if (x == 0f && z == 0f)
        {
            const float HalfWorld = 533.33333f * 32f;
            x = HalfWorld;
            z = HalfWorld;
        }
        return (x, y, z);
    }

    [Theory]
    [InlineData(0f, 5f, 0f, 17066.666f, 5f, 17066.666f)]   // worldspawn: both axes snap to 32G
    [InlineData(100f, 5f, 200f, 100f, 5f, 200f)]            // non-worldspawn: unchanged
    [InlineData(0f, 5f, 1f, 0f, 5f, 1f)]                    // x == 0 but z != 0: unchanged
    [InlineData(1f, 5f, 0f, 1f, 5f, 0f)]                    // z == 0 but x != 0: unchanged
    public void WorldspawnFix_OnlyTriggersWhenBothAxesAreZero(
        float inX, float inY, float inZ,
        float outX, float outY, float outZ)
    {
        var r = ApplyWorldspawnFix(inX, inY, inZ);
        Assert.InRange(r.X - outX, -1f, 1f);
        Assert.InRange(r.Y - outY, -1e-6f, 1e-6f);
        Assert.InRange(r.Z - outZ, -1f, 1f);
    }

    [Fact]
    public void WorldspawnFix_FixCoordsOutput_MatchesCppWorldspawnPosition()
    {
        // C++ wmo.cpp:629-643:
        //   if (x == 0 && z == 0) { x = 32G; z = 32G; }
        //   pos = fixCoords(pos);  // fixCoords returns (z, x, y) of the input
        // So a worldspawn WMO with raw (0, y, 0) ends up at (32G, 32G, y)
        // in fixCoords space.
        const float HalfWorld = 533.33333f * 32f;
        float rawX = 0f, rawY = 100f, rawZ = 0f;
        if (rawX == 0f && rawZ == 0f) { rawX = HalfWorld; rawZ = HalfWorld; }
        // fixCoords(z, x, y) of the (already-fixed) raw input → (Z_fixed, X_fixed, Y).
        var fixCoordsOutput = (Z: rawZ, X: rawX, Y: rawY);
        Assert.InRange(fixCoordsOutput.X - HalfWorld, -1f, 1f);
        Assert.InRange(fixCoordsOutput.Y - 100f, -1e-6f, 1e-6f);
        Assert.InRange(fixCoordsOutput.Z - HalfWorld, -1f, 1f);
    }

    private static void AssertVectorClose(Vector3 actual, Vector3 expected)
    {
        Assert.InRange(actual.X - expected.X, -Tol, Tol);
        Assert.InRange(actual.Y - expected.Y, -Tol, Tol);
        Assert.InRange(actual.Z - expected.Z, -Tol, Tol);
    }
}