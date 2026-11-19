using System;
using BepInEx;
using Comfort.Common;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using UnityEngine;

namespace WinterOutbreak.Fika
{
    /// <summary>
    /// Fika sync for Winter Outbreak.
    /// </summary>
    [BepInPlugin("com.harmonyzt.winteroutbreak.fika", "Winter Outbreak Fika Sync", "1.0.1")]
    [BepInDependency("com.harmonyzt.winteroutbreak")]
    [BepInDependency("com.fika.core")]
    public sealed class WinterOutbreakFikaPlugin : BaseUnityPlugin, IWinterOutbreakNetwork
    {
        // 3: the weather also carries the host's sky (aurora and extra Blizzard flakes)
        private const string ProtocolVersion = "3";
        private const float ReadySeconds = 3f;

        private IFikaNetworkManager _manager;
        private float _nextReady;
        private bool _warnedMismatch;

        // Fika knows the role from matchmaking, before its network manager exists
        public bool IsHost => FikaBackendUtils.IsServer;

        public bool IsClient => FikaBackendUtils.IsClient;

        public bool IsHeadless => FikaBackendUtils.IsHeadless;

        // The game controller keeps the season from the backend, untouched by Fika-Headless's spring patch
        public bool IsHeadlessWinter => FikaBackendUtils.IsHeadless
            && Singleton<IFikaGame>.Instantiated && Singleton<IFikaGame>.Instance.Season == ESeason.Winter;

        private void Awake()
        {
            CoopSupport.Network = this;
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManagerCreated);
            FikaEventDispatcher.SubscribeEvent<FikaGameEndedEvent>(OnGameEnded);
            Logger.LogInfo("Winter Outbreak Fika sync loaded. Install it with Winter Outbreak on every peer, including a headless host.");
        }

        private void OnDestroy()
        {
            FikaEventDispatcher.UnsubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManagerCreated);
            FikaEventDispatcher.UnsubscribeEvent<FikaGameEndedEvent>(OnGameEnded);
            if (ReferenceEquals(CoopSupport.Network, this))
            {
                CoopSupport.Network = null;
            }
        }

        private void OnManagerCreated(FikaNetworkManagerCreatedEvent created)
        {
            _manager = created.Manager;
            _nextReady = 0f;
            _manager.RegisterPacket<WinterPacket, NetPeer>(Receive);
        }

        private void OnGameEnded(FikaGameEndedEvent ended)
        {
            _manager = null;
        }

        private void Update()
        {
            if (!IsClient || !CoopSupport.NeedsHostWeather || Time.time < _nextReady)
            {
                return;
            }

            _nextReady = Time.time + ReadySeconds;
            Send(new WinterPacket { Kind = WinterPacketKind.Ready, Text = ProtocolVersion });
        }

        public void SendWeather(SharedWeather weather)
        {
            if (IsHost)
            {
                Send(ToPacket(weather));
            }
        }

        public void SendBonfire(SharedBonfire bonfire)
        {
            if (_manager != null)
            {
                Send(ToPacket(bonfire));
            }
        }

        private void Receive(WinterPacket packet, NetPeer peer)
        {
            try
            {
                if (FikaBackendUtils.IsServer)
                {
                    ReceiveOnHost(packet, peer);
                }
                else
                {
                    ReceiveOnClient(packet);
                }
            }
            catch (Exception exception)
            {
                Logger.LogError($"Winter Outbreak sync packet {packet.Kind} failed: {exception}");
            }
        }

        private void ReceiveOnHost(WinterPacket packet, NetPeer peer)
        {
            if (packet.Kind == WinterPacketKind.Ready)
            {
                if (packet.Text != ProtocolVersion)
                {
                    WarnMismatch(packet.Text);
                    return;
                }

                if (CoopSupport.TryGetWeather(out var weather))
                {
                    Send(ToPacket(weather), peer);
                }

                foreach (var bonfire in CoopSupport.GetBonfires())
                {
                    Send(ToPacket(bonfire), peer);
                }

                return;
            }

            if (packet.Kind == WinterPacketKind.Bonfire)
            {
                // Pass it on to every client first, so a host that fails to build it (a headless host without graphics)
                // still lets everyone else see it
                Send(packet);

                CoopSupport.ReceiveBonfire(new SharedBonfire { Position = packet.Position, Yaw = packet.Yaw });
            }
        }

        private static void ReceiveOnClient(WinterPacket packet)
        {
            if (packet.Kind == WinterPacketKind.Weather)
            {
                CoopSupport.ReceiveWeather(new SharedWeather
                {
                    Preset = packet.Text,
                    Depth = packet.Depth,
                    DepthRate = packet.DepthRate,
                    MaxDepth = packet.MaxDepth,
                    Ice = packet.Ice,
                    Sky = packet.Sky
                });
            }
            else if (packet.Kind == WinterPacketKind.Bonfire)
            {
                CoopSupport.ReceiveBonfire(new SharedBonfire { Position = packet.Position, Yaw = packet.Yaw });
            }
        }

        private void WarnMismatch(string version)
        {
            if (_warnedMismatch)
            {
                return;
            }

            _warnedMismatch = true;
            Logger.LogWarning($"A client runs a different Winter Outbreak Fika sync (protocol {version}, host {ProtocolVersion}). "
                + "Install the same Winter Outbreak version on every peer.");
        }

        private void Send(WinterPacket packet, NetPeer peer = null)
        {
            if (_manager == null)
            {
                return;
            }

            if (peer == null)
            {
                _manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                _manager.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
            }
        }

        private static WinterPacket ToPacket(SharedWeather weather)
        {
            return new WinterPacket
            {
                Kind = WinterPacketKind.Weather,
                Text = weather.Preset,
                Depth = weather.Depth,
                DepthRate = weather.DepthRate,
                MaxDepth = weather.MaxDepth,
                Ice = weather.Ice,
                Sky = weather.Sky
            };
        }

        private static WinterPacket ToPacket(SharedBonfire bonfire)
        {
            return new WinterPacket
            {
                Kind = WinterPacketKind.Bonfire,
                Position = bonfire.Position,
                Yaw = bonfire.Yaw
            };
        }
    }
}
