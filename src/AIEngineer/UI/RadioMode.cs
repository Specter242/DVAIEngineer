using System;
using System.Collections.Generic;
using System.Reflection;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Tracks;
using AIEngineer.Game;
using DV;
using HarmonyLib;
using UnityEngine;

namespace AIEngineer.UI
{
    internal static class Radio
    {
        private static readonly FieldInfo AllModes = AccessTools.Field(typeof(CommsRadioController), "allModes");

        public static void InjectAll()
        {
            if (AllModes == null)
                return;
            foreach (var controller in Resources.FindObjectsOfTypeAll<CommsRadioController>())
            {
                if (controller != null && controller.gameObject.scene.IsValid())
                    Inject(controller);
            }
        }

        public static void Inject(CommsRadioController controller)
        {
            if (AllModes == null || !(AllModes.GetValue(controller) is List<ICommsRadioMode> modes))
                return;
            foreach (var m in modes)
            {
                if (m is EngineerRadioMode)
                    return;
            }
            modes.Add(new EngineerRadioMode(controller));
            Main.Log($"Comms Radio: AI engineer mode added to '{controller.name}'");
        }
    }

    /// <summary>Adds the mode as each radio wakes, before the radio hands its modes their aim (flat-screen play aims from the camera).</summary>
    [HarmonyPatch(typeof(CommsRadioController), "Awake")]
    internal static class RadioAwakePatch
    {
        private static void Postfix(CommsRadioController __instance)
        {
            try
            {
                Radio.Inject(__instance);
            }
            catch (Exception e)
            {
                Main.Error("Couldn't add the AI engineer radio mode", e);
            }
        }
    }

    internal sealed class EngineerRadioMode : ICommsRadioMode
    {
        private enum Order
        {
            Go,
            Queue,
            CoupleGo,
            CoupleQueue,
            Leave1,
            Leave2,
            Leave3,
            Leave5,
            StopEngineer,
            Back,
        }

        private static readonly Color PickLaser = new Color(0.35f, 1f, 0.5f, 1f);
        private static readonly Color TrackLaser = new Color(1f, 0.8f, 0.2f, 1f);
        private static readonly int OrderCount = Enum.GetValues(typeof(Order)).Length;

        private readonly CommsRadioController controller;
        private Transform signalOrigin;
        private int carMask;
        private int groundMask;
        private Session session;
        private Order order;
        private TrainCar pointedCar;
        private TrackPosition? pointedTrack;
        private bool shownValid;
        private bool shownHasSession;
        private TrainCar shownLoco;
        private string shownLocoId;
        private Order shownOrder;
        private int shownTrack = -1;
        private string shownStatus;
        private float nextRefresh;

        public EngineerRadioMode(CommsRadioController controller) => this.controller = controller;

        /// <summary>Picking a loco, the arrows change radio mode; with one picked, they choose the order.</summary>
        public ButtonBehaviourType ButtonBehaviour => session == null ? ButtonBehaviourType.Regular : ButtonBehaviourType.Override;

        private CommsRadioDisplay Display => controller.deleteControl != null ? controller.deleteControl.display : null;

        private void InvalidateDisplay()
        {
            shownValid = false;
            shownLoco = null;
            shownLocoId = null;
            shownTrack = -1;
            shownStatus = null;
        }

        public void Enable()
        {
            if (session != null && (session.Loco == null || !IsLiveSession(session)))
                session = null;
        }

        public void Disable()
        {
            pointedCar = null;
            pointedTrack = null;
            InvalidateDisplay();
        }

        public void OverrideSignalOrigin(Transform origin) => signalOrigin = origin;

        public void SetStartingDisplay()
        {
            if (session != null && (session.Loco == null || !IsLiveSession(session)))
                session = null;
            InvalidateDisplay();
            Display?.SetDisplay("AI ENGINEER", session == null ? "POINT AT A LOCO" : Describe(), "");
        }

        private static bool IsLiveSession(Session candidate)
        {
            var sessions = Host.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                if (ReferenceEquals(sessions[i], candidate))
                    return true;
            }
            return false;
        }

        public Color GetLaserBeamColor() => session == null ? PickLaser : TrackLaser;

        public bool ButtonACustomAction()
        {
            order = (Order)(((int)order + OrderCount - 1) % OrderCount);
            InvalidateDisplay();
            return true;
        }

        public bool ButtonBCustomAction()
        {
            order = (Order)(((int)order + 1) % OrderCount);
            InvalidateDisplay();
            return true;
        }

        public void OnUse()
        {
            try
            {
                if (session == null || session.Loco == null || !IsLiveSession(session))
                {
                    session = null;
                    var loco = Host.LocoOf(pointedCar);
                    if (loco == null)
                        return;
                    session = Host.SessionFor(loco, true);
                    order = Order.Go;
                    InvalidateDisplay();
                    return;
                }
                switch (order)
                {
                    case Order.Back:
                        session = null;
                        InvalidateDisplay();
                        return;
                    case Order.StopEngineer:
                        session.Stop(secure: true);
                        Main.Notify($"AI engineer: {session.Name} handed back.");
                        return;
                }
                if (!pointedTrack.HasValue)
                    return;
                var network = Host.Network;
                var waypoint = new Waypoint(pointedTrack.Value, ActionOf(order))
                {
                    CarCount = CarsOf(order),
                    Label = network?.Describe(pointedTrack.Value.Track),
                };
                if (order == Order.Go || order == Order.CoupleGo)
                    session.Engineer.GoTo(waypoint);
                else
                    session.Engineer.Enqueue(waypoint);
                Main.Notify($"AI engineer: {session.Name}: {ActionLabel(waypoint)} at {waypoint.Label} ({session.Engineer.Queue.Count} in queue).");
            }
            catch (Exception e)
            {
                Main.Error("AI engineer radio failed", e);
            }
        }

