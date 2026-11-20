using System.Numerics;

namespace TuckPane.Core;

internal static class ItemMotionMath
{
    // Beyond e^-50 (~1.9e-22, well under IEEE-754 double epsilon), any dissipative
    // spring has analytically converged and snapping avoids Inf * 0 -> NaN on huge dt.
    private const double AsymptoticExponentCutoff = 50.0;

    internal static Vector3 RedirectVelocity(Vector3 current, Vector3 target, Vector3 velocity, float maximumSpeed)
    {
        if (!float.IsFinite(maximumSpeed) || maximumSpeed <= 0f ||
            !IsFinite(current) || !IsFinite(target) || !IsFinite(velocity))
            return Vector3.Zero;

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
        if (!IsFinite(target)) return true;
        if (!IsFinite(value))
        {
            value = target;
            velocity = Vector3.Zero;
            return true;
        }
        if (!IsFinite(velocity)) velocity = Vector3.Zero;
        if (!double.IsFinite(seconds) || seconds <= 0)
            return IsSettled(value, velocity, target, positionToleranceSquared, velocityToleranceSquared);

        double clampedResponse = double.IsFinite(responseSeconds) ? Math.Max(0.02, responseSeconds) : .14;
        double omega = 2.0 * Math.PI / clampedResponse;
        double omegaDt = omega * seconds;
        if (omegaDt >= AsymptoticExponentCutoff)
        {
            value = target;
            velocity = Vector3.Zero;
            return true;
        }

        double decay = Math.Exp(-omegaDt);
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
    /// (60-360Hz) and frame-spike recovery, preserving continuous momentum across
    /// mid-flight target retargeting with zero overshoot explosion or NaN.
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
        if (!IsFinite(target)) return true;
        if (!IsFinite(value))
        {
            value = target;
            velocity = Vector3.Zero;
            return true;
        }
        if (!IsFinite(velocity)) velocity = Vector3.Zero;
        if (!double.IsFinite(seconds) || seconds <= 0)
            return IsSettled(value, velocity, target, positionToleranceSquared, velocityToleranceSquared);

        double clampedResponse = double.IsFinite(responseSeconds) ? Math.Max(0.05, responseSeconds) : 0.28;
        double zeta = double.IsFinite(dampingFraction) ? Math.Clamp(dampingFraction, 0.45, 1.2) : 0.85;
        if (Math.Abs(zeta - 1.0) < 0.001)
            return StepCriticalSpring(ref value, ref velocity, target, seconds, clampedResponse, positionToleranceSquared, velocityToleranceSquared);

        double omega = 2.0 * Math.PI / clampedResponse;
        if (zeta < 1.0)
        {
            double zetaOmega = zeta * omega;
            double decayArg = zetaOmega * seconds;
            if (decayArg >= AsymptoticExponentCutoff)
            {
                value = target;
                velocity = Vector3.Zero;
                return true;
            }
            double omegaD = omega * Math.Sqrt(1.0 - zeta * zeta);
            double invOmegaD = 1.0 / omegaD;
            double omegaSq = omega * omega;
            double expTerm = Math.Exp(-decayArg);
            (double sinTerm, double cosTerm) = Math.SinCos(omegaD * seconds);
            StepIosUnderdampedComponent(ref value.X, ref velocity.X, target.X, zetaOmega, omegaSq, invOmegaD, expTerm, cosTerm, sinTerm);
            StepIosUnderdampedComponent(ref value.Y, ref velocity.Y, target.Y, zetaOmega, omegaSq, invOmegaD, expTerm, cosTerm, sinTerm);
            StepIosUnderdampedComponent(ref value.Z, ref velocity.Z, target.Z, zetaOmega, omegaSq, invOmegaD, expTerm, cosTerm, sinTerm);
        }
        else
        {
            double sq = Math.Sqrt(zeta * zeta - 1.0);
            double r1 = -omega * (zeta - sq);
            double r2 = -omega * (zeta + sq);
            if (-r1 * seconds >= AsymptoticExponentCutoff)
            {
                value = target;
                velocity = Vector3.Zero;
                return true;
            }
            double e1 = Math.Exp(r1 * seconds);
            double e2 = Math.Exp(r2 * seconds);
            double invDiff = 1.0 / (r2 - r1);
            StepIosOverdampedComponent(ref value.X, ref velocity.X, target.X, r1, r2, e1, e2, invDiff);
            StepIosOverdampedComponent(ref value.Y, ref velocity.Y, target.Y, r1, r2, e1, e2, invDiff);
            StepIosOverdampedComponent(ref value.Z, ref velocity.Z, target.Z, r1, r2, e1, e2, invDiff);
        }

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
        if (!float.IsFinite(target)) return value;
        if (!float.IsFinite(value))
        {
            value = target;
            velocity = 0;
            return value;
        }
        if (!float.IsFinite(velocity)) velocity = 0;
        if (!double.IsFinite(seconds) || seconds <= 0) return value;

        double clampedResponse = double.IsFinite(responseSeconds) ? Math.Max(0.05, responseSeconds) : 0.22;
        double omega = 2.0 * Math.PI / clampedResponse;
        double zeta = double.IsFinite(dampingFraction) ? Math.Clamp(dampingFraction, 0.45, 1.2) : 0.82;
        if (Math.Abs(zeta - 1.0) < 0.001)
        {
            double omegaDt = omega * seconds;
            if (omegaDt >= AsymptoticExponentCutoff)
            {
                value = target;
                velocity = 0;
                return value;
            }
            StepComponent(ref value, ref velocity, target, seconds, omega, Math.Exp(-omegaDt));
        }
        else if (zeta < 1.0)
        {
            double zetaOmega = zeta * omega;
            double decayArg = zetaOmega * seconds;
            if (decayArg >= AsymptoticExponentCutoff)
            {
                value = target;
                velocity = 0;
                return value;
            }
            double omegaD = omega * Math.Sqrt(1.0 - zeta * zeta);
            double expTerm = Math.Exp(-decayArg);
            (double sinTerm, double cosTerm) = Math.SinCos(omegaD * seconds);
            StepIosUnderdampedComponent(ref value, ref velocity, target, zetaOmega, omega * omega, 1.0 / omegaD, expTerm, cosTerm, sinTerm);
        }
        else
        {
            double sq = Math.Sqrt(zeta * zeta - 1.0);
            double r1 = -omega * (zeta - sq);
            double r2 = -omega * (zeta + sq);
            if (-r1 * seconds >= AsymptoticExponentCutoff)
            {
                value = target;
                velocity = 0;
                return value;
            }
            StepIosOverdampedComponent(ref value, ref velocity, target, r1, r2, Math.Exp(r1 * seconds), Math.Exp(r2 * seconds), 1.0 / (r2 - r1));
        }

        if (Math.Abs(value - target) < 0.02f && Math.Abs(velocity) < 0.05f)
        {
            value = target;
            velocity = 0;
        }
        return value;
    }

