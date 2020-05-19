﻿using UnityEngine;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Generic;

namespace Davinet
{
    public class Remote
    {
        public INetEventListener NetEventListener => eventBasedNetListener;

        private EventBasedNetListener eventBasedNetListener;

        /// <summary>
        /// There is only one arbiter per session. The arbiter by default has ownership over all objects.
        /// </summary>
        private bool arbiter;
        private bool listenRemote;

        public NetDataWriter SetOwnershipWriter { get; private set; }

        private int remoteID;
        private Dictionary<int, NetPeer> peersByIndex;
        private StatefulWorld statefulWorld;

        private bool writeAllStates;
        private bool writeAllFields;

        public Remote(StatefulWorld statefulWorld, bool arbiter, bool listenRemote)
        {            
            this.arbiter = arbiter;
            this.listenRemote = listenRemote;
            this.statefulWorld = statefulWorld;

            statefulWorld.OnAdd += Spawn;
            statefulWorld.OnSetOwnership += StatefulWorld_OnSetOwnership;

            eventBasedNetListener = new EventBasedNetListener();
            eventBasedNetListener.NetworkReceiveEvent += Read;
            eventBasedNetListener.ConnectionRequestEvent += EventBasedNetListener_ConnectionRequestEvent;
            eventBasedNetListener.PeerConnectedEvent += EventBasedNetListener_PeerConnectedEvent;

            objectsToSpawn = new Dictionary<int, IdentifiableObject>();

            SetOwnershipWriter = new NetDataWriter();
            peersByIndex = new Dictionary<int, NetPeer>();
        }

        private void EventBasedNetListener_PeerConnectedEvent(NetPeer peer)
        {
            Debug.Log("Peer connected.");

            if (arbiter)
            {
                int id = peersByIndex.Count + 1;
                peersByIndex[id] = peer;

                NetDataWriter writer = new NetDataWriter();
                writer.Put((byte)PacketType.Join);
                writer.Put(id);
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
                writer.Reset();

                foreach (var kvp in statefulWorld.statefulObjects)
                {
                    objectsToSpawn.Add(kvp.Key, kvp.Value.GetComponent<IdentifiableObject>());
                    statefulWorld.SetOwnership(kvp.Value.GetComponent<OwnableObject>(), kvp.Value.GetComponent<OwnableObject>().Owner);
                }

                var player = Object.Instantiate((statefulWorld.registeredPrefabsMap[1717083505]));
                statefulWorld.Add(player.GetComponent<StatefulObject>());
                statefulWorld.SetOwnership(player.GetComponent<OwnableObject>(), id);

                writeAllStates = true;
                writeAllFields = true;
            }
        }

        private void EventBasedNetListener_ConnectionRequestEvent(ConnectionRequest request)
        {
            Debug.Log("Connection requested.");
            request.Accept();
        }

        private void StatefulWorld_OnSetOwnership(OwnableObject o)
        {
            if (SetOwnershipWriter.Length == 0)
                SetOwnershipWriter.Put((byte)PacketType.SetOwnership);

            SetOwnershipWriter.Put(o.Owner);
            SetOwnershipWriter.Put(o.GetComponent<StatefulObject>().ID);

            IInputController inputController = statefulWorld.statefulObjects[o.GetComponent<StatefulObject>().ID].GetComponent<IInputController>();

            if (inputController != null)
                inputController.SetEnabled(o.Owner == remoteID);
        }

        private void Spawn(StatefulObject obj)
        {
            if (arbiter)
            {
                IdentifiableObject o = obj.GetComponent<IdentifiableObject>();
                objectsToSpawn.Add(obj.ID, o);
            }
        }

        private Dictionary<int, IdentifiableObject> objectsToSpawn;

        public void WriteState(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.State);