        public void OnUpdate()
        {
            try
            {
                if (carMask == 0)
                {
                    carMask = LayerMask.GetMask("Train_Big_Collider");
                    groundMask = LayerMask.GetMask("Default", "Terrain");
                }
                var origin = signalOrigin != null ? signalOrigin : controller.deleteControl != null ? controller.deleteControl.signalOrigin : null;
                if (origin == null || Time.unscaledTime < nextRefresh)
                    return;
                nextRefresh = Time.unscaledTime + 0.1f;
                if (session != null && (session.Loco == null || !IsLiveSession(session)))
                    session = null;

                var hasSession = session != null;
                TrainCar loco;
                int track;
                string status;
                if (!hasSession)
                {
                    pointedCar = Physics.Raycast(origin.position, origin.forward, out var hit, 100f, carMask)
                        ? TrainCar.Resolve(hit.transform.root)
                        : null;
                    loco = Host.LocoOf(pointedCar);
                    track = -1;
                    status = null;
                }
                else
                {
                    pointedTrack = null;
                    var network = Host.Network;
                    if (network != null && Physics.Raycast(origin.position, origin.forward, out var hit, 150f, groundMask))
                    {
                        var abs = hit.point - WorldMover.currentMove;
                        if (network.TryFindNearest(new Vector2(abs.x, abs.z), 4f, out var p, out _))
                            pointedTrack = p;
                    }
                    loco = session.Loco;
                    track = pointedTrack.HasValue ? pointedTrack.Value.Track : -1;
                    status = session.Engineer.Status;
                }
                var locoId = loco != null ? loco.ID : null;
                if (shownValid && hasSession == shownHasSession && ReferenceEquals(loco, shownLoco)
                    && string.Equals(locoId, shownLocoId, StringComparison.Ordinal)
                    && (!hasSession || (order == shownOrder && track == shownTrack && string.Equals(status, shownStatus, StringComparison.Ordinal))))
                    return;
                shownValid = true;
                shownHasSession = hasSession;
                shownLoco = loco;
                shownLocoId = locoId;
                shownOrder = order;
                shownTrack = track;
                shownStatus = status;

                string text;
                string action;
                if (!hasSession)
                {
                    text = loco == null ? "POINT AT A LOCO" : loco.ID;
                    action = loco == null ? "" : "CLICK: TAKE IT";
                }
                else
                {
                    text = Describe();
                    action = NeedsTrack(order) && !pointedTrack.HasValue ? "POINT AT TRACK" : "CLICK: " + OrderLabel(order);
                }
                Display?.SetContentAndAction(text, action);
            }
            catch (Exception e)
            {
                Main.Error("AI engineer radio failed", e);
                session = null;
                InvalidateDisplay();
            }
        }

        private string Describe()
        {
            var e = session.Engineer;
            var where = pointedTrack.HasValue ? Host.Network?.Describe(pointedTrack.Value.Track) : null;
            return $"{session.Name}: {OrderLabel(order)}\n{(where != null ? "AT " + where : "")}\n{e.Status}";
        }

        private static bool NeedsTrack(Order o) => o != Order.Back && o != Order.StopEngineer;

        private static WaypointAction ActionOf(Order o)
        {
            switch (o)
            {
                case Order.CoupleGo:
                case Order.CoupleQueue:
                    return WaypointAction.Couple;
                case Order.Leave1:
                case Order.Leave2:
                case Order.Leave3:
                case Order.Leave5:
                    return WaypointAction.Uncouple;
                default:
                    return WaypointAction.Stop;
            }
        }

        private static int CarsOf(Order o)
        {
            switch (o)
            {
                case Order.Leave2:
                    return 2;
                case Order.Leave3:
                    return 3;
                case Order.Leave5:
                    return 5;
                default:
                    return 1;
            }
        }

        /// <summary>A waypoint's order in the notice an order gives: "Stop", "Couple", "Leave 2 cars".</summary>
        private static string ActionLabel(Waypoint wp)
        {
            switch (wp.Action)
            {
                case WaypointAction.Couple:
                    return "Couple";
                case WaypointAction.Uncouple:
                    return $"Leave {wp.CarCount} car{(wp.CarCount == 1 ? "" : "s")}";
                default:
                    return "Stop";
            }
        }

        private static string OrderLabel(Order o)
        {
            switch (o)
            {
                case Order.Go:
                    return "GO HERE";
                case Order.Queue:
                    return "ADD STOP";
                case Order.CoupleGo:
                    return "GO COUPLE";
                case Order.CoupleQueue:
                    return "ADD COUPLE";
                case Order.Leave1:
                    return "ADD LEAVE 1";
                case Order.Leave2:
                    return "ADD LEAVE 2";
                case Order.Leave3:
                    return "ADD LEAVE 3";
                case Order.Leave5:
                    return "ADD LEAVE 5";
                case Order.StopEngineer:
                    return "HAND BACK";
                default:
                    return "BACK";
            }
        }
    }
}
