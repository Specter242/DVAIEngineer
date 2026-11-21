using System;
using System.Collections.Generic;

namespace AIEngineer.Core.Driving
{
    /// <summary>What part of a train rides with its loco through a job run.</summary>
    public static class TrainMakeup
    {
        /// <summary>
        /// Of a train's cars listed from its far end in to the loco, how many
        /// at the loco's end ride with it: its power (a second unit, a slug,
        /// a tender) and a caboose (mredinius, 2026-11-20: "I want to take my
        /// caboose"), coupled next to the loco or to those. They are never set
        /// aside, put back, or cut off as strangers; a caboose anywhere else
        /// in the train is a car like the rest.
        /// </summary>
        public static int RidingWithLoco<T>(IReadOnlyList<T> farToNear, Func<T, bool> rides)
        {
            var n = 0;
            for (var i = farToNear.Count - 1; i >= 0 && rides(farToNear[i]); i--)
                n++;
            return n;
        }
    }
}
