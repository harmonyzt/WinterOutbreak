using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace WinterOutbreak.Fika
{
    internal enum WinterPacketKind : byte
    {
        /// <summary>
        /// Client to host: send me the weather and the bonfires.
        /// </summary>
        Ready,

        /// <summary>
        /// Host to clients.
        /// </summary>
        Weather,

        Bonfire
    }

    internal struct WinterPacket : INetSerializable
    {
        public WinterPacketKind Kind;
        public string Text;
        public float Depth;
        public float DepthRate;
        public float MaxDepth;
        public Vector3 Position;
        public float Yaw;
        public string Ice;
        public string Sky;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)Kind);
            writer.Put(Text ?? string.Empty);

            writer.Put(Depth);
            writer.Put(DepthRate);
            writer.Put(MaxDepth);

            writer.Put(Position.x);
            writer.Put(Position.y);
            writer.Put(Position.z);
            writer.Put(Yaw);
            writer.Put(Ice ?? string.Empty);
            writer.Put(Sky ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            Kind = (WinterPacketKind)reader.GetByte();
            Text = reader.GetString();

            Depth = reader.GetFloat();
            DepthRate = reader.GetFloat();
            MaxDepth = reader.GetFloat();

            Position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            Yaw = reader.GetFloat();
            Ice = reader.GetString();
            Sky = reader.GetString();
        }
    }
}
