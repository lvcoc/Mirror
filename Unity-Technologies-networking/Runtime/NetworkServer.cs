#if ENABLE_UNET
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Networking.Match;
using UnityEngine.Networking.NetworkSystem;
using UnityEngine.Networking.Types;

namespace UnityEngine.Networking
{
    public sealed class NetworkServer
    {

        static bool s_Active;
        static volatile NetworkServer s_Instance;
        static object s_Sync = new Object();
        static bool m_DontListen;
        static bool s_LocalClientActive;
        ULocalConnectionToClient m_LocalConnection;

        static NetworkScene s_NetworkScene;
        static HashSet<int> s_ExternalConnections;

        float m_MaxDelay = 0.1f;

        NetworkMessageHandlers m_MessageHandlers = new NetworkMessageHandlers();
        List<NetworkConnection> m_Connections = new List<NetworkConnection>();

        int m_ServerHostId = -1;
        int m_ServerPort = -1;
        int m_RelaySlotId = -1;
        HostTopology m_HostTopology;
        byte[] m_MsgBuffer;
        bool m_UseWebSockets;
        bool m_Initialized = false;

        // this is cached here for easy access when checking the size of state update packets in NetworkIdentity
        static internal ushort maxPacketSize;

        // original HLAPI has .localConnections list with only m_LocalConnection in it
        // (for downwards compatibility because they removed the real localConnections list a while ago)
        // => removed it for easier code. use .localConection now!
        static public NetworkConnection localConnection { get { return (NetworkConnection)instance.m_LocalConnection; } }

        static public int listenPort { get { return instance.m_ServerPort; } }
        static public int serverHostId { get { return instance.m_ServerHostId; } }

        public static List<NetworkConnection> connections { get { return instance.m_Connections; } }
        public static Dictionary<short, NetworkMessageDelegate> handlers { get { return instance.m_MessageHandlers.GetHandlers(); } }
        public static HostTopology hostTopology { get { return instance.m_HostTopology; }}

        public static Dictionary<NetworkInstanceId, NetworkIdentity> objects { get { return s_NetworkScene.localObjects; } }
        public static bool dontListen { get { return m_DontListen; } set { m_DontListen = value; } }
        public static bool useWebSockets { get { return instance.m_UseWebSockets; } set { instance.m_UseWebSockets = value; } }

        internal static NetworkServer instance
        {
            get
            {
                if (s_Instance == null)
                {
                    lock (s_Sync)
                    {
                        if (s_Instance == null)
                        {
                            s_Instance = new NetworkServer();
                        }
                    }
                }
                return s_Instance;
            }
        }

        public static bool active { get { return s_Active; } }
        public static bool localClientActive { get { return s_LocalClientActive; } }
        public static int numChannels { get { return instance.m_HostTopology.DefaultConfig.ChannelCount; } }
        public static float maxDelay { get { return instance.m_MaxDelay; } set { instance.InternalSetMaxDelay(value); } }


        static Type s_NetworkConnectionClass = typeof(NetworkConnection);

        public static Type networkConnectionClass { get { return s_NetworkConnectionClass; } }

        static public void SetNetworkConnectionClass<T>() where T : NetworkConnection
        {
            s_NetworkConnectionClass = typeof(T);
        }

        NetworkServer()
        {
            NetworkTransport.Init();
            if (LogFilter.logDev) { Debug.Log("NetworkServer Created version " + Version.Current); }
            s_ExternalConnections = new HashSet<int>();
            s_NetworkScene = new NetworkScene();
            m_MsgBuffer = new byte[NetworkMessage.MaxMessageSize];
        }

        static public bool Configure(ConnectionConfig config, int maxConnections)
        {
            HostTopology top = new HostTopology(config, maxConnections);
            return Configure(top);
        }

        static public bool Configure(HostTopology topology)
        {
            instance.m_HostTopology = topology;
            return true;
        }

        public static void Reset()
        {
#if UNITY_EDITOR
            UnityEditor.NetworkDetailStats.ResetAll();
#endif
            NetworkTransport.Shutdown();
            NetworkTransport.Init();
            s_Instance = null;
            s_Active = false;
        }

        public static void Shutdown()
        {
            if (s_Instance != null)
            {
                s_Instance.InternalDisconnectAll();

                if (m_DontListen)
                {
                    // was never started, so dont stop
                }
                else
                {
                    NetworkTransport.RemoveHost(s_Instance.m_ServerHostId);
                    s_Instance.m_ServerHostId = -1;
                }

                s_Instance = null;
            }
            m_DontListen = false;
            s_Active = false;
        }

        public void Initialize()
        {
            if (m_Initialized)
                return;

            m_Initialized = true;
            NetworkTransport.Init();

            m_MsgBuffer = new byte[NetworkMessage.MaxMessageSize];

            if (m_HostTopology == null)
            {
                var config = new ConnectionConfig();
                config.AddChannel(QosType.ReliableSequenced);
                config.AddChannel(QosType.Unreliable);
                m_HostTopology = new HostTopology(config, 8);
            }

            if (LogFilter.logDebug) { Debug.Log("NetworkServer initialize."); }
        }

        static public bool Listen(MatchInfo matchInfo, int listenPort)
        {
            if (!matchInfo.usingRelay)
                return instance.InternalListen(null, listenPort);

            instance.InternalListenRelay(matchInfo.address, matchInfo.port, matchInfo.networkId, Utility.GetSourceID(), matchInfo.nodeId);
            return true;
        }

        internal void RegisterMessageHandlers()
        {
            m_MessageHandlers.RegisterHandlerSafe(MsgType.Ready, OnClientReadyMessage);
            m_MessageHandlers.RegisterHandlerSafe(MsgType.Command, OnCommandMessage);
            m_MessageHandlers.RegisterHandlerSafe(MsgType.LocalPlayerTransform, NetworkTransform.HandleTransform);
            m_MessageHandlers.RegisterHandlerSafe(MsgType.LocalChildTransform, NetworkTransformChild.HandleChildTransform);
            m_MessageHandlers.RegisterHandlerSafe(MsgType.RemovePlayer, OnRemovePlayerMessage);
            m_MessageHandlers.RegisterHandlerSafe(MsgType.Animation, NetworkAnimator.OnAnimationServerMessage);
            m_MessageHandlers.RegisterHandlerSafe(MsgType.AnimationParameters, NetworkAnimator.OnAnimationParametersServerMessage);
            m_MessageHandlers.RegisterHandlerSafe(MsgType.AnimationTrigger, NetworkAnimator.OnAnimationTriggerServerMessage);
            m_MessageHandlers.RegisterHandlerSafe(MsgType.Fragment, NetworkConnection.OnFragment);

            // also setup max packet size.
            maxPacketSize = hostTopology.DefaultConfig.PacketSize;
        }

        static public void ListenRelay(string relayIp, int relayPort, NetworkID netGuid, SourceID sourceId, NodeID nodeId)
        {
            instance.InternalListenRelay(relayIp, relayPort, netGuid, sourceId, nodeId);
        }

        internal void InternalListenRelay(string relayIp, int relayPort, NetworkID netGuid, SourceID sourceId, NodeID nodeId)
        {
            Initialize();

            m_ServerHostId = NetworkTransport.AddHost(m_HostTopology, listenPort);
            if (LogFilter.logDebug) { Debug.Log("Server Host Slot Id: " + m_ServerHostId); }

            Update();

            byte error;
            NetworkTransport.ConnectAsNetworkHost(
                m_ServerHostId,
                relayIp,
                relayPort,
                netGuid,
                sourceId,
                nodeId,
                out error);

            m_RelaySlotId = 0;
            if (LogFilter.logDebug) { Debug.Log("Relay Slot Id: " + m_RelaySlotId); }

            s_Active = true;
            RegisterMessageHandlers();
        }