    private static void StepIosUnderdampedComponent(
        ref float value,
        ref float velocity,
        float target,
        double zetaOmega,
        double omegaSq,
        double invOmegaD,
        double expTerm,
        double cosTerm,
        double sinTerm)
    {
        // Underdamped harmonic oscillator (signature iOS silky bounce).
        // Using v0 directly on cosTerm avoids catastrophic cancellation at 240-360Hz micro-dt.
        double x0 = value - target;
        double v0 = velocity;
        double c2 = (v0 + zetaOmega * x0) * invOmegaD;
        double nextX = expTerm * (x0 * cosTerm + c2 * sinTerm);
        double nextV = expTerm * (v0 * cosTerm - (zetaOmega * v0 + omegaSq * x0) * invOmegaD * sinTerm);
        value = (float)(target + nextX);
        velocity = (float)nextV;
    }

    private static void StepIosOverdampedComponent(
        ref float value,
        ref float velocity,
        float target,
        double r1,
        double r2,
        double e1,
        double e2,
        double invDiff)
    {
        // Overdamped exponential decay.
        double x0 = value - target;
        double v0 = velocity;
        double c2 = (v0 - r1 * x0) * invDiff;
        double c1 = x0 - c2;
        value = (float)(target + c1 * e1 + c2 * e2);
        velocity = (float)(c1 * r1 * e1 + c2 * r2 * e2);
    }

    private static void StepComponent(ref float value, ref float velocity, float target, double seconds, double omega, double decay)
    {
        double displacement = value - target;
        double coefficient = velocity + omega * displacement;
        value = (float)(target + (displacement + coefficient * seconds) * decay);
        velocity = (float)((velocity - omega * coefficient * seconds) * decay);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsSettled(Vector3 value, Vector3 velocity, Vector3 target,
        float positionToleranceSquared, float velocityToleranceSquared) =>
        Vector3.DistanceSquared(value, target) < positionToleranceSquared &&
        velocity.LengthSquared() < velocityToleranceSquared;
}
