namespace AIEngineer.Core.Tests;

/// <summary>
/// A point-mass train for exercising the speed controller: traction
/// limited by adhesion and power, an air brake whose cylinders follow the
/// handle with a lag (slower to release than to apply), a loco brake, and
/// rolling resistance. Not Derail Valley's model; close enough in kind
/// to show a controller that hunts, overshoots, or stops short.
/// </summary>
public sealed class SimTrain
{
    public double Mass = 400_000;          // kg: a DE6 and six loaded cars
    public double MaxTractiveForce = 250_000; // N
    public double Power = 1_500_000;       // W at the rail
    public double TrainBrakeDecel = 0.7;   // m/s² at full service
    public double LocoBrakeDecel = 0.15;   // m/s² from the loco alone, whole train
    public double ApplyLag = 3;            // s
    public double ReleaseLag = 8;          // s
    public double Grade = 0;               // rise over run, positive uphill

    // American equipment's direct release (Realistic Brakes' AB valve): any easing of
    // the handle releases the cars all the way, and they recharge only while released,
    // so an application made soon after a release is weaker.
    public bool DirectRelease;
    public double RechargeTime = 90;       // s, from empty to full while released
    public double Charge = 1;              // 0..1: what the reservoirs can give
    public int EaseOffsWhileMoving;        // handle moves that released a moving train
    public readonly System.Collections.Generic.List<(double Position, double Speed)> EaseOffs = new();
    private double lastHandle;
    private bool released = true;

    public double Speed;                   // m/s, positive forward
    public double Position;                // m
    public double TrainBrake;              // cylinder level 0..1
    public double LocoBrake;               // 0..1

    public void Step(double throttle, double trainBrakeHandle, double locoBrakeHandle, double dt)
    {
        var cylinderTarget = trainBrakeHandle;
        if (DirectRelease)
        {
            if (trainBrakeHandle < lastHandle - 1e-3)
            {
                if (!released && System.Math.Abs(Speed) > 0.5)
                {
                    EaseOffsWhileMoving++;
                    EaseOffs.Add((Position, Speed));
                }
                released = true;
            }
            else if (trainBrakeHandle > lastHandle + 1e-3)
            {
                // Each reduction draws on the reservoirs.
                if (released)
                    released = false;
                Charge = System.Math.Max(0, Charge - 0.3 * (trainBrakeHandle - lastHandle));
            }
            lastHandle = trainBrakeHandle;
            if (released || trainBrakeHandle <= 1e-3)
            {
                cylinderTarget = 0;
                released = true;
                if (TrainBrake < 0.05)
                    Charge = System.Math.Min(1, Charge + dt / RechargeTime);
            }
            else
            {
                cylinderTarget = trainBrakeHandle * System.Math.Min(1, 0.4 + Charge);
            }
        }
        TrainBrake += (cylinderTarget - TrainBrake) * dt / (cylinderTarget > TrainBrake ? ApplyLag : ReleaseLag);
        LocoBrake += (locoBrakeHandle - LocoBrake) * dt / 1.0;
        var traction = throttle * System.Math.Min(MaxTractiveForce, Power / System.Math.Max(System.Math.Abs(Speed), 0.5)) / Mass;
        var brake = TrainBrake * TrainBrakeDecel + LocoBrake * LocoBrakeDecel;
        var resistance = 0.008 + 0.00006 * Speed * Speed;
        var gravity = 9.81 * Grade;
        var accel = traction - gravity;
        // Brakes and resistance oppose motion, and can't drive the train backward.
        var opposing = brake + resistance;
        if (System.Math.Abs(Speed) < 1e-3 && System.Math.Abs(accel) <= opposing)
        {
            Speed = 0;
            return;
        }
        accel -= System.Math.Sign(Speed == 0 ? accel : Speed) * opposing;
        var next = Speed + accel * dt;
        if (Speed != 0 && System.Math.Sign(next) != System.Math.Sign(Speed) && System.Math.Abs(traction - gravity) <= opposing)
            next = 0;
        Position += (Speed + next) / 2 * dt;
        Speed = next;
    }
}