        static public bool Listen(int serverPort)
        {
            return instance.InternalListen(null, serverPort);
        }

        static public bool Listen(string ipAddress, int serverPort)
        {
            return instance.InternalListen(ipAddress, serverPort);
        }

        internal bool InternalListen(string ipAddress, int serverPort)
        {
            Initialize();

            // only start server if we want to listen. otherwise this mode uses external connections instead
            if (!m_DontListen) 
            {
                Initialize();
                m_ServerPort = serverPort;

                if (m_UseWebSockets)
                {
                    m_ServerHostId = NetworkTransport.AddWebsocketHost(m_HostTopology, serverPort, ipAddress);
                }
                else
                {
                    m_ServerHostId = NetworkTransport.AddHost(m_HostTopology, serverPort, ipAddress);
                }

                if (m_ServerHostId == -1)
                {
                    return false;
                }

                if (LogFilter.logDebug) { Debug.Log("Server listen: " + ipAddress + ":" + m_ServerPort); }
            }

            maxPacketSize = hostTopology.DefaultConfig.PacketSize;
            s_Active = true;
            RegisterMessageHandlers();
            return true;
        }

        void InternalSetMaxDelay(float seconds)
        {
            // set on existing connections
            for (int i = 0; i < connections.Count; i++)
            {
                NetworkConnection conn = connections[i];
                if (conn != null)
                    conn.SetMaxDelay(seconds);
            }

            // save for future connections
            m_MaxDelay = seconds;
        }

        public bool SetConnectionAtIndex(NetworkConnection conn)
        {
            while (m_Connections.Count <= conn.connectionId)
            {
                m_Connections.Add(null);
            }

            if (m_Connections[conn.connectionId] != null)
            {
                // already a connection at this index
                return false;
            }

            m_Connections[conn.connectionId] = conn;
            conn.SetHandlers(m_MessageHandlers);
            return true;
        }

        public bool RemoveConnectionAtIndex(int connectionId)
        {
            if (connectionId < 0 || connectionId >= m_Connections.Count)
                return false;
            m_Connections.RemoveAt(connectionId);
            return true;
        }

        // called by LocalClient to add itself. dont call directly.
        internal int AddLocalClient(LocalClient localClient)
        {
            if (m_LocalConnection != null)
            {
                Debug.LogError("Local Connection already exists");
                return -1;
            }

            m_LocalConnection = new ULocalConnectionToClient(localClient);
            m_LocalConnection.connectionId = 0;
            SetConnectionAtIndex(m_LocalConnection);

            m_LocalConnection.InvokeHandlerNoData(MsgType.Connect);

            return 0;
        }

        internal void RemoveLocalClient(NetworkConnection localClientConnection)
        {
            if (m_LocalConnection != null)
            {
                m_LocalConnection.Disconnect();
                m_LocalConnection.Dispose();
                m_LocalConnection = null;
            }
            s_LocalClientActive = false;
            RemoveConnectionAtIndex(0);
        }

        internal void SetLocalObjectOnServer(NetworkInstanceId netId, GameObject obj)
        {
            if (LogFilter.logDev) { Debug.Log("SetLocalObjectOnServer " + netId + " " + obj); }

            s_NetworkScene.SetLocalObject(netId, obj, false, true);
        }

        internal void ActivateLocalClientScene()
        {
            if (s_LocalClientActive)
                return;

            // ClientScene for a local connection is becoming active. any spawned objects need to be started as client objects
            s_LocalClientActive = true;
            foreach (var uv in objects.Values)
            {
                if (!uv.isClient)
                {
                    if (LogFilter.logDev) { Debug.Log("ActivateClientScene " + uv.netId + " " + uv.gameObject); }

                    ClientScene.SetLocalObject(uv.netId, uv.gameObject);
                    uv.OnStartClient();
                }
            }
        }

        static public bool SendToAll(short msgType, MessageBase msg)
        {
            if (LogFilter.logDev) { Debug.Log("Server.SendToAll msgType:" + msgType); }

            bool result = true;

            // remote connections
            for (int i = 0; i < connections.Count; i++)
            {
                NetworkConnection conn = connections[i];
                if (conn != null)
                    result &= conn.Send(msgType, msg);
            }

            return result;
        }

        // this is like SendToReady - but it doesn't check the ready flag on the connection.
        // this is used for ObjectDestroy messages.
        static bool SendToObservers(GameObject contextObj, short msgType, MessageBase msg)
        {
            if (LogFilter.logDev) { Debug.Log("Server.SendToObservers id:" + msgType); }

            var uv = contextObj.GetComponent<NetworkIdentity>();
            if (uv != null && uv.observers != null)
            {
                bool result = true;
                for (int i = 0; i < uv.observers.Count; ++i)
                {
                    var conn = uv.observers[i];
                    result &= conn.Send(msgType, msg);
                }
                return result;
            }
            return false;
        }

        static public bool SendToReady(GameObject contextObj, short msgType, MessageBase msg)
        {
            if (LogFilter.logDev) { Debug.Log("Server.SendToReady id:" + msgType); }

            if (contextObj == null)
            {
                for (int i = 0; i < connections.Count; i++)
                {
                    NetworkConnection conn = connections[i];
                    if (conn != null && conn.isReady)
                    {
                        conn.Send(msgType, msg);
                    }
                }
                return true;
            }

            var uv = contextObj.GetComponent<NetworkIdentity>();
            if (uv != null && uv.observers != null)
            {
                bool result = true;
                for (int i = 0; i < uv.observers.Count; ++i)
                {
                    var conn = uv.observers[i];
                    if (conn.isReady)
                    {
                        result &= conn.Send(msgType, msg);
                    }
                }
                return result;
            }
            return false;
        }

        static public void SendWriterToReady(GameObject contextObj, NetworkWriter writer, int channelId)
        {
            if (writer.Position > UInt16.MaxValue)
            {
                throw new UnityException("NetworkWriter used buffer is too big!");
            }
            // send relevant data, which is until .Position
            SendBytesToReady(contextObj, writer.ToArray(), writer.Position, channelId);
        }

        static public void SendBytesToReady(GameObject contextObj, byte[] buffer, int numBytes, int channelId)
        {
            if (contextObj == null)
            {
                // no context.. send to all ready connections
                bool success = true;
                for (int i = 0; i < connections.Count; i++)
                {
                    NetworkConnection conn = connections[i];
                    if (conn != null && conn.isReady)
                    {
                        if (!conn.SendBytes(buffer, numBytes, channelId))
                        {
                            success = false;
                        }
                    }
                }
                if (!success)
                {
                    if (LogFilter.logWarn) { Debug.LogWarning("SendBytesToReady failed"); }
                }
                return;
            }

            var uv = contextObj.GetComponent<NetworkIdentity>();
            try
            {
                bool success = true;
                for (int i = 0; i < uv.observers.Count; ++i)
                {
                    var conn = uv.observers[i];
                    if (conn.isReady)
                    {
                        if (!conn.SendBytes(buffer, numBytes, channelId))
                        {
                            success = false;
                        }
                    }
                }
                if (!success)
                {
                    if (LogFilter.logWarn) { Debug.LogWarning("SendBytesToReady failed for " + contextObj); }
                }
            }
            catch (NullReferenceException)
            {
                // observers may be null if object has not been spawned
                if (LogFilter.logWarn) { Debug.LogWarning("SendBytesToReady object " + contextObj + " has not been spawned"); }
            }
        }

