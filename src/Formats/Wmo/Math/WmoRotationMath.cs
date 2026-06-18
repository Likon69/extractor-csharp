using System.Numerics;

namespace MaNGOS.Extractor.Formats.Wmo.Math;

/// <summary>
/// 3D math helpers used by MaNGOS vmap-extractor's WMO doodad extraction
/// (vec3d.h::Mat3, vec3d.h::Quat, vec3d.h::Vec3D). The C++ uses a
/// "ZYX-with-axis-swap" Euler convention for WMO/MODF rotations: the WMO
/// rot triplet (rot.x, rot.y, rot.z) is interpreted as (Y-angle, Z-angle,
/// X-angle) when building a rotation matrix. The doodad composition path
/// in Doodad::ExtractSet relies on round-tripping through this exact
/// convention, so any deviation produces dir_bin records that disagree with
/// the C++ reference byte-for-byte.
/// </summary>
public static class WmoRotationMath
{
    private const float DEG2RAD = MathF.PI / 180.0f;
    private const float RAD2DEG = 180.0f / MathF.PI;

    /// <summary>
    /// Build a rotation matrix from the WMO/MODF Euler triplet (rot.x, rot.y, rot.z).
    /// Mirrors MaNGOS vec3d.h::Mat3::fromWMORot exactly.
    /// </summary>
    /// <remarks>
    /// C++ convention: <c>rot.y</c> is the Z-axis (yaw), <c>rot.x</c> is the
    /// Y-axis (pitch), <c>rot.z</c> is the X-axis (roll). The matrix is the
    /// product Rz * Ry * Rx.
    /// </remarks>
    public static Matrix4x4 FromWmoRot(float rotX, float rotY, float rotZ)
    {
        float z = rotY * DEG2RAD; // rot.y → Z (yaw)
        float y = rotX * DEG2RAD; // rot.x → Y (pitch)
        float x = rotZ * DEG2RAD; // rot.z → X (roll)

        float cz = MathF.Cos(z), sz = MathF.Sin(z);
        float cy = MathF.Cos(y), sy = MathF.Sin(y);
        float cx = MathF.Cos(x), sx = MathF.Sin(x);

        // R = Rz * Ry * Rx, expanded.
        return new Matrix4x4(
            cz * cy,
            -sz * cy,
            sy, 0,

            cz * sy * sx + sz * cx,
            -sz * sy * sx + cz * cx,
            -cy * sx, 0,

            -cz * sy * cx + sz * sx,
            sz * sy * cx + cz * sx,
            cy * cx, 0,

            0, 0, 0, 1
        );
    }

    /// <summary>
    /// Convert a rotation matrix back to (rotX, rotY, rotZ) Euler triplet in
    /// degrees, using the same ZYX-with-axis-swap convention as
    /// <see cref="FromWmoRot"/>. Mirrors MaNGOS vec3d.h::Mat3::toWMORot.
    /// </summary>
    public static (float RotX, float RotY, float RotZ) ToWmoRot(Matrix4x4 m)
    {
        float sy = m.M21; // m[0][2]
        float y = MathF.Asin(MathF.Max(-1.0f, MathF.Min(1.0f, sy)));
        float cy = MathF.Cos(y);

        float z, x;
        if (MathF.Abs(cy) > 1e-6f)
        {
            z = MathF.Atan2(-m.M12, m.M11); // -m[0][1], m[0][0]
            x = MathF.Atan2(-m.M32, m.M33); // -m[1][2], m[2][2]
        }
        else
        {
            // Gimbal lock: y ~ +/- pi/2.
            z = MathF.Atan2(m.M13, m.M14);   // m[1][0], m[1][1]
            x = 0.0f;
        }

        return (y * RAD2DEG, z * RAD2DEG, x * RAD2DEG);
    }

    /// <summary>
    /// Build a rotation matrix from a unit quaternion (x, y, z, w).
    /// Mirrors MaNGOS vec3d.h::quatToMat3 (model.cpp:486).
    /// </summary>
    public static Matrix4x4 QuatToMat3(float qx, float qy, float qz, float qw)
    {
        float xx = qx * qx, yy = qy * qy, zz = qz * qz;
        float xy = qx * qy, xz = qx * qz, yz = qy * qz;
        float wx = qw * qx, wy = qw * qy, wz = qw * qz;

        return new Matrix4x4(
            1.0f - 2.0f * (yy + zz),
            2.0f * (xy - wz),
            2.0f * (xz + wy), 0,

            2.0f * (xy + wz),
            1.0f - 2.0f * (xx + zz),
            2.0f * (yz - wx), 0,

            2.0f * (xz - wy),
            2.0f * (yz + wx),
            1.0f - 2.0f * (xx + yy), 0,

            0, 0, 0, 1
        );
    }

    /// <summary>Matrix * vector (MaNGOS vec3d.h::Mat3::operator*).</summary>
    public static Vector3 Mul(Matrix4x4 m, Vector3 v)
    {
        return new Vector3(
            m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z,
            m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z,
            m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z
        );
    }

    /// <summary>Matrix * matrix (MaNGOS vec3d.h::Mat3::operator*).</summary>
    public static Matrix4x4 Mul(Matrix4x4 a, Matrix4x4 b)
    {
        var r = new Matrix4x4();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
        return r;
    }
}