            // Object state serialization.
            foreach (var kvp in statefulWorld.statefulObjects)
            {
                if ((arbiter || kvp.Value.GetComponent<OwnableObject>().Owner == remoteID) && (kvp.Value.GetComponent<IStateful>().ShouldWrite() || writeAllStates))
                {
                    writer.Put(kvp.Key);
                    kvp.Value.GetComponent<IStateful>().Write(writer);
                }
            }

            writeAllStates = false;
        }

        public void WriteFields(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.Fields);

            foreach (var kvp in statefulWorld.statefulObjects)
            {
                if (arbiter || kvp.Value.GetComponent<OwnableObject>().Owner == remoteID)
                {
                    kvp.Value.WriteStateFields(writer, kvp.Key, writeAllFields);
                }
            }

            writeAllFields = false;
        }

        public void WriteSpawns(NetDataWriter writer)
        {
            if (arbiter)
            {
                writer.Put((byte)PacketType.Spawn);

                foreach (var kvp in objectsToSpawn)
                {
                    writer.Put(kvp.Key);
                    writer.Put(kvp.Value.GUID);
                }
            }

            objectsToSpawn.Clear();
        }

        private void Read(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod)
        {
            PacketType packetType = (PacketType)reader.GetByte();

            switch (packetType)
            {
                case PacketType.State:
                    ReadState(reader);
                    break;
                case PacketType.Fields:
                    ReadFields(reader);
                    break;
                case PacketType.Spawn:
                    ReadSpawns(reader);
                    break;
                case PacketType.Join:
                    ReadJoin(reader);
                    break;
                case PacketType.SetOwnership:
                    ReadOwnership(reader);
                    break;
                default:
                    break;
            }
        }

        private void ReadOwnership(NetPacketReader reader)
        {
            while (!reader.EndOfData)
            {
                int owner = reader.GetInt();
                int id = reader.GetInt();

                statefulWorld.statefulObjects[id].GetComponent<OwnableObject>().Owner = owner;
                IInputController inputController = statefulWorld.statefulObjects[id].GetComponent<IInputController>();

                if (inputController != null)
                    inputController.SetEnabled(owner == remoteID);
            }
        }

        private void ReadJoin(NetPacketReader reader)
        {            
            remoteID = reader.GetInt();
            Debug.Log($"Local client assigned id {remoteID}");
        }

        private void ReadState(NetPacketReader reader)
        {
            if (listenRemote)
                return;

            while (!reader.EndOfData)
            {
                int id = reader.GetInt();
                
                if (statefulWorld.statefulObjects[id].GetComponent<OwnableObject>().Owner != remoteID)
                    statefulWorld.statefulObjects[id].GetComponent<IStateful>().Read(reader);
                else
                    statefulWorld.statefulObjects[id].GetComponent<IStateful>().Clear(reader);                
            }
        }

        private void ReadFields(NetDataReader reader)
        {
            if (listenRemote)
                return;

            // Clear the first byte of the payload. This will be
            // a StatefulObject.DataType.Object enum.
            // TODO: Should not send the packet if no payload.
            if (!reader.EndOfData)
                reader.GetByte();

            while (!reader.EndOfData)
            {
                int id = reader.GetInt();

                if (statefulWorld.statefulObjects[id].GetComponent<OwnableObject>().Owner != remoteID)
                    statefulWorld.statefulObjects[id].ReadStateFields(reader);
                else
                    statefulWorld.statefulObjects[id].ReadStateFields(reader, true);
            }
        }

        private void ReadSpawns(NetDataReader reader)
        {
            if (listenRemote)
                return;

            while (!reader.EndOfData)
            {
                // Instantiate, etc.
                int id = reader.GetInt();
                int GUID = reader.GetInt();

                // Check if it's already been spawned.
                if (!statefulWorld.statefulObjects.ContainsKey(id))
                {
                    IdentifiableObject clone = Object.Instantiate(statefulWorld.registeredPrefabsMap[GUID], Vector3.zero, Quaternion.identity);
                    statefulWorld.statefulObjects[id] = clone.GetComponent<StatefulObject>();
                }
            }
        }
    }
}