        public static void SendBytesToPlayer(GameObject player, byte[] buffer, int numBytes, int channelId)
        {
            for (int i = 0; i < connections.Count; i++)
            {
                var conn = connections[i];
                if (conn != null)
                {
                    for (int j = 0; j < conn.playerControllers.Count; j++)
                    {
                        if (conn.playerControllers[j].IsValid && conn.playerControllers[j].gameObject == player)
                        {
                            conn.SendBytes(buffer, numBytes, channelId);
                            break;
                        }
                    }
                }
            }
        }

        static public bool SendUnreliableToAll(short msgType, MessageBase msg)
        {
            if (LogFilter.logDev) { Debug.Log("Server.SendUnreliableToAll msgType:" + msgType); }

            bool result = true;
            for (int i = 0; i < connections.Count; i++)
            {
                NetworkConnection conn = connections[i];
                if (conn != null)
                    result &= conn.SendUnreliable(msgType, msg);
            }
            return result;
        }

        static public bool SendUnreliableToReady(GameObject contextObj, short msgType, MessageBase msg)
        {
            if (LogFilter.logDev) { Debug.Log("Server.SendUnreliableToReady id:" + msgType); }

            if (contextObj == null)
            {
                // no context.. send to all ready connections
                for (int i = 0; i < connections.Count; i++)
                {
                    var conn = connections[i];
                    if (conn != null && conn.isReady)
                    {
                        conn.SendUnreliable(msgType, msg);
                    }
                }
                return true;
            }

            bool result = true;
            var uv = contextObj.GetComponent<NetworkIdentity>();
            int count = uv.observers.Count;
            for (int i = 0; i < count; i++)
            {
                var conn = uv.observers[i];
                if (conn.isReady)
                {
                    result &= conn.SendUnreliable(msgType, msg);
                }
            }
            return result;
        }

        static public bool SendByChannelToAll(short msgType, MessageBase msg, int channelId)
        {
            if (LogFilter.logDev) { Debug.Log("Server.SendByChannelToAll id:" + msgType); }

            bool result = true;

            for (int i = 0; i < connections.Count; i++)
            {
                var conn = connections[i];
                if (conn != null)
                    result &= conn.SendByChannel(msgType, msg, channelId);
            }
            return result;
        }

        static public bool SendByChannelToReady(GameObject contextObj, short msgType, MessageBase msg, int channelId)
        {
            if (LogFilter.logDev) { Debug.Log("Server.SendByChannelToReady msgType:" + msgType); }

            if (contextObj == null)
            {
                // no context.. send to all ready connections
                for (int i = 0; i < connections.Count; i++)
                {
                    var conn = connections[i];
                    if (conn != null && conn.isReady)
                    {
                        conn.SendByChannel(msgType, msg, channelId);
                    }
                }
                return true;
            }

            bool result = true;
            var uv = contextObj.GetComponent<NetworkIdentity>();
            int count = uv.observers.Count;
            for (int i = 0; i < count; i++)
            {
                var conn = uv.observers[i];
                if (conn.isReady)
                {
                    result &= conn.SendByChannel(msgType, msg, channelId);
                }
            }
            return result;
        }

        static public void DisconnectAll()
        {
            instance.InternalDisconnectAll();
        }

        public void DisconnectAllConnections()
        {
            for (int i = 0; i < connections.Count; i++)
            {
                NetworkConnection conn = connections[i];
                if (conn != null)
                {
                    conn.Disconnect();
                    conn.Dispose();
                }
            }
        }

        internal void InternalDisconnectAll()
        {
            DisconnectAllConnections();

            if (m_LocalConnection != null)
            {
                m_LocalConnection.Disconnect();
                m_LocalConnection.Dispose();
                m_LocalConnection = null;
            }

            s_Active = false;
            s_LocalClientActive = false;
        }

        // The user should never need to pump the update loop manually
        internal static void Update()
        {
            if (s_Instance != null)
                s_Instance.InternalUpdate();
        }

        void UpdateServerObjects()
        {
            // vis2k: original code only removed null entries every 100 frames. this was unnecessarily complicated and
            // probably even slower than removing null entries each time (hence less iterations next time).
            List<NetworkInstanceId> remove = new List<NetworkInstanceId>();
            foreach (var kvp in objects)
            {
                if (kvp.Value != null && kvp.Value.gameObject != null)
                {
                    kvp.Value.UNetUpdate();
                }
                else
                {
                    remove.Add(kvp.Key); 
                }
            }

            // now remove
            foreach (NetworkInstanceId key in remove)
            {
                objects.Remove(key);
            }
        }

        // this can be used independantly of Update() - such as when using external connections and not listening.
        public void UpdateConnections()
        {
            for (int i = 0; i < connections.Count; i++)
            {
                NetworkConnection conn = connections[i];
                if (conn != null)
                    conn.FlushChannels();
            }
        }

        internal void InternalUpdate()
        {
            if (m_ServerHostId == -1)
                return;

            int connectionId;
            int channelId;
            int receivedSize;
            byte error;

            var networkEvent = NetworkEventType.DataEvent;
            if (m_RelaySlotId != -1)
            {
                networkEvent = NetworkTransport.ReceiveRelayEventFromHost(m_ServerHostId, out error);
                if (NetworkEventType.Nothing != networkEvent)
                {
                    if (LogFilter.logDebug) { Debug.Log("NetGroup event:" + networkEvent); }
                }
                if (networkEvent == NetworkEventType.ConnectEvent)
                {
                    if (LogFilter.logDebug) { Debug.Log("NetGroup server connected"); }
                }
                if (networkEvent == NetworkEventType.DisconnectEvent)
                {
                    if (LogFilter.logDebug) { Debug.Log("NetGroup server disconnected"); }
                }
            }

            do
            {
                networkEvent = NetworkTransport.ReceiveFromHost(m_ServerHostId, out connectionId, out channelId, m_MsgBuffer, (ushort)m_MsgBuffer.Length, out receivedSize, out error);
                if (networkEvent != NetworkEventType.Nothing)
                {
                    if (LogFilter.logDev) { Debug.Log("Server event: host=" + m_ServerHostId + " event=" + networkEvent + " error=" + error); }
                }

                switch (networkEvent)
                {
                    case NetworkEventType.ConnectEvent:
                    {
                        HandleConnect(connectionId, error);
                        break;
                    }
                    case NetworkEventType.DataEvent:
                    {
                        HandleData(connectionId, channelId, receivedSize, error);
                        break;
                    }
                    case NetworkEventType.DisconnectEvent:
                    {
                        HandleDisconnect(connectionId, error);
                        break;
                    }
                    case NetworkEventType.Nothing:
                    {
                        break;
                    }
                    default:
                    {
                        if (LogFilter.logError) { Debug.LogError("Unknown network message type received: " + networkEvent); }
                        break;
                    }
                }
            }
            while (networkEvent != NetworkEventType.Nothing);

            UpdateConnections();

            if (m_DontListen)
            {
                UpdateConnections();
            }
            UpdateServerObjects();
        }

