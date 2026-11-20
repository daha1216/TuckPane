using System.Numerics;

namespace TuckPane.Core;

internal static class ItemMotionMath
{
    internal static Vector3 RedirectVelocity(Vector3 current, Vector3 target, Vector3 velocity, float maximumSpeed)
    {
        Vector3 remaining = target - current;
        if (remaining.LengthSquared() < .0001f) return Vector3.Zero;

        Vector3 direction = Vector3.Normalize(remaining);
        float forwardSpeed = Math.Clamp(Vector3.Dot(velocity, direction), 0, maximumSpeed);
        return direction * forwardSpeed;
    }

    internal static bool StepCriticalSpring(
        ref Vector3 value,
        ref Vector3 velocity,
        Vector3 target,
        double seconds,
        double responseSeconds = .14,
        float positionToleranceSquared = .0004f,
        float velocityToleranceSquared = .01f)
    {
        if (seconds <= 0) return IsSettled(value, velocity, target, positionToleranceSquared, velocityToleranceSquared);

        double omega = 2 * Math.PI / responseSeconds;
        double decay = Math.Exp(-omega * seconds);
        StepComponent(ref value.X, ref velocity.X, target.X, seconds, omega, decay);
        StepComponent(ref value.Y, ref velocity.Y, target.Y, seconds, omega, decay);
        StepComponent(ref value.Z, ref velocity.Z, target.Z, seconds, omega, decay);
        if (!IsSettled(value, velocity, target, positionToleranceSquared, velocityToleranceSquared)) return false;

        value = target;
        velocity = Vector3.Zero;
        return true;
    }

    /// <summary>
    /// Exact closed-form analytical Apple-style spring integrator (UIKit / SwiftUI
    /// spring(response, dampingFraction)). Unconditionally stable at any frame rate
    /// (60-240Hz) and preserves continuous momentum across mid-flight target retargeting.
    /// </summary>
    internal static bool StepIosSpring(
        ref Vector3 value,
        ref Vector3 velocity,
        Vector3 target,
        double seconds,
        double responseSeconds = 0.28,
        double dampingFraction = 0.85,
        float positionToleranceSquared = 0.01f,
        float velocityToleranceSquared = 0.04f)
    {
        if (seconds <= 0)
            return IsSettled(value, velocity, target, positionToleranceSquared, velocityToleranceSquared);

        double clampedResponse = Math.Max(0.05, responseSeconds);
        double zeta = Math.Clamp(dampingFraction, 0.45, 1.2);
        if (Math.Abs(zeta - 1.0) < 0.001)
            return StepCriticalSpring(ref value, ref velocity, target, seconds, clampedResponse, positionToleranceSquared, velocityToleranceSquared);

        double omega = 2.0 * Math.PI / clampedResponse;
        StepIosComponent(ref value.X, ref velocity.X, target.X, seconds, omega, zeta);
        StepIosComponent(ref value.Y, ref velocity.Y, target.Y, seconds, omega, zeta);
        StepIosComponent(ref value.Z, ref velocity.Z, target.Z, seconds, omega, zeta);

        if (!IsSettled(value, velocity, target, positionToleranceSquared, velocityToleranceSquared))
            return false;

        value = target;
        velocity = Vector3.Zero;
        return true;
    }

    internal static float StepScalarSpring(
        ref float value,
        ref float velocity,
        float target,
        double seconds,
        double responseSeconds = 0.22,
        double dampingFraction = 0.82)
    {
        if (seconds <= 0) return value;
        double omega = 2.0 * Math.PI / Math.Max(0.05, responseSeconds);
        double zeta = Math.Clamp(dampingFraction, 0.45, 0.999);
        StepIosComponent(ref value, ref velocity, target, seconds, omega, zeta);
        if (Math.Abs(value - target) < 0.02f && Math.Abs(velocity) < 0.05f)
        {
            value = target;
            velocity = 0;
        }
        return value;
    }

    private static void StepIosComponent(
        ref float value,
        ref float velocity,
        float target,
        double dt,
        double omega,
        double zeta)
    {
        double x0 = value - target;
        double v0 = velocity;
        if (zeta < 1.0)
        {
            // Underdamped harmonic oscillator (signature iOS silky bounce).
            double omegaD = omega * Math.Sqrt(1.0 - zeta * zeta);
            double expTerm = Math.Exp(-zeta * omega * dt);
            double cosTerm = Math.Cos(omegaD * dt);
            double sinTerm = Math.Sin(omegaD * dt);
            double c1 = x0;
            double c2 = (v0 + zeta * omega * x0) / omegaD;
            double nextX = expTerm * (c1 * cosTerm + c2 * sinTerm);
            double nextV = expTerm * (
                (-zeta * omega * c1 + omegaD * c2) * cosTerm +
                (-zeta * omega * c2 - omegaD * c1) * sinTerm);
            value = (float)(target + nextX);
            velocity = (float)nextV;
        }
        else
        {
            // Overdamped exponential decay.
            double sq = Math.Sqrt(zeta * zeta - 1.0);
            double r1 = -omega * (zeta - sq);
            double r2 = -omega * (zeta + sq);
            double c2 = (v0 - r1 * x0) / (r2 - r1);
            double c1 = x0 - c2;
            double e1 = Math.Exp(r1 * dt);
            double e2 = Math.Exp(r2 * dt);
            value = (float)(target + c1 * e1 + c2 * e2);
            velocity = (float)(c1 * r1 * e1 + c2 * r2 * e2);
        }
    }

    private static void StepComponent(ref float value, ref float velocity, float target, double seconds, double omega, double decay)
    {
        double displacement = value - target;
        double coefficient = velocity + omega * displacement;
        value = (float)(target + (displacement + coefficient * seconds) * decay);
        velocity = (float)((velocity - omega * coefficient * seconds) * decay);
    }

    private static bool IsSettled(Vector3 value, Vector3 velocity, Vector3 target,
        float positionToleranceSquared, float velocityToleranceSquared) =>
        Vector3.DistanceSquared(value, target) < positionToleranceSquared &&
        velocity.LengthSquared() < velocityToleranceSquared;
}
