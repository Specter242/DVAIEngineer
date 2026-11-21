using System;
using AIEngineer.Core.Driving;
using UnityEngine;

namespace AIEngineer.Game
{
    internal static class ConductorDesk
    {
        private static readonly Func<int, string> SwitchName = NameOfSwitch;

        /// <summary>The loco of the train the player is aboard; null if none.</summary>
        public static TrainCar PlayerLoco =>
#if !PUBLIC_RELEASE
            LocoForTest != null ? LocoForTest :
#endif
            Host.LocoOf(PlayerManager.Car);

#if !PUBLIC_RELEASE
        /// <summary>The self-test's train, standing in for the one the player is aboard.</summary>
        internal static TrainCar LocoForTest;
#endif

        /// <summary>The session whose conductor has a plan or is at work; null if none has.</summary>
        public static Session Current
        {
            get
            {
                var sessions = Host.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    if (sessions[i].Loco != null && sessions[i].Conductor.State != ConductorState.None)
                        return sessions[i];
                }
                return null;
            }
        }

        /// <summary>Whether the map may offer the conductor a destination now.</summary>
        public static bool CanPlan => PlayerLoco != null && MultiplayerGate.MayDrive;

        /// <summary>
        /// Has the conductor plan a route for the player's train, standing
        /// down any plan it had. Returns why it can't, or null. Nothing on the
        /// railroad changes until <see cref="Execute"/>.
        /// </summary>
        /// <param name="destination">The waypoint, for the train as it stands.</param>
        /// <param name="anywhereOnTrack">Whether the whole train on the destination's track is arrival.</param>
        public static string Plan(Func<TrainState, Waypoint> destination, bool anywhereOnTrack)
        {
            var world = Host.World;
            var loco = PlayerLoco;
            if (world == null)
                return "The world isn't ready yet";
            if (loco == null)
                return "Get aboard your train first";
            if (Host.EngineerOn(loco.trainset))
                return "Hand back the engineer on this train first";
            var session = Host.SessionFor(loco, true);
            if (session == null)
                return "Only the host has a conductor in multiplayer";
            StandDown();
            // The cars are scanned only while someone needs them.
            world.ScanCars();
            var state = session.Read();
            if (!state.OnTrack)
                return "The train isn't on the rails";
            var waypoint = destination(state);
            return session.Conductor.Plan(waypoint, anywhereOnTrack, state, world) ? null : $"No route to {waypoint.Label}";
        }

        /// <summary>The word to go. Returns what the player should be told.</summary>
        public static string Execute()
        {
            var session = Current;
            var world = Host.World;
            if (session == null || world == null)
                return "Nothing is planned";
            if (Host.EngineerOn(session.Loco.trainset))
                return "Hand back the engineer on this train first";
            world.ScanCars();
            switch (session.Conductor.Execute(session.Read(), world))
            {
                case ConductorExecution.Started:
                    return $"Conductor at work, bound for {session.Conductor.Destination.Label}";
                case ConductorExecution.RouteChanged:
                    return "The route has changed: look it over and press Execute again";
                case ConductorExecution.NoRoute:
                    return $"No route to {session.Conductor.Destination.Label} from here";
                default:
                    return "Nothing is planned";
            }
        }

        /// <summary>Stands every conductor down. The switches stay as they lie.</summary>
        public static void StandDown()
        {
            var sessions = Host.Sessions;
            for (var i = 0; i < sessions.Count; i++)
                sessions[i].Conductor.StandDown();
        }

        /// <summary>A call in the player's words.</summary>
        public static string Say(Conductor conductor, ConductorCall call) =>
            call.Say(SwitchName, conductor.Destination?.Label);

        private static string NameOfSwitch(int junction)
        {
            var junctions = Host.Network?.Junctions;
            var j = junctions != null && junction >= 0 && junction < junctions.Length ? junctions[junction] : null;
            return j != null ? j.junctionData.junctionIdLong : "a switch";
        }
    }
}