        void HandleConnect(int connectionId, byte error)
        {
            if (LogFilter.logDebug) { Debug.Log("Server accepted client:" + connectionId); }

            if (error != 0)
            {
                GenerateConnectError(error);
                return;
            }

            string address;
            int port;
            NetworkID networkId;
            NodeID node;
            byte error2;
            NetworkTransport.GetConnectionInfo(m_ServerHostId, connectionId, out address, out port, out networkId, out node, out error2);

            // add player info
            NetworkConnection conn = (NetworkConnection)Activator.CreateInstance(s_NetworkConnectionClass);
            conn.SetHandlers(m_MessageHandlers);
            conn.Initialize(address, m_ServerHostId, connectionId, m_HostTopology);
            conn.SetMaxDelay(m_MaxDelay);
            conn.lastError = (NetworkError)error2;

            // add connection at correct index
            while (m_Connections.Count <= connectionId)
            {
                m_Connections.Add(null);
            }
            m_Connections[connectionId] = conn;

            OnConnected(conn);
        }

        void OnConnected(NetworkConnection conn)
        {
            if (LogFilter.logDebug) { Debug.Log("Server accepted client:" + conn.connectionId); }

            // add player info
            conn.SetMaxDelay(m_MaxDelay);

            conn.InvokeHandlerNoData(MsgType.Connect);

            SendCrc(conn);
        }

        void HandleDisconnect(int connectionId, byte error)
        {
            if (LogFilter.logDebug) { Debug.Log("Server disconnect client:" + connectionId); }

            var conn = FindConnection(connectionId);
            if (conn == null)
            {
                return;
            }
            conn.lastError = (NetworkError)error;

            if (error != 0)
            {
                if ((NetworkError)error != NetworkError.Timeout)
                {
                    m_Connections[connectionId] = null;
                    if (LogFilter.logError) { Debug.LogError("Server client disconnect error, connectionId: " + connectionId + " error: " + (NetworkError)error); }
                    return;
                }
            }

            conn.Disconnect();
            m_Connections[connectionId] = null;
            if (LogFilter.logDebug) { Debug.Log("Server lost client:" + connectionId); }

            OnDisconnected(conn);
        }

        void OnDisconnected(NetworkConnection conn)
        {
            conn.InvokeHandlerNoData(MsgType.Disconnect);

            if (conn.playerControllers.Any(pc => pc.gameObject != null))
            {
                //NOTE: should there be default behaviour here to destroy the associated player?
                if (LogFilter.logWarn) { Debug.LogWarning("Player not destroyed when connection disconnected."); }
            }

            if (LogFilter.logDebug) { Debug.Log("Server lost client:" + conn.connectionId); }
            conn.RemoveObservers();
            conn.Dispose();
        }

        public NetworkConnection FindConnection(int connectionId)
        {
            if (connectionId < 0 || connectionId >= m_Connections.Count)
                return null;

            return m_Connections[connectionId];
        }

        void HandleData(int connectionId, int channelId, int receivedSize, byte error)
        {
            var conn = FindConnection(connectionId);
            if (conn == null)
            {
                if (LogFilter.logError) { Debug.LogError("HandleData Unknown connectionId:" + connectionId); }
                return;
            }
            conn.lastError = (NetworkError)error;

            if (error != 0)
            {
                GenerateDataError(conn, error);
                return;
            }

            OnData(conn, receivedSize, channelId);
        }

        void OnData(NetworkConnection conn, int receivedSize, int channelId)
        {
#if UNITY_EDITOR
            UnityEditor.NetworkDetailStats.IncrementStat(
                UnityEditor.NetworkDetailStats.NetworkDirection.Incoming,
                MsgType.LLAPIMsg, "msg", 1);
#endif
            conn.TransportReceive(m_MsgBuffer, receivedSize, channelId);
        }

        private void GenerateConnectError(byte error)
        {
            if (LogFilter.logError) { Debug.LogError("UNet Server Connect Error: " + error); }
            GenerateError(null, error);
        }

        private void GenerateDataError(NetworkConnection conn, byte error)
        {
            NetworkError dataError = (NetworkError)error;
            if (LogFilter.logError) { Debug.LogError("UNet Server Data Error: " + dataError); }
            GenerateError(conn, error);
        }

        private void GenerateDisconnectError(NetworkConnection conn, byte error)
        {
            NetworkError disconnectError = (NetworkError)error;
            if (LogFilter.logError) { Debug.LogError("UNet Server Disconnect Error: " + disconnectError + " conn:[" + conn + "]:" + conn.connectionId); }
            GenerateError(conn, error);
        }

        private void GenerateError(NetworkConnection conn, byte error)
        {
            if (handlers.ContainsKey(MsgType.Error))
            {
                ErrorMessage msg = new ErrorMessage();
                msg.errorCode = error;

                // write the message to a local buffer
                NetworkWriter writer = new NetworkWriter();
                msg.Serialize(writer);

                // pass a reader (attached to local buffer) to handler
                NetworkReader reader = new NetworkReader(writer.ToArray());
                conn.InvokeHandler(MsgType.Error, reader, 0);
            }
        }

        static public void RegisterHandler(short msgType, NetworkMessageDelegate handler)
        {
            instance.m_MessageHandlers.RegisterHandler(msgType, handler);
        }

        static public void UnregisterHandler(short msgType)
        {
            instance.m_MessageHandlers.UnregisterHandler(msgType);
        }

        static public void ClearHandlers()
        {
            instance.m_MessageHandlers.ClearMessageHandlers();
        }

        static public void ClearSpawners()
        {
            NetworkScene.ClearSpawners();
        }

        static public void GetStatsOut(out int numMsgs, out int numBufferedMsgs, out int numBytes, out int lastBufferedPerSecond)
        {
            numMsgs = 0;
            numBufferedMsgs = 0;
            numBytes = 0;
            lastBufferedPerSecond = 0;

            for (int i = 0; i < connections.Count; i++)
            {
                var conn = connections[i];
                if (conn != null)
                {
                    int snumMsgs;
                    int snumBufferedMsgs;
                    int snumBytes;
                    int slastBufferedPerSecond;

                    conn.GetStatsOut(out snumMsgs, out snumBufferedMsgs, out snumBytes, out slastBufferedPerSecond);

                    numMsgs += snumMsgs;
                    numBufferedMsgs += snumBufferedMsgs;
                    numBytes += snumBytes;
                    lastBufferedPerSecond += slastBufferedPerSecond;
                }
            }
        }

        static public void GetStatsIn(out int numMsgs, out int numBytes)
        {
            numMsgs = 0;
            numBytes = 0;
            for (int i = 0; i < connections.Count; i++)
            {
                var conn = connections[i];
                if (conn != null)
                {
                    int cnumMsgs;
                    int cnumBytes;

                    conn.GetStatsIn(out cnumMsgs, out cnumBytes);

                    numMsgs += cnumMsgs;
                    numBytes += cnumBytes;
                }
            }
        }

        // send this message to the player only
        static public void SendToClientOfPlayer(GameObject player, short msgType, MessageBase msg)
        {
            for (int i = 0; i < connections.Count; i++)
            {
                var conn = connections[i];
                if (conn != null)
                {
                    for (int j = 0; j < conn.playerControllers.Count; j++)
                    {
                        if (conn.playerControllers[j].IsValid && conn.playerControllers[j].gameObject == player)
                        {
                            conn.Send(msgType, msg);
                            return;
                        }
                    }
                }
            }

            if (LogFilter.logError) { Debug.LogError("Failed to send message to player object '" + player.name + ", not found in connection list"); }
        }

