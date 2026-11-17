using System;
using System.Collections.Generic;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Driving
{
    /// <summary>A speed limit over a stretch of a leg, by the leading end's distance along it.</summary>
    public readonly struct SpeedZone
    {
        public readonly double From;
        public readonly double To;
        public readonly double Limit;

        public SpeedZone(double from, double to, double limit)
        {
            From = from;
            To = to;
            Limit = limit;
        }

        public override string ToString() => $"{Limit:F1} m/s from {From:F0} to {To:F0} m";
    }

    /// <summary>How hard the planner may count on braking, and how soon.</summary>
    public readonly struct BrakingModel
    {
        /// <summary>The deceleration planned for, in m/s²: well short of what the brakes can do.</summary>
        public readonly double Deceleration;

        /// <summary>The time between deciding to brake and the brakes biting, in seconds.</summary>
        public readonly double Delay;

        public BrakingModel(double deceleration, double delay)
        {
            if (!(deceleration > 0))
                throw new ArgumentOutOfRangeException(nameof(deceleration));
            Deceleration = deceleration;
            Delay = Math.Max(0, delay);
        }

        /// <summary>
        /// The highest speed now from which the train can be down to
        /// <paramref name="limit"/> in <paramref name="distance"/> meters:
        /// v·delay + (v² − limit²)/(2a) = distance, solved for v.
        /// </summary>
        public double SpeedToMeet(double limit, double distance)
        {
            if (distance <= 0)
                return limit;
            var a = Deceleration;
            var at = a * Delay;
            return -at + Math.Sqrt(at * at + limit * limit + 2 * a * distance);
        }

        /// <summary>The distance needed to slow from <paramref name="speed"/> to <paramref name="limit"/>.</summary>
        public double DistanceToSlow(double speed, double limit)
        {
            if (speed <= limit)
                return 0;
            return speed * Delay + (speed * speed - limit * limit) / (2 * Deceleration);
        }
    }

    /// <summary>
    /// The speed a train should run at along a leg: the lowest of the zone
    /// it is in (held until its rear clears the zone) and the braking curve
    /// to every slower zone and stop ahead.
    /// </summary>
    public sealed class SpeedPlanner
    {
        private readonly List<SpeedZone> zones = new List<SpeedZone>();

        /// <summary>The leg's zones, built from its tracks' limits, each held for the train's length past its end.</summary>
        public IReadOnlyList<SpeedZone> Zones => zones;

        public static SpeedPlanner ForLeg(TrackGraph graph, RouteLeg leg, double trainLength, double junctionLimit)
        {
            var planner = new SpeedPlanner();
            for (var i = 0; i < leg.Pieces.Count; i++)
            {
                var piece = leg.Pieces[i];
                var start = leg.PieceStart(i);
                var profile = graph.Limits(piece.Track);
                for (var k = 0; k < profile.Count; k++)
                {
                    var zoneLo = profile.OffsetAt(k);
                    var zoneHi = k + 1 < profile.Count ? profile.OffsetAt(k + 1) : graph.Length(piece.Track);
                    var speed = profile.SpeedAt(k);
                    if (double.IsPositiveInfinity(speed))
                        continue;
                    // The overlap of this zone with the piece, as leg distances.
                    var lo = Math.Max(zoneLo, Math.Min(piece.From, piece.To));
                    var hi = Math.Min(zoneHi, Math.Max(piece.From, piece.To));
                    if (hi <= lo)
                        continue;
                    var a = start + (piece.Direction == TrackDirection.Forward ? lo - piece.From : piece.From - hi);
                    var b = a + (hi - lo);
                    planner.Add(new SpeedZone(a, b + trainLength, speed));
                }
            }
            if (junctionLimit > 0 && !double.IsPositiveInfinity(junctionLimit))
            {
                foreach (var sw in leg.Switches)
                {
                    // Through the switch's points and frog: about 30 m of turnout.
                    planner.Add(new SpeedZone(sw.AtDistance - 2, sw.AtDistance + 30 + trainLength, junctionLimit));
                }
            }
            return planner;
        }

        public void Add(SpeedZone zone)
        {
            if (zone.To > zone.From && zone.Limit >= 0)
                zones.Add(zone);
        }

        /// <summary>
        /// The target speed at <paramref name="position"/>, capped at
        /// <paramref name="maxSpeed"/>, with the train to be stopped at
        /// <paramref name="stopAt"/> (the leg's end, an obstacle, or a
        /// switch that can't be set).
        /// </summary>
        public double TargetSpeed(double position, double maxSpeed, double stopAt, BrakingModel braking)
        {
            var target = maxSpeed;
            target = Math.Min(target, braking.SpeedToMeet(0, stopAt - position));
            foreach (var z in zones)
            {
                if (z.To <= position)
                    continue;
                if (z.From <= position)
                    target = Math.Min(target, z.Limit);
                else if (z.Limit < target)
                    target = Math.Min(target, braking.SpeedToMeet(z.Limit, z.From - position));
            }
            return Math.Max(0, target);
        }

        /// <summary>The limit in force at a position (the zone's own, not the braking curve's).</summary>
        public double LimitAt(double position, double maxSpeed)
        {
            var limit = maxSpeed;
            foreach (var z in zones)
            {
                if (z.From <= position && z.To > position)
                    limit = Math.Min(limit, z.Limit);
            }
            return limit;
        }
    }
}
