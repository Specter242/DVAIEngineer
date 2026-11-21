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
                return $"steam is down to {r.Pressure:F1} bar: with none left the engineer stops and waits for you";
            }
            return null;
        }
    }
}