        static public void SendToClient(int connectionId, short msgType, MessageBase msg)
        {
            if (connectionId < connections.Count)
            {
                var conn = connections[connectionId];
                if (conn != null)
                {
                    conn.Send(msgType, msg);
                    return;
                }
            }
            if (LogFilter.logError) { Debug.LogError("Failed to send message to connection ID '" + connectionId + ", not found in connection list"); }
        }

        static public bool ReplacePlayerForConnection(NetworkConnection conn, GameObject player, short playerControllerId, NetworkHash128 assetId)
        {
            NetworkIdentity id;
            if (GetNetworkIdentity(player, out id))
            {
                id.SetDynamicAssetId(assetId);
            }
            return instance.InternalReplacePlayerForConnection(conn, player, playerControllerId);
        }

        static public bool ReplacePlayerForConnection(NetworkConnection conn, GameObject player, short playerControllerId)
        {
            return instance.InternalReplacePlayerForConnection(conn, player, playerControllerId);
        }

        static public bool AddPlayerForConnection(NetworkConnection conn, GameObject player, short playerControllerId, NetworkHash128 assetId)
        {
            NetworkIdentity id;
            if (GetNetworkIdentity(player, out id))
            {
                id.SetDynamicAssetId(assetId);
            }
            return instance.InternalAddPlayerForConnection(conn, player, playerControllerId);
        }

        static public bool AddPlayerForConnection(NetworkConnection conn, GameObject player, short playerControllerId)
        {
            return instance.InternalAddPlayerForConnection(conn, player, playerControllerId);
        }

        internal bool InternalAddPlayerForConnection(NetworkConnection conn, GameObject playerGameObject, short playerControllerId)
        {
            NetworkIdentity playerNetworkIdentity;
            if (!GetNetworkIdentity(playerGameObject, out playerNetworkIdentity))
            {
                if (LogFilter.logError) { Debug.Log("AddPlayer: playerGameObject has no NetworkIdentity. Please add a NetworkIdentity to " + playerGameObject); }
                return false;
            }
            playerNetworkIdentity.Reset();

            if (!CheckPlayerControllerIdForConnection(conn, playerControllerId))
                return false;

            // cannot have a player object in "Add" version
            PlayerController oldController = null;
            GameObject oldPlayer = null;
            if (conn.GetPlayerController(playerControllerId, out oldController))
            {
                oldPlayer = oldController.gameObject;
            }
            if (oldPlayer != null)
            {
                if (LogFilter.logError) { Debug.Log("AddPlayer: player object already exists for playerControllerId of " + playerControllerId); }
                return false;
            }

            PlayerController newPlayerController = new PlayerController(playerGameObject, playerControllerId);
            conn.SetPlayerController(newPlayerController);

            // Set the playerControllerId on the NetworkIdentity on the server, NetworkIdentity.SetLocalPlayer is not called on the server (it is on clients and that sets the playerControllerId there)
            playerNetworkIdentity.SetConnectionToClient(conn, newPlayerController.playerControllerId);

            SetClientReady(conn);

            if (SetupLocalPlayerForConnection(conn, playerNetworkIdentity, newPlayerController))
            {
                return true;
            }

            if (LogFilter.logDebug) { Debug.Log("Adding new playerGameObject object netId: " + playerGameObject.GetComponent<NetworkIdentity>().netId + " asset ID " + playerGameObject.GetComponent<NetworkIdentity>().assetId); }

            FinishPlayerForConnection(conn, playerNetworkIdentity, playerGameObject);
            if (playerNetworkIdentity.localPlayerAuthority)
            {
                playerNetworkIdentity.SetClientOwner(conn);
            }
            return true;
        }

        static bool CheckPlayerControllerIdForConnection(NetworkConnection conn, short playerControllerId)
        {
            if (playerControllerId < 0)
            {
                if (LogFilter.logError) { Debug.LogError("AddPlayer: playerControllerId of " + playerControllerId + " is negative"); }
                return false;
            }
            if (playerControllerId > PlayerController.MaxPlayersPerClient)
            {
                if (LogFilter.logError) { Debug.Log("AddPlayer: playerControllerId of " + playerControllerId + " is too high. max is " + PlayerController.MaxPlayersPerClient); }
                return false;
            }
            if (playerControllerId > PlayerController.MaxPlayersPerClient / 2)
            {
                if (LogFilter.logWarn) { Debug.LogWarning("AddPlayer: playerControllerId of " + playerControllerId + " is unusually high"); }
            }
            return true;
        }

        bool SetupLocalPlayerForConnection(NetworkConnection conn, NetworkIdentity uv, PlayerController newPlayerController)
        {
            if (LogFilter.logDev) { Debug.Log("NetworkServer SetupLocalPlayerForConnection netID:" + uv.netId); }

            var localConnection = conn as ULocalConnectionToClient;
            if (localConnection != null)
            {
                if (LogFilter.logDev) { Debug.Log("NetworkServer AddPlayer handling ULocalConnectionToClient"); }

                // Spawn this player for other players, instead of SpawnObject:
                if (uv.netId.IsEmpty())
                {
                    // it is allowed to provide an already spawned object as the new player object.
                    // so dont spawn it again.
                    uv.OnStartServer(true);
                }
                uv.RebuildObservers(true);
                SendSpawnMessage(uv, null);

                // Set up local player instance on the client instance and update local object map
                localConnection.localClient.AddLocalPlayer(newPlayerController);
                uv.SetClientOwner(conn);

                // Trigger OnAuthority
                uv.ForceAuthority(true);

                // Trigger OnStartLocalPlayer
                uv.SetLocalPlayer(newPlayerController.playerControllerId);
                return true;
            }
            return false;
        }

        static void FinishPlayerForConnection(NetworkConnection conn, NetworkIdentity uv, GameObject playerGameObject)
        {
            if (uv.netId.IsEmpty())
            {
                // it is allowed to provide an already spawned object as the new player object.
                // so dont spawn it again.
                Spawn(playerGameObject);
            }

            OwnerMessage owner = new OwnerMessage();
            owner.netId = uv.netId;
            owner.playerControllerId = uv.playerControllerId;
            conn.Send(MsgType.Owner, owner);
        }

        internal bool InternalReplacePlayerForConnection(NetworkConnection conn, GameObject playerGameObject, short playerControllerId)
        {
            NetworkIdentity playerNetworkIdentity;
            if (!GetNetworkIdentity(playerGameObject, out playerNetworkIdentity))
            {
                if (LogFilter.logError) { Debug.LogError("ReplacePlayer: playerGameObject has no NetworkIdentity. Please add a NetworkIdentity to " + playerGameObject); }
                return false;
            }

            if (!CheckPlayerControllerIdForConnection(conn, playerControllerId))
                return false;

            //NOTE: there can be an existing player
            if (LogFilter.logDev) { Debug.Log("NetworkServer ReplacePlayer"); }

            // is there already an owner that is a different object??
            PlayerController oldOwner;
            if (conn.GetPlayerController(playerControllerId, out oldOwner))
            {
                oldOwner.unetView.SetNotLocalPlayer();
                oldOwner.unetView.ClearClientOwner();
            }

            PlayerController newPlayerController = new PlayerController(playerGameObject, playerControllerId);
            conn.SetPlayerController(newPlayerController);

            // Set the playerControllerId on the NetworkIdentity on the server, NetworkIdentity.SetLocalPlayer is not called on the server (it is on clients and that sets the playerControllerId there)
            playerNetworkIdentity.SetConnectionToClient(conn, newPlayerController.playerControllerId);

            //NOTE: DONT set connection ready.

            if (LogFilter.logDev) { Debug.Log("NetworkServer ReplacePlayer setup local"); }

            if (SetupLocalPlayerForConnection(conn, playerNetworkIdentity, newPlayerController))
            {
                return true;
            }

            if (LogFilter.logDebug) { Debug.Log("Replacing playerGameObject object netId: " + playerGameObject.GetComponent<NetworkIdentity>().netId + " asset ID " + playerGameObject.GetComponent<NetworkIdentity>().assetId); }

            FinishPlayerForConnection(conn, playerNetworkIdentity, playerGameObject);
            if (playerNetworkIdentity.localPlayerAuthority)
            {
                playerNetworkIdentity.SetClientOwner(conn);
            }
            return true;
        }

