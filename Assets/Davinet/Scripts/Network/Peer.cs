﻿using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Generic;

namespace Davinet
{
    /// <summary>
    /// Represents either a client or server in the network. Sends and receives messages
    /// with other connected peers. Sends state updates and applies received state updates 
    /// for its <see cref="Remote"/>.
    /// </summary>
    public class Peer
    {
        private Remote remote;
        private NetManager netManager;
        private NetDataWriter netDataWriter;
        private EventBasedNetListener listener;

        /// <summary>
        /// If this peer receives any state updates before it has correctly
        /// initialized its remote, store them in a queue to apply them after
        /// initialization.
        /// </summary>
        private Queue<NetPacketReader> queuedStatePackets;

        private enum Role { Inactive, Server, Client, ListenClient }
        private Role role;

        private PeerDebug debug;

        public bool HasListenClient { get; set; }

        public Peer() : this(null)
        {
        }

        public Peer(PeerDebug debug)
        {
            this.debug = debug;

            listener = new EventBasedNetListener();
            listener.NetworkReceiveEvent += Listener_NetworkReceiveEvent;
            listener.ConnectionRequestEvent += Listener_ConnectionRequestEvent;            

            netManager = new NetManager(listener);
            netManager.AutoRecycle = false;

            if (debug != null)
            {
                netManager.SimulatePacketLoss = debug.settings.simulatePacketLoss;
                netManager.SimulationPacketLossChance = debug.settings.packetLossChance;
            }

            netDataWriter = new NetDataWriter();

            queuedStatePackets = new Queue<NetPacketReader>();
        }

        // TODO: Would be nice if server specific logic lived somewhere else.
        private void Listener_PeerConnectedEvent(NetPeer peer)
        {
            int id = remote.AddPeer(peer);

            NetDataWriter writer = new NetDataWriter();
            writer.Put((byte)PacketType.Join);
            writer.Put(id);
            writer.Put(StatefulWorld.Instance.Frame);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
            writer.Reset();

            remote.SynchronizeAll();

            // TODO: This should be part of the gameplay logic layer, since none of it is network specific.
            // Instead, the gameplay layer should listen for when a player joins, and spawn an appropriate prefab.
            var player = UnityEngine.Object.Instantiate(StatefulWorld.Instance.registeredPrefabsMap[1717083505]);
            StatefulWorld.Instance.Add(player.GetComponent<StatefulObject>());
            player.GetComponent<OwnableObject>().GrantOwnership(id);

            UnityEngine.Debug.Log($"Peer {peer.Id} connected.");
        }

        private void Listener_ConnectionRequestEvent(ConnectionRequest request)
        {            
            request.Accept();

            UnityEngine.Debug.Log($"Connection requested.");
        }

        private void Listener_NetworkReceiveEvent(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod)
        {
            PacketType packetType = (PacketType)reader.GetByte();

            if (packetType == PacketType.State)
            {
                if (remote == null)
                {
                    queuedStatePackets.Enqueue(reader);
                }
                else
                {
                    ReadState(reader);
                }
            }
            // TODO: This is also server only logic.
            else if (packetType == PacketType.Join)
            {
                int remoteID = reader.GetInt();
                int frame = reader.GetInt();

                StatefulWorld.Instance.Frame = frame;
                remote = new Remote(StatefulWorld.Instance, false, role == Role.ListenClient, remoteID);

                UnityEngine.Debug.Log($"Client assigned id {remoteID}");
            }
        }

        private void ReadState(NetPacketReader reader)
        {
            if (debug != null && debug.settings.simulateLatency)
            {
                debug.InsertDelayedReader(UnityEngine.Random.Range(debug.settings.minLatency, debug.settings.maxLatency) / (float)1000, reader);
            }
            else
            {
                remote.ReadState(reader);
            }
        }

        public void Listen(int port)
        {
            if (role != Role.Inactive)
                throw new System.Exception($"Cannot start listening with peer already performing the {role} role.");

            listener.PeerConnectedEvent += Listener_PeerConnectedEvent;
            netManager.Start(port);

            role = Role.Server;

            remote = new Remote(StatefulWorld.Instance, true, false, 0);

            StatefulWorld.Instance.Frame = 0;
        }

        public void Connect(string address, int port, bool listenClient=false)
        {
            if (role != Role.Inactive)
                throw new System.Exception($"Cannot connect with peer already performing the {role} role.");

            netManager.Start();
            netManager.Connect(address, port, "Davinet");

            role = listenClient ? Role.ListenClient : Role.Client;
        }

        public void PollEvents()
        {
            while (remote != null && queuedStatePackets.Count > 0)
            {
                NetPacketReader reader = queuedStatePackets.Dequeue();
                ReadState(reader);
            }

            netManager.PollEvents();

            if (role != Role.ListenClient && debug != null && debug.settings.simulateLatency)
            {
                foreach (NetPacketReader reader in debug.GetAllReadyReaders())
                {
                    remote.ReadState(reader);
                }
            }
        }

        public void SendState()
        {
            if (remote != null && role != Role.ListenClient)
            {
                remote.WriteState(netDataWriter);

                if (debug != null && debug.settings.simulateLatency)
                {
                    debug.SendStateDelayed(netDataWriter, netManager, HasListenClient);
                    // TODO: This should probably be pooled when simulating latency.
                    netDataWriter = new NetDataWriter();
                }
                else
                {                    
                    netManager.SendToAll(netDataWriter, DeliveryMethod.ReliableUnordered);
                    netDataWriter.Reset();
                }
            }
        }
    }
}