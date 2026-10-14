using System.Collections.Generic;
using Ror2Common;
using RoR2;
using UnityEngine.Networking;

namespace MarioTest
{
    // Mario's multiplayer messages. The player's machine sends the frames of its sim (MarioFrames)
    // to the server, which relays them to every other client; each of those plays them back on a
    // shadow sim (MarioPuppet). The server's own game is a client of that too. Everyone needs the
    // same plugin build and the Sunshine disc's assets; a mismatch is logged once and that
    // player's Mario looks like Commando to the other.
    internal static class MarioNet
    {
        /// Frames of one Mario's sim: client to server, then server to the other clients.
        public const short FramesMsg = 0x4D01;

        static readonly Dictionary<uint, FrameQueue> queues = new Dictionary<uint, FrameQueue>();
        static bool warned;

        public static void Register()
        {
            Mp.Register(FramesMsg, OnServer, OnClient);
            Run.onRunDestroyGlobal += run => queues.Clear();
        }

        /// The server relays a client's frames to everyone else untouched (the host's own
        /// frames come in with `from` null and go to everyone).
        static void OnServer(NetworkConnection from, ByteReader reader) =>
            Mp.SendToAllExcept(FramesMsg, reader.Raw, from);

        static void OnClient(NetworkConnection from, ByteReader reader)
        {
            var frames = new List<Frame>();
            var status = FrameCodec.ReadBatch(reader, out uint netId, out bool clone, frames, out byte theirs);
            if (status != FrameCodec.BatchStatus.Ok)
            {
                if (status != FrameCodec.BatchStatus.Corrupt && !warned)
                {
                    warned = true;
                    Plugin.Log.LogWarning(status == FrameCodec.BatchStatus.WrongAbi
                        ? $"Another player's Mario runs libsms ABI {theirs}, this one {Native.AbiVersion}: install the same Mario release as everyone else. Their Mario will look like Commando."
                        : $"Another player's Mario uses network protocol {theirs}, this one {FrameCodec.Protocol}: install the same Mario release as everyone else. Their Mario will look like Commando.");
                }
                return;
            }
            // The player's own frames come back through the relay: they already have them.
            var go = Mp.Find(netId);
            var body = go ? go.GetComponent<CharacterBody>() : null;
            if (body && body.hasEffectiveAuthority) return;
            if (!queues.TryGetValue(netId, out var queue))
                queues[netId] = queue = new FrameQueue();
            queue.Clone = clone;
            foreach (var f in frames) queue.Add(f);
        }

        /// The frames waiting for this body's puppet, or null if none have come.
        public static FrameQueue QueueFor(uint netId) =>
            queues.TryGetValue(netId, out var q) ? q : null;

        public static void Drop(uint netId) => queues.Remove(netId);

        /// Sends one body's recorded frames.
        public static void Send(FrameRecorder recorder, uint netId, bool clone)
        {
            var payload = recorder.Take(netId, clone);
            if (payload != null) Mp.SendToServer(FramesMsg, payload);
        }
    }
}