        static bool GetNetworkIdentity(GameObject go, out NetworkIdentity view)
        {
            view = go.GetComponent<NetworkIdentity>();
            if (view == null)
            {
                if (LogFilter.logError) { Debug.LogError("UNET failure. GameObject doesn't have NetworkIdentity."); }
                return false;
            }
            return true;
        }

        static public void SetClientReady(NetworkConnection conn)
        {
            instance.SetClientReadyInternal(conn);
        }

        internal void SetClientReadyInternal(NetworkConnection conn)
        {
            if (LogFilter.logDebug) { Debug.Log("SetClientReadyInternal for conn:" + conn.connectionId); }

            if (conn.isReady)
            {
                if (LogFilter.logDebug) { Debug.Log("SetClientReady conn " + conn.connectionId + " already ready"); }
                return;
            }

            if (conn.playerControllers.Count == 0)
            {
                // this is now allowed
                if (LogFilter.logDebug) { Debug.LogWarning("Ready with no player object"); }
            }

            conn.isReady = true;

            var localConnection = conn as ULocalConnectionToClient;
            if (localConnection != null)
            {
                if (LogFilter.logDev) { Debug.Log("NetworkServer Ready handling ULocalConnectionToClient"); }

                // Setup spawned objects for local player
                // Only handle the local objects for the first player (no need to redo it when doing more local players)
                // and don't handle player objects here, they were done above
                foreach (NetworkIdentity uv in objects.Values)
                {
                    // Need to call OnStartClient directly here, as it's already been added to the local object dictionary
                    // in the above SetLocalPlayer call
                    if (uv != null && uv.gameObject != null)
                    {
                        var vis = uv.OnCheckObserver(conn);
                        if (vis)
                        {
                            uv.AddObserver(conn);
                        }
                        if (!uv.isClient)
                        {
                            if (LogFilter.logDev) { Debug.Log("LocalClient.SetSpawnObject calling OnStartClient"); }
                            uv.OnStartClient();
                        }
                    }
                }
                return;
            }

            // Spawn/update all current server objects
            if (LogFilter.logDebug) { Debug.Log("Spawning " + objects.Count + " objects for conn " + conn.connectionId); }

            ObjectSpawnFinishedMessage msg = new ObjectSpawnFinishedMessage();
            msg.state = 0;
            conn.Send(MsgType.SpawnFinished, msg);

            foreach (NetworkIdentity uv in objects.Values)
            {
                if (uv == null)
                {
                    if (LogFilter.logWarn) { Debug.LogWarning("Invalid object found in server local object list (null NetworkIdentity)."); }
                    continue;
                }
                if (!uv.gameObject.activeSelf)
                {
                    continue;
                }

                if (LogFilter.logDebug) { Debug.Log("Sending spawn message for current server objects name='" + uv.gameObject.name + "' netId=" + uv.netId); }

                var vis = uv.OnCheckObserver(conn);
                if (vis)
                {
                    uv.AddObserver(conn);
                }
            }

            msg.state = 1;
            conn.Send(MsgType.SpawnFinished, msg);
        }

        static internal void ShowForConnection(NetworkIdentity uv, NetworkConnection conn)
        {
            if (conn.isReady)
                instance.SendSpawnMessage(uv, conn);
        }

        static internal void HideForConnection(NetworkIdentity uv, NetworkConnection conn)
        {
            ObjectDestroyMessage msg = new ObjectDestroyMessage();
            msg.netId = uv.netId;
            conn.Send(MsgType.ObjectHide, msg);
        }

        // call this to make all the clients not ready, such as when changing levels.
        static public void SetAllClientsNotReady()
        {
            for (int i = 0; i < connections.Count; i++)
            {
                var conn = connections[i];
                if (conn != null)
                {
                    SetClientNotReady(conn);
                }
            }
        }

        static public void SetClientNotReady(NetworkConnection conn)
        {
            instance.InternalSetClientNotReady(conn);
        }

        internal void InternalSetClientNotReady(NetworkConnection conn)
        {
            if (conn.isReady)
            {
                if (LogFilter.logDebug) { Debug.Log("PlayerNotReady " + conn); }
                conn.isReady = false;
                conn.RemoveObservers();

                NotReadyMessage msg = new NotReadyMessage();
                conn.Send(MsgType.NotReady, msg);
            }
        }

        // default ready handler.
        static void OnClientReadyMessage(NetworkMessage netMsg)
        {
            if (LogFilter.logDebug) { Debug.Log("Default handler for ready message from " + netMsg.conn); }
            SetClientReady(netMsg.conn);
        }

        // default remove player handler
        static void OnRemovePlayerMessage(NetworkMessage netMsg)
        {
            RemovePlayerMessage msg = new RemovePlayerMessage();
            netMsg.ReadMessage(msg);

            PlayerController player = null;
            netMsg.conn.GetPlayerController(msg.playerControllerId, out player);
            if (player != null)
            {
                netMsg.conn.RemovePlayerController(msg.playerControllerId);
                Destroy(player.gameObject);
            }
            else
            {
                if (LogFilter.logError) { Debug.LogError("Received remove player message but could not find the player ID: " + msg.playerControllerId); }
            }
        }

        // Handle command from specific player, this could be one of multiple players on a single client
        static  void OnCommandMessage(NetworkMessage netMsg)
        {
            int cmdHash = (int)netMsg.reader.ReadPackedUInt32();
            var netId = netMsg.reader.ReadNetworkId();

            var cmdObject = FindLocalObject(netId);
            if (cmdObject == null)
            {
                if (LogFilter.logWarn) { Debug.LogWarning("Instance not found when handling Command message [netId=" + netId + "]"); }
                return;
            }

            var uv = cmdObject.GetComponent<NetworkIdentity>();
            if (uv == null)
            {
                if (LogFilter.logWarn) { Debug.LogWarning("NetworkIdentity deleted when handling Command message [netId=" + netId + "]"); }
                return;
            }

            // Commands can be for player objects, OR other objects with client-authority
            // => check if there is no owner
            if (!netMsg.conn.playerControllers.Any(
                pc => pc.gameObject != null &&
                pc.gameObject.GetComponent<NetworkIdentity>().netId == uv.netId))
            {
                if (uv.clientAuthorityOwner != netMsg.conn)
                {
                    if (LogFilter.logWarn) { Debug.LogWarning("Command for object without authority [netId=" + netId + "]"); }
                    return;
                }
            }

            if (LogFilter.logDev) { Debug.Log("OnCommandMessage for netId=" + netId + " conn=" + netMsg.conn); }
            uv.HandleCommand(cmdHash, netMsg.reader);
        }

