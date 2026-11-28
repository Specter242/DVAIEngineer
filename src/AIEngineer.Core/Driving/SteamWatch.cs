using System;

namespace AIEngineer.Core.Driving
{
    /// <summary>A steam loco's boiler and fire as they read now.</summary>
    public struct BoilerReading
    {
        /// <summary>Boiler pressure, bar, as the engine's readout gives it.</summary>
        public double Pressure;

        /// <summary>The pressure wanted before the train sets off, bar.</summary>
        public double StartPressure;

        /// <summary>The water in the gauge glass, 0 to 1; NaN when it can't be told.</summary>
        public double Glass;

        /// <summary>Whether the fire is lit; null when it can't be told.</summary>
        public bool? FireLit;
    }

    /// <summary>
    /// What steam lets the engineer do, and what it tells a player who has
    /// taken the firing (the "Fire: you" button). The engineer waits rather
    /// than works a regulator with nothing behind it; with the player firing
    /// it also stops when the water is out of sight, since nobody else is
    /// watching the glass. Each warning is given once, and again only after
    /// things have come right.
    /// </summary>
    public sealed class SteamWatch
    {
        /// <summary>Under this, bar, there is no steam to work with: the train stops, moving or not.</summary>
        public const double NoSteamPressure = 2;

        /// <summary>The steam warning comes back once the pressure is this far over the start pressure, bar.</summary>
        public const double SteamRecovered = 1;

        /// <summary>The glass this low (of 1) draws a warning.</summary>
        public const double LowGlass = 0.3;

        /// <summary>The water warning comes back once the glass is this far over <see cref="LowGlass"/>.</summary>
        public const double GlassRecovered = 0.1;

        /// <summary>The glass this low counts as out of sight, as Realistic Firing's fireman calls it.</summary>
        public const double OutOfSightGlass = 0.1;

        /// <summary>After stopping for water the train goes on once the glass is back to this.</summary>
        public const double BackInSightGlass = 0.2;

        private bool outOfSight, outOfSightTold, steamTold, waterTold, fireTold;

        /// <summary>Whether the player does the firing: fire, water, blower, and damper are theirs.</summary>
        public bool PlayerFires { get; set; }

        /// <summary>Forgets what has been said and seen, for a new turn at the loco.</summary>
        public void Reset()
        {
            outOfSight = outOfSightTold = steamTold = waterTold = fireTold = false;
        }

        /// <summary>
        /// Why the train must stand now, for its status; null when it may go.
        /// <paramref name="lighting"/>: the engineer is lighting the fire itself.
        /// </summary>
        public string Hold(BoilerReading r, bool standing, bool lighting)
        {
            if (PlayerFires)
            {
                if (!double.IsNaN(r.Glass))
                {
                    if (r.Glass <= OutOfSightGlass)
                        outOfSight = true;
                    else if (r.Glass >= BackInSightGlass)
                        outOfSight = false;
                }
                if (outOfSight)
                    return "Water out of sight: waiting for you to fill the boiler";
            }
            else if (lighting && standing)
            {
                return "Lighting the fire";
            }
            if (r.Pressure >= NoSteamPressure && (!standing || r.Pressure >= r.StartPressure))
                return null;
            return PlayerFires
                ? $"Waiting for steam: you're firing ({r.Pressure:F1} of {r.StartPressure:F0} bar)"
                : $"Raising steam: {r.Pressure:F1} of {r.StartPressure:F0} bar";
        }

        /// <summary>The next thing to tell a firing player, at most one a call; null for nothing new.</summary>
        public string NextWarning(BoilerReading r)
        {
            if (!PlayerFires)
                return null;
            if (r.FireLit == true)
                fireTold = false;
            if (r.Pressure >= r.StartPressure + SteamRecovered)
                steamTold = false;
            if (!double.IsNaN(r.Glass) && r.Glass >= LowGlass + GlassRecovered)
                waterTold = false;
            if (!outOfSight)
                outOfSightTold = false;
            if (outOfSight && !outOfSightTold)
            {
                outOfSightTold = waterTold = true;
                return "the water is out of sight in the glass: the engineer is stopping until you fill the boiler";
            }
            if (r.FireLit == false && !fireTold)
            {
                fireTold = true;
                return "the fire is out, and it's yours to light";
            }
            if (!double.IsNaN(r.Glass) && r.Glass <= LowGlass && !waterTold)
            {
                waterTold = true;
                return "the water is low in the glass";
            }
            if (r.Pressure < r.StartPressure && !steamTold)
            {
                steamTold = true;
                return $"steam is down to {r.Pressure:F1} bar: waiting for {r.StartPressure:F0} bar before starting, and with none left the engineer stops";
            }
            return null;
        }
    }

