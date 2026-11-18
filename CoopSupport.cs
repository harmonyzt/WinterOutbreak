using System.Collections.Generic;
using UnityEngine;

namespace WinterOutbreak
{
    /// <summary>
    /// What the optional Fika add-on (WinterOutbreak.Fika.dll) provides. Without it every game runs on its own, exactly
    /// as in single player.
    /// </summary>
    public interface IWinterOutbreakNetwork
    {
        /// <summary>
        /// This game is the Fika host (including a headless host).
        /// </summary>
        bool IsHost { get; }

        /// <summary>
        /// This game joined someone else's raid.
        /// </summary>
        bool IsClient { get; }

        /// <summary>
        /// Host only: sends the raid's weather to every client.
        /// </summary>
        void SendWeather(SharedWeather weather);

        /// <summary>
        /// Sends a bonfire this game built: a client sends it to the host, the host to every client.
        /// </summary>
        void SendBonfire(SharedBonfire bonfire);
    }

    /// <summary>
    /// The host's storm and where its snow depth stands, so every player sees the same raid.
    /// </summary>
    public struct SharedWeather
    {
        /// <summary>
        /// "Snowstorm", "Blizzard" or "Normal".
        /// </summary>
        public string Preset;

        /// <summary>
        /// Current snow depth in metres.
        /// </summary>
        public float Depth;

        /// <summary>
        /// How fast the depth grows, in metres per second (0 once it is full).
        /// </summary>
        public float DepthRate;

        public float MaxDepth;

        /// <summary>
        /// The host's frozen water settings (CoopIce.cs), which every client freezes with.
        /// </summary>
        public string Ice;

        /// <summary>
        /// The host's sky for this raid: "aurora,green,flakes" (CoopWeather.cs), or empty until decided.
        /// </summary>
        public string Sky;
    }

    public struct SharedBonfire
    {
        public Vector3 Position;
        public float Yaw;
    }

    /// <summary>
    /// The add-on's way in. Everything here runs on Unity's main thread.
    /// </summary>
    public static class CoopSupport
    {
        public static IWinterOutbreakNetwork Network;

        public static bool IsHost => Network != null && Network.IsHost;

        public static bool IsClient => Network != null && Network.IsClient;

        /// <summary>
        /// The host's current weather, once it has been rolled for this raid.
        /// </summary>
        public static bool TryGetWeather(out SharedWeather weather)
        {
            weather = default;
            return WinterOutbreakPlugin.Instance != null && WinterOutbreakPlugin.Instance.TryGetSharedWeather(out weather);
        }

        /// <summary>
        /// Client: still in a raid without the host's weather, so it should keep asking.
        /// </summary>
        public static bool NeedsHostWeather => WinterOutbreakPlugin.Instance != null && WinterOutbreakPlugin.Instance.NeedsHostWeather;

        public static void ReceiveWeather(SharedWeather weather)
        {
            WinterOutbreakPlugin.Instance?.ReceiveSharedWeather(weather);
        }

        /// <summary>
        /// Every bonfire built this raid, for a client that joins late.
        /// </summary>
        public static List<SharedBonfire> GetBonfires()
        {
            return WinterOutbreakPlugin.Instance?.GetSharedBonfires() ?? new List<SharedBonfire>();
        }

        public static void ReceiveBonfire(SharedBonfire bonfire)
        {
            WinterOutbreakPlugin.Instance?.ReceiveSharedBonfire(bonfire);
        }
    }
}