        internal void SpawnObject(GameObject obj)
        {
            if (!NetworkServer.active)
            {
                if (LogFilter.logError) { Debug.LogError("SpawnObject for " + obj + ", NetworkServer is not active. Cannot spawn objects without an active server."); }
                return;
            }

            NetworkIdentity objNetworkIdentity;
            if (!GetNetworkIdentity(obj, out objNetworkIdentity))
            {
                if (LogFilter.logError) { Debug.LogError("SpawnObject " + obj + " has no NetworkIdentity. Please add a NetworkIdentity to " + obj); }
                return;
            }
            objNetworkIdentity.Reset();

            objNetworkIdentity.OnStartServer(false);

            if (LogFilter.logDebug) { Debug.Log("SpawnObject instance ID " + objNetworkIdentity.netId + " asset ID " + objNetworkIdentity.assetId); }

            objNetworkIdentity.RebuildObservers(true);
            //SendSpawnMessage(objNetworkIdentity, null);
        }

        internal void SendSpawnMessage(NetworkIdentity uv, NetworkConnection conn)
        {
            if (uv.serverOnly)
                return;

            if (LogFilter.logDebug) { Debug.Log("Server SendSpawnMessage: name=" + uv.name + " sceneId=" + uv.sceneId + " netid=" + uv.netId); } // for easier debugging
            if (uv.sceneId.IsEmpty())
            {
                ObjectSpawnMessage msg = new ObjectSpawnMessage();
                msg.netId = uv.netId;
                msg.assetId = uv.assetId;
                msg.position = uv.transform.position;
                msg.rotation = uv.transform.rotation;

                // include synch data
                NetworkWriter writer = new NetworkWriter();
                uv.UNetSerializeAllVars(writer);
                if (writer.Position > 0)
                {
                    msg.payload = writer.ToArray();
                }

                if (conn != null)
                {
                    conn.Send(MsgType.ObjectSpawn, msg);
                }
                else
                {
                    SendToReady(uv.gameObject, MsgType.ObjectSpawn, msg);
                }

#if UNITY_EDITOR
                UnityEditor.NetworkDetailStats.IncrementStat(
                    UnityEditor.NetworkDetailStats.NetworkDirection.Outgoing,
                    MsgType.ObjectSpawn, uv.assetId.ToString(), 1);
#endif
            }
            else
            {
                ObjectSpawnSceneMessage msg = new ObjectSpawnSceneMessage();
                msg.netId = uv.netId;
                msg.sceneId = uv.sceneId;
                msg.position = uv.transform.position;

                // include synch data
                NetworkWriter writer = new NetworkWriter();
                uv.UNetSerializeAllVars(writer);
                if (writer.Position > 0)
                {
                    msg.payload = writer.ToArray();
                }

                if (conn != null)
                {
                    conn.Send(MsgType.ObjectSpawnScene, msg);
                }
                else
                {
                    SendToReady(uv.gameObject, MsgType.ObjectSpawn, msg);
                }

#if UNITY_EDITOR
                UnityEditor.NetworkDetailStats.IncrementStat(
                    UnityEditor.NetworkDetailStats.NetworkDirection.Outgoing,
                    MsgType.ObjectSpawnScene, "sceneId", 1);
#endif
            }
        }

        static public void DestroyPlayersForConnection(NetworkConnection conn)
        {
            if (conn.playerControllers.Count == 0)
            {
                // list is empty if players are still in a lobby etc., no need to show a warning
                return;
            }

            if (conn.clientOwnedObjects != null)
            {
                var tmp = new HashSet<NetworkInstanceId>(conn.clientOwnedObjects);
                foreach (var netId in tmp)
                {
                    var obj = FindLocalObject(netId);
                    if (obj != null)
                    {
                        DestroyObject(obj);
                    }
                }
            }

            for (int i = 0; i < conn.playerControllers.Count; i++)
            {
                var player = conn.playerControllers[i];
                if (player.IsValid)
                {
                    if (player.unetView == null)
                    {
                        // the playerController's object has been destroyed, but RemovePlayerForConnection was never called.
                        // this is ok, just dont double destroy it.
                    }
                    else
                    {
                        DestroyObject(player.unetView, true);
                    }
                    player.gameObject = null;
                }
            }
            conn.playerControllers.Clear();
        }

        static void UnSpawnObject(GameObject obj)
        {
            if (obj == null)
            {
                if (LogFilter.logDev) { Debug.Log("NetworkServer UnspawnObject is null"); }
                return;
            }

            NetworkIdentity objNetworkIdentity;
            if (!GetNetworkIdentity(obj, out objNetworkIdentity)) return;

            UnSpawnObject(objNetworkIdentity);
        }

        static void UnSpawnObject(NetworkIdentity uv)
        {
            DestroyObject(uv, false);
        }

        static void DestroyObject(GameObject obj)
        {
            if (obj == null)
            {
                if (LogFilter.logDev) { Debug.Log("NetworkServer DestroyObject is null"); }
                return;
            }

            NetworkIdentity objNetworkIdentity;
            if (!GetNetworkIdentity(obj, out objNetworkIdentity)) return;

            DestroyObject(objNetworkIdentity, true);
        }

        static void DestroyObject(NetworkIdentity uv, bool destroyServerObject)
        {
            if (LogFilter.logDebug) { Debug.Log("DestroyObject instance:" + uv.netId); }
            if (objects.ContainsKey(uv.netId))
            {
                objects.Remove(uv.netId);
            }

            if (uv.clientAuthorityOwner != null)
            {
                uv.clientAuthorityOwner.RemoveOwnedObject(uv);
            }

#if UNITY_EDITOR
            UnityEditor.NetworkDetailStats.IncrementStat(
                UnityEditor.NetworkDetailStats.NetworkDirection.Outgoing,
                MsgType.ObjectDestroy, uv.assetId.ToString(), 1);
#endif

            ObjectDestroyMessage msg = new ObjectDestroyMessage();
            msg.netId = uv.netId;
            SendToObservers(uv.gameObject, MsgType.ObjectDestroy, msg);

            uv.ClearObservers();
            if (NetworkClient.active && s_LocalClientActive)
            {
                uv.OnNetworkDestroy();
                ClientScene.SetLocalObject(msg.netId, null);
            }

            // when unspawning, dont destroy the server's object
            if (destroyServerObject)
            {
                Object.Destroy(uv.gameObject);
            }
            uv.MarkForReset();
        }

        static public void ClearLocalObjects()
        {
            objects.Clear();
        }

        static public void Spawn(GameObject obj)
        {
            if (VerifyCanSpawn(obj))
            {
                instance.SpawnObject(obj);
            }
        }

        static bool CheckForPrefab(GameObject obj)
        {
#if UNITY_EDITOR
            return (UnityEditor.PrefabUtility.GetPrefabParent(obj) == null) && (UnityEditor.PrefabUtility.GetPrefabObject(obj) != null);
#else
            return false;
#endif
        }

        static bool VerifyCanSpawn(GameObject obj)
        {
            if (CheckForPrefab(obj))
            {
                Debug.LogErrorFormat("GameObject {0} is a prefab, it can't be spawned. This will cause errors in builds.", obj.name);
                return false;
            }

            return true;
        }

        static public Boolean SpawnWithClientAuthority(GameObject obj, GameObject player)
        {
            var uv = player.GetComponent<NetworkIdentity>();
            if (uv == null)
            {
                Debug.LogError("SpawnWithClientAuthority player object has no NetworkIdentity");
                return false;
            }

            if (uv.connectionToClient == null)
            {
                Debug.LogError("SpawnWithClientAuthority player object is not a player.");
                return false;
            }

            return SpawnWithClientAuthority(obj, uv.connectionToClient);
        }

