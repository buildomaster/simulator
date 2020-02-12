/**
 * Copyright (c) 2019 LG Electronics, Inc.
 *
 * This software contains code licensed as described in LICENSE.
 *
 */

namespace Simulator.Network.Master
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using Core;
    using Core.Configs;
    using Core.Connection;
    using Core.Messaging;
    using Core.Messaging.Data;
    using Core.Threading;
    using LiteNetLib.Utils;
    using Shared;
    using UnityEngine;
    using UnityEngine.SceneManagement;
    using Utilities;

    /// <summary>
    /// Simulation network master manager
    /// </summary>
    public class MasterManager : MonoBehaviour, IMessageSender, IMessageReceiver
    {
        /// <summary>
        /// Data about connection with a single client
        /// </summary>
        public class ClientConnection
        {
            public IPeerManager Peer { get; set; }
            public SimulationState State { get; set; }
        }

        /// <summary>
        /// Network settings for this simulation
        /// </summary>
        private NetworkSettings settings;

        /// <summary>
        /// Root of the distributed objects
        /// </summary>
        private MasterObjectsRoot objectsRoot;

        /// <summary>
        /// All current clients connected or trying to connect to the master
        /// </summary>
        private readonly List<ClientConnection> clients = new List<ClientConnection>();

        /// <summary>
        /// Packets processor used for objects deserialization
        /// </summary>
        public NetPacketProcessor PacketsProcessor { get; } = new NetPacketProcessor();

        /// <summary>
        /// Messages manager for incoming and outgoing messages via connection manager
        /// </summary>
        public MessagesManager MessagesManager { get; }

        /// <summary>
        /// Simulation configuration
        /// </summary>
        public SimulationConfig Simulation { get; set; }

        /// <summary>
        /// Current state of the simulation on the server
        /// </summary>
        private SimulationState State { get; set; } = SimulationState.Initial;

        /// <summary>
        /// Connection manager for this server simulation
        /// </summary>
        private LiteNetLibServer ConnectionManager { get; } = new LiteNetLibServer();

        /// <inheritdoc />
        public string Key { get; } = "SimulationManager";

        /// <summary>
        /// All current clients connected or trying to connect to the master
        /// </summary>
        public List<ClientConnection> Clients
        {
            get => clients;
        }

        /// <summary>
        /// Root of the distributed objects
        /// </summary>
        public MasterObjectsRoot ObjectsRoot => objectsRoot;

        /// <summary>
        /// Constructor
        /// </summary>
        public MasterManager()
        {
            MessagesManager = new MessagesManager(ConnectionManager);
        }

        /// <summary>
        /// Unity Awake method
        /// </summary>
        private void Awake()
        {
            PacketsProcessor.RegisterNestedType(SerializationHelpers.SerializeLoadAgent,
                SerializationHelpers.DeserializeLoadAgent);
            PacketsProcessor.SubscribeReusable<Commands.Info, IPeerManager>(OnInfoCommand);
            PacketsProcessor.SubscribeReusable<Commands.LoadResult, IPeerManager>(OnLoadResultCommand);
        }

        /// <summary>
        /// Unity LateUpdate method
        /// </summary>
        private void LateUpdate()
        {
            ConnectionManager.PoolEvents();
        }

        /// <summary>
        /// Unity OnApplicationQuit method
        /// </summary>
        private void OnApplicationQuit()
        {
            StopConnection();
        }

        /// <summary>
        /// Unity OnDestroy method
        /// </summary>
        private void OnDestroy()
        {
            StopConnection();
        }

        /// <summary>
        /// Initializes the simulation, adds <see cref="MasterObjectsRoot"/> component to the root game object
        /// </summary>
        /// <param name="rootGameObject">Root game object where new component will be added</param>
        public void InitializeSimulation(GameObject rootGameObject)
        {
            SimulatorManager.Instance.TimeManager.LockTimeScale();
            Loader.Instance.LoaderUI.SetLoaderUIState(LoaderUI.LoaderUIStateType.PROGRESS);
            if (ObjectsRoot!=null)
                Log.Warning("Setting new master objects root, but previous one is still available on the scene.");
            objectsRoot = rootGameObject.AddComponent<MasterObjectsRoot>();
            ObjectsRoot.SetMessagesManager(MessagesManager);
            ObjectsRoot.SetSettings(settings);
        }

        /// <summary>
        /// Sets network settings for this simulation
        /// </summary>
        /// <param name="networkSettings">Network settings to set</param>
        public void SetSettings(NetworkSettings networkSettings)
        {
            settings = networkSettings;
            if (ObjectsRoot != null)
                ObjectsRoot.SetSettings(settings);
        }

        /// <summary>
        /// Start the connection listening for incoming packets
        /// </summary>
        public void StartConnection()
        {
            if (settings == null)
                throw new NullReferenceException("Set network settings before starting the connection.");
            MessagesManager.RegisterObject(this);
            ConnectionManager.Start(settings.ConnectionPort);
            ConnectionManager.PeerConnected += OnClientConnected;
            ConnectionManager.PeerDisconnected += OnClientDisconnected;
        }

        /// <summary>
        /// Stop the connection
        /// </summary>
        public void StopConnection()
        {
            DisconnectFromClients();
            State = SimulationState.Initial;
            ConnectionManager.PeerConnected -= OnClientConnected;
            ConnectionManager.PeerDisconnected -= OnClientDisconnected;
            ConnectionManager.Stop();
            MessagesManager.UnregisterObject(this);
        }

        /// <summary>
        /// Tries to connect to the clients defined in the simulation config clusters
        /// </summary>
        public void ConnectToClients()
        {
            Debug.Assert(State == SimulationState.Initial);
            foreach (var address in Simulation.Clusters)
            {
                Log.Info($"Trying to connect to the address: {address}");
                var endPoint = new IPEndPoint(IPAddress.Parse(address), settings.ConnectionPort);
                var peer = ConnectionManager.Connect(endPoint);
                Clients.Add(new ClientConnection() {Peer = peer, State = SimulationState.Initial});
            }

            State = SimulationState.Connecting;
        }

        /// <summary>
        /// Disconnects from all the clients
        /// </summary>
        public void DisconnectFromClients()
        {
            if (Clients == null || Clients.Count == 0)
                return;
            foreach (var client in Clients.Where(client => client.Peer.Connected))
                client.Peer.Disconnect();
            State = SimulationState.Initial;
        }

        /// <summary>
        /// Method invoked when connection with a client is established
        /// </summary>
        /// <param name="clientPeerManager">Connected client peer manager</param>
        private void OnClientConnected(IPeerManager clientPeerManager)
        {
            Log.Info($"Client connected: {clientPeerManager.PeerEndPoint.Address}");
            Debug.Assert(State == SimulationState.Connecting);
            var client = Clients.Find(c => c.Peer == clientPeerManager);
            Debug.Assert(client != null);
            Debug.Assert(client.State == SimulationState.Initial);

            client.State = SimulationState.Connecting;
        }

        /// <summary>
        /// Method invoked when connection with a client is established
        /// </summary>
        /// <param name="clientPeerManager">Connected client peer manager</param>
        private void OnClientDisconnected(IPeerManager clientPeerManager)
        {
            var client = Clients.Find(c => c.Peer == clientPeerManager);
            Debug.Assert(client != null);
            Clients.Remove(client);
            
            if (Loader.Instance.CurrentSimulation != null && State != SimulationState.Initial)
                Loader.StopAsync();

            State = SimulationState.Initial;
        }

        /// <inheritdoc/>
        public void UnicastMessage(IPEndPoint endPoint, Message message)
        {
            MessagesManager.UnicastMessage(endPoint, message);
        }

        /// <inheritdoc/>
        public void BroadcastMessage(Message message)
        {
            MessagesManager.BroadcastMessage(message);
        }

        /// <inheritdoc/>
        void IMessageSender.UnicastInitialMessages(IPEndPoint endPoint)
        {
        }

        /// <inheritdoc/>
        public void ReceiveMessage(IPeerManager sender, Message message)
        {
            PacketsProcessor.ReadAllPackets(new NetDataReader(message.Content.GetDataCopy()), sender);
        }

        /// <summary>
        /// Method invoked when manager receives info command
        /// </summary>
        /// <param name="info">Received info command</param>
        /// <param name="peer">Peer which has sent the command</param>
        public void OnInfoCommand(Commands.Info info, IPeerManager peer)
        {
            Debug.Assert(State == SimulationState.Connecting);
            var client = Clients.Find(c => c.Peer == peer);
            Debug.Assert(client != null);
            Debug.Assert(client.State == SimulationState.Connecting);

            Debug.Log($"NET: Client connected from {peer.PeerEndPoint}");

            Debug.Log($"NET: Client version = {info.Version}");
            Debug.Log($"NET: Client Unity version = {info.UnityVersion}");
            Debug.Log($"NET: Client OS = {info.OperatingSystem}");

            client.State = SimulationState.Connected;

            var sim = Loader.Instance.SimConfig;

            if (Clients.All(c => c.State == SimulationState.Connected))
            {
                var load = new Commands.Load()
                {
                    UseSeed = sim.Seed != null,
                    Seed = sim.Seed ?? 0,
                    Name = sim.Name,
                    MapName = sim.MapName,
                    MapUrl = sim.MapUrl,
                    ApiOnly = sim.ApiOnly,
                    Headless = sim.Headless,
                    Interactive = false,
                    TimeOfDay = sim.TimeOfDay.ToString("o", CultureInfo.InvariantCulture),
                    Rain = sim.Rain,
                    Fog = sim.Fog,
                    Wetness = sim.Wetness,
                    Cloudiness = sim.Cloudiness,
                    Agents = Simulation.Agents.Select(a => new Commands.LoadAgent()
                    {
                        Name = a.Name,
                        Url = a.Url,
                        Bridge = a.Bridge == null ? string.Empty : a.Bridge.Name,
                        Connection = a.Connection,
                        Sensors = a.Sensors,
                    }).ToArray(),
                    UseTraffic = false,
                    UsePedestrians = false
//                        UseTraffic = Simulation.UseTraffic,
//                        UsePedestrians = Simulation.UsePedestrians,
                };

                foreach (var c in Clients)
                {
                    UnicastMessage(c.Peer.PeerEndPoint, new Message(Key,
                        new BytesStack(PacketsProcessor.Write(load), false),
                        MessageType.ReliableOrdered));
                    c.State = SimulationState.Loading;
                }

                State = SimulationState.Loading;
            }
        }

        /// <summary>
        /// Method invoked when manager receives load result command
        /// </summary>
        /// <param name="res">Received load result command</param>
        /// <param name="peer">Peer which has sent the command</param>
        public void OnLoadResultCommand(Commands.LoadResult res, IPeerManager peer)
        {
            Debug.Assert(State == SimulationState.Loading);
            var client = Clients.Find(c => c.Peer == peer);
            Debug.Assert(client != null);
            Debug.Assert(client.State == SimulationState.Loading);

            if (res.Success)
            {
                Debug.Log("Client loaded");
            }
            else
            {
                // TODO: stop simulation / cancel loading for other clients
                Debug.LogError($"Client failed to load: {res.ErrorMessage}");

                // TODO: reset all other clients

                Debug.Log($"Failed to start '{Simulation.Name}' simulation");

                // TODO: update simulation status in DB
                // simulation.Status = "Invalid";
                // db.Update(simulation);

                // NotificationManager.SendNotification("simulation", SimulationResponse.Create(simulation), simulation.Owner);

                Loader.ResetLoaderScene();

                Clients.Clear();
                return;
            }

            client.State = SimulationState.Ready;

            if (Clients.All(c => c.State == SimulationState.Ready))
            {
                Debug.Log("All clients are ready. Resuming time.");

                var run = new Commands.Run();
                foreach (var c in Clients)
                {
                    UnicastMessage(c.Peer.PeerEndPoint, new Message(Key,
                        new BytesStack(PacketsProcessor.Write(run), false),
                        MessageType.ReliableOrdered));
                    c.State = SimulationState.Running;
                }

                State = SimulationState.Running;

                // Notify WebUI simulation is running
                Loader.Instance.CurrentSimulation.Status = "Running";
                
                // Flash main window to let user know simulation is ready
                WindowFlasher.Flash();

                if (Loader.Instance.LoaderUI != null)
                {
                    Loader.Instance.LoaderUI.SetLoaderUIState(LoaderUI.LoaderUIStateType.READY);
                    Loader.Instance.LoaderUI.DisableUI();
                }

                SceneManager.UnloadSceneAsync(Loader.Instance.LoaderScene);
                SimulatorManager.Instance.TimeManager.UnlockTimeScale();
            }
        }

        /// <summary>
        /// Broadcast the stop command to all clients' simulations
        /// </summary>
        public void BroadcastSimulationStop()
        {
            BroadcastMessage(new Message(Key, new BytesStack(PacketsProcessor.Write(new Commands.Stop()), false),
                MessageType.ReliableOrdered));
            ThreadingUtility.DispatchToMainThread(RevertChangesInSimulator);
        }

        private void RevertChangesInSimulator()
        {
            DisconnectFromClients();
        }
    }
}