    /// <summary>
    /// Reverser (cutoff) policy for a steam locomotive: full gear to start,
    /// hooked up smoothly as speed builds (lengthened again when pulling hard),
    /// and dropped into the corner in the direction of motion whenever the
    /// regulator is shut so the pistons never pump smokebox gas into the
    /// cylinders (Realistic Firing's <c>Drifting.HookedUp</c>: throttle under
    /// 2 %, faster than 5.5 m/s, reverser under 80 % travel from mid-gear).
    /// </summary>
    public static class SteamGear
    {
        /// <summary>
        /// Regulator below this counts as shut (above Realistic Firing's 0.02
        /// cracked-throttle threshold so the reverser drops into the corner
        /// before drifting wear can begin).
        /// </summary>
        public const double ShutThrottle = 0.025;

        /// <summary>
        /// Once dropped into the corner for drifting, stay in full gear until
        /// the regulator opens past this, avoiding lever chatter when throttle
        /// blips near idle.
        /// </summary>
        public const double RehookThrottle = 0.06;

        /// <summary>Speed above which actual motion overrides the ordered direction for the reverser, m/s.</summary>
        public const double MovingSpeed = 0.3;

        /// <summary>
        /// Desired reverser position (0 full reverse, 0.5 mid-gear, 1 full
        /// forward), or null to leave the reverser alone at a stand with no
        /// ordered direction.
        /// </summary>
        public static double? Reverser(double locoForwardSpeed, double throttle, bool? orderedForward, double currentReverser)
        {
            var speed = Math.Abs(locoForwardSpeed);
            var moving = speed >= MovingSpeed;
            bool forward;
            if (moving)
                forward = locoForwardSpeed > 0;
            else if (orderedForward.HasValue)
                forward = orderedForward.Value;
            else
                return null;

            var opposing = moving && orderedForward.HasValue && orderedForward.Value != forward;
            var currentTravel = Math.Abs(currentReverser - 0.5) * 2.0;

            double travel;
            if (opposing || throttle < ShutThrottle)
                travel = 1.0;
            else if (throttle < RehookThrottle && currentTravel >= 0.85)
                travel = 1.0;
            else
                travel = WorkingTravel(speed, throttle);

            return forward ? 0.5 + 0.5 * travel : 0.5 - 0.5 * travel;
        }

        /// <summary>
        /// Working reverser travel from mid-gear (0 to 1) while under steam:
        /// full gear at a start (up to 2 m/s), hooked up smoothly with speed
        /// for cruising (down to 0.20 at 18 m/s+, ~22 % cutoff), and
        /// lengthened smoothly past 65 % regulator so heavy pulls on grades
        /// keep full power.
        /// </summary>
        public static double WorkingTravel(double absSpeed, double throttle)
        {
            double cruise;
            if (absSpeed <= 2.0)
                cruise = 1.0;
            else if (absSpeed <= 4.0)
                cruise = Lerp(1.0, 0.65, (absSpeed - 2.0) / 2.0);
            else if (absSpeed <= 6.0)
                cruise = Lerp(0.65, 0.45, (absSpeed - 4.0) / 2.0);
            else if (absSpeed <= 8.0)
                cruise = Lerp(0.45, 0.33, (absSpeed - 6.0) / 2.0);
            else if (absSpeed <= 11.0)
                cruise = Lerp(0.33, 0.25, (absSpeed - 8.0) / 3.0);
            else if (absSpeed <= 14.0)
                cruise = Lerp(0.25, 0.22, (absSpeed - 11.0) / 3.0);
            else if (absSpeed <= 18.0)
                cruise = Lerp(0.22, 0.20, (absSpeed - 14.0) / 4.0);
            else
                cruise = 0.20;

            if (throttle <= 0.65 || absSpeed <= 2.0)
                return cruise;
            var hard = Math.Min(1.0, (throttle - 0.65) / 0.35);
            double heavy;
            if (absSpeed <= 5.0)
                heavy = Lerp(1.0, 0.80, (absSpeed - 2.0) / 3.0);
            else if (absSpeed <= 10.0)
                heavy = Lerp(0.80, 0.55, (absSpeed - 5.0) / 5.0);
            else if (absSpeed <= 18.0)
                heavy = Lerp(0.55, 0.40, (absSpeed - 10.0) / 8.0);
            else
                heavy = 0.40;
            return Lerp(cruise, heavy, hard);
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * Math.Max(0.0, Math.Min(1.0, t));
    }
}