        static public bool SpawnWithClientAuthority(GameObject obj, NetworkConnection conn)
        {
            if (!conn.isReady)
            {
                Debug.LogError("SpawnWithClientAuthority NetworkConnection is not ready!");
                return false;
            }

            Spawn(obj);

            var uv = obj.GetComponent<NetworkIdentity>();
            if (uv == null || !uv.isServer)
            {
                // spawning the object failed.
                return false;
            }

            return uv.AssignClientAuthority(conn);
        }

        static public bool SpawnWithClientAuthority(GameObject obj, NetworkHash128 assetId, NetworkConnection conn)
        {
            Spawn(obj, assetId);

            var uv = obj.GetComponent<NetworkIdentity>();
            if (uv == null || !uv.isServer)
            {
                // spawning the object failed.
                return false;
            }

            return uv.AssignClientAuthority(conn);
        }

        static public void Spawn(GameObject obj, NetworkHash128 assetId)
        {
            if (VerifyCanSpawn(obj))
            {
                NetworkIdentity id;
                if (GetNetworkIdentity(obj, out id))
                {
                    id.SetDynamicAssetId(assetId);
                }
                instance.SpawnObject(obj);
            }
        }

        static public void Destroy(GameObject obj)
        {
            DestroyObject(obj);
        }

        static public void UnSpawn(GameObject obj)
        {
            UnSpawnObject(obj);
        }

        internal bool InvokeBytes(ULocalConnectionToServer conn, byte[] buffer, int numBytes, int channelId)
        {
            NetworkReader reader = new NetworkReader(buffer);

            reader.ReadInt16(); // size
            short msgType = reader.ReadInt16();

            if (handlers.ContainsKey(msgType) && m_LocalConnection != null)
            {
                // this must be invoked with the connection to the client, not the client's connection to the server
                m_LocalConnection.InvokeHandler(msgType, reader, channelId);
                return true;
            }
            return false;
        }

        // invoked for local clients
        internal bool InvokeHandlerOnServer(ULocalConnectionToServer conn, short msgType, MessageBase msg, int channelId)
        {
            if (handlers.ContainsKey(msgType) && m_LocalConnection != null)
            {
                // write the message to a local buffer
                NetworkWriter writer = new NetworkWriter();
                msg.Serialize(writer);

                // pass a reader (attached to local buffer) to handler
                NetworkReader reader = new NetworkReader(writer.ToArray());

                // this must be invoked with the connection to the client, not the client's connection to the server
                m_LocalConnection.InvokeHandler(msgType, reader, channelId);
                return true;
            }
            if (LogFilter.logError) { Debug.LogError("Local invoke: Failed to find local connection to invoke handler on [connectionId=" + conn.connectionId + "] for MsgId:" + msgType); }
            return false;
        }

        static public GameObject FindLocalObject(NetworkInstanceId netId)
        {
            return s_NetworkScene.FindLocalObject(netId);
        }

        static public Dictionary<short, NetworkConnection.PacketStat> GetConnectionStats()
        {
            Dictionary<short, NetworkConnection.PacketStat> stats = new Dictionary<short, NetworkConnection.PacketStat>();

            for (int i = 0; i < connections.Count; i++)
            {
                var conn = connections[i];
                if (conn != null)
                {
                    foreach (short k in conn.packetStats.Keys)
                    {
                        if (stats.ContainsKey(k))
                        {
                            NetworkConnection.PacketStat s = stats[k];
                            s.count += conn.packetStats[k].count;
                            s.bytes += conn.packetStats[k].bytes;
                            stats[k] = s;
                        }
                        else
                        {
                            stats[k] = new NetworkConnection.PacketStat(conn.packetStats[k]);
                        }
                    }
                }
            }
            return stats;
        }

        static public void ResetConnectionStats()
        {
            for (int i = 0; i < connections.Count; i++)
            {
                var conn = connections[i];
                if (conn != null)
                {
                    conn.ResetStats();
                }
            }
        }

        static public bool AddExternalConnection(NetworkConnection conn)
        {
            return instance.AddExternalConnectionInternal(conn);
        }

        bool AddExternalConnectionInternal(NetworkConnection conn)
        {
            if (conn.connectionId < 0)
                return false;

            if (conn.connectionId < connections.Count && connections[conn.connectionId] != null)
            {
                if (LogFilter.logError) { Debug.LogError("AddExternalConnection failed, already connection for id:" + conn.connectionId); }
                return false;
            }

            if (LogFilter.logDebug) { Debug.Log("AddExternalConnection external connection " + conn.connectionId); }
            SetConnectionAtIndex(conn);
            s_ExternalConnections.Add(conn.connectionId);
            conn.InvokeHandlerNoData(MsgType.Connect);

            return true;
        }

        static public void RemoveExternalConnection(int connectionId)
        {
            instance.RemoveExternalConnectionInternal(connectionId);
        }

        bool RemoveExternalConnectionInternal(int connectionId)
        {
            if (!s_ExternalConnections.Contains(connectionId))
            {
                if (LogFilter.logError) { Debug.LogError("RemoveExternalConnection failed, no connection for id:" + connectionId); }
                return false;
            }
            if (LogFilter.logDebug) { Debug.Log("RemoveExternalConnection external connection " + connectionId); }

            var conn = FindConnection(connectionId);
            if (conn != null)
            {
                conn.RemoveObservers();
            }
            m_Connections.RemoveAt(connectionId);

            return true;
        }

        static bool ValidateSceneObject(NetworkIdentity netId)
        {
            if (netId.gameObject.hideFlags == HideFlags.NotEditable || netId.gameObject.hideFlags == HideFlags.HideAndDontSave)
                return false;

#if UNITY_EDITOR
            if (UnityEditor.EditorUtility.IsPersistent(netId.gameObject))
                return false;
#endif

            // If not a scene object
            if (netId.sceneId.IsEmpty())
                return false;

            return true;
        }

        static public bool SpawnObjects()
        {
            if (!active)
                return true;

            NetworkIdentity[] netIds = Resources.FindObjectsOfTypeAll<NetworkIdentity>();
            for (int i = 0; i < netIds.Length; i++)
            {
                var netId = netIds[i];
                if (!ValidateSceneObject(netId))
                    continue;

                if (LogFilter.logDebug) { Debug.Log("SpawnObjects sceneId:" + netId.sceneId + " name:" + netId.gameObject.name); }
                netId.Reset();
                netId.gameObject.SetActive(true);
            }
            for (int i = 0; i < netIds.Length; i++)
            {
                var netId = netIds[i];
                if (!ValidateSceneObject(netId))
                    continue;

                Spawn(netId.gameObject);

                // these objects are server authority - even if "localPlayerAuthority" is set on them
                netId.ForceAuthority(true);
            }
            return true;
        }

        static void SendCrc(NetworkConnection targetConnection)
        {
            if (NetworkCRC.singleton == null)
                return;

            if (NetworkCRC.scriptCRCCheck == false)
                return;

            CRCMessage crcMsg = new CRCMessage();

            // build entries
            List<CRCMessageEntry> entries = new List<CRCMessageEntry>();
            foreach (var name in NetworkCRC.singleton.scripts.Keys)
            {
                CRCMessageEntry entry = new CRCMessageEntry();
                entry.name = name;
                entry.channel = (byte)NetworkCRC.singleton.scripts[name];
                entries.Add(entry);
            }
            crcMsg.scripts = entries.ToArray();

            targetConnection.Send(MsgType.CRC, crcMsg);
        }
    };
}
#endif //ENABLE_UNET
