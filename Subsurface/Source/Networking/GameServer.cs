﻿
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Lidgren.Network;
using Microsoft.Xna.Framework;
using RestSharp;

namespace Subsurface.Networking
{
    class GameServer : NetworkMember
    {

        public List<Client> connectedClients = new List<Client>();

        private NetServer server;
        private NetPeerConfiguration config;
        
        private TimeSpan SparseUpdateInterval = new TimeSpan(0, 0, 0, 1);
        private DateTime sparseUpdateTimer;

        private TimeSpan refreshMasterInterval = new TimeSpan(0, 0, 40);
        private DateTime refreshMasterTimer;

        private bool masterServerResponded;

        private bool registeredToMaster;

        private string password;

        private Client myClient;

        public GameServer(string name, int port, bool isPublic = false, string password = "", bool attemptUPnP = false, int maxPlayers = 10)
        {
            var endRoundButton = new GUIButton(new Rectangle(Game1.GraphicsWidth - 290, 20, 150, 25), "End round", Alignment.TopLeft, GUI.style, inGameHUD);
            endRoundButton.OnClicked = EndButtonHit;

            this.name = name;

            this.password = password;
            
            config = new NetPeerConfiguration("subsurface");

#if DEBUG
            config.SimulatedLoss = 0.2f;
            config.SimulatedMinimumLatency = 0.3f;
#endif 
            
            config.Port = port;
            Port = port;

            if (attemptUPnP)
            {
                config.EnableUPnP = true;
            }

            config.MaximumConnections = maxPlayers;

            config.DisableMessageType(NetIncomingMessageType.DebugMessage | NetIncomingMessageType.WarningMessage | NetIncomingMessageType.Receipt
                | NetIncomingMessageType.ErrorMessage | NetIncomingMessageType.Error);
                        
            config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);
                        
            try
            {
                server = new NetServer(config);    
                server.Start();

                if (attemptUPnP)
                {
                    server.UPnP.ForwardPort(port, "subsurface");
                }
            }

            catch (Exception e)
            {
                DebugConsole.ThrowError("Couldn't start the server", e);
            }

            if (isPublic)
            {
                RegisterToMasterServer();
            }

            
            updateInterval = new TimeSpan(0, 0, 0, 0, 30);

            DebugConsole.NewMessage("Server started", Color.Green);
        }

        private void RegisterToMasterServer()
        {
            var client = new RestClient(NetworkMember.MasterServerUrl);
            
            var request = new RestRequest("masterserver.php", Method.GET);            
            request.AddParameter("action", "addserver");
            request.AddParameter("servername", name);
            request.AddParameter("serverport", Port);
            request.AddParameter("playercount", PlayerCountToByte(connectedClients.Count, config.MaximumConnections));
            request.AddParameter("password", string.IsNullOrWhiteSpace(password) ? 0 : 1);

            // execute the request
            RestResponse response = (RestResponse)client.Execute(request);

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                DebugConsole.ThrowError("Error while connecting to master server (" +response.StatusCode+": "+response.StatusDescription+")");
                return;
            }

            if (response!=null && !string.IsNullOrWhiteSpace(response.Content))
            {
                DebugConsole.ThrowError("Error while connecting to master server (" +response.Content+")");
                return;
            }

            registeredToMaster = true;
            refreshMasterTimer = DateTime.Now + refreshMasterInterval;
        }

        private IEnumerable<object> RefreshMaster()
        {
            var client = new RestClient(NetworkMember.MasterServerUrl);

            var request = new RestRequest("masterserver.php", Method.GET);
            request.AddParameter("action", "refreshserver");
            request.AddParameter("gamestarted", gameStarted ? 1 : 0);
            request.AddParameter("playercount", PlayerCountToByte(connectedClients.Count, config.MaximumConnections));
            
            System.Diagnostics.Debug.WriteLine("refreshing master");

            var sw = new Stopwatch();
            sw.Start();

            masterServerResponded = false;
            var restRequestHandle = client.ExecuteAsync(request, response => MasterServerCallBack(response));

            DateTime timeOut = DateTime.Now + new TimeSpan(0, 0, 15);
            while (!masterServerResponded)
            {
                if (DateTime.Now > timeOut)
                {
                    restRequestHandle.Abort();
                    DebugConsole.ThrowError("Couldn't connect to master server (request timed out)");
                    registeredToMaster = false;
                }
            System.Diagnostics.Debug.WriteLine("took "+sw.ElapsedMilliseconds+" ms");
                
                yield return Status.Running;
            }



            yield return Status.Success;




        }

        private void MasterServerCallBack(IRestResponse response)
        {
            masterServerResponded = true;

            if (response.ErrorException != null)
            {
                DebugConsole.ThrowError("Error while connecting to master server", response.ErrorException);
                registeredToMaster = false;
                return;
            }

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                DebugConsole.ThrowError("Error while connecting to master server (" + response.StatusCode + ": " + response.StatusDescription + ")");
                registeredToMaster = false;
                return;
            }
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            if (gameStarted) inGameHUD.Update((float)Physics.step);

            NetIncomingMessage inc = server.ReadMessage();
            if (inc != null)
            {
                try
                {
                    ReadMessage(inc);
                }
                catch
                {

                }
            }

            // if 30ms has passed
            if (updateTimer < DateTime.Now)
            {
                if (server.ConnectionsCount > 0)
                {
                    if (sparseUpdateTimer < DateTime.Now) SparseUpdate();

                    SendNetworkEvents();
                }

                updateTimer = DateTime.Now + updateInterval;
            }

            if (registeredToMaster && refreshMasterTimer < DateTime.Now)
            {
                CoroutineManager.StartCoroutine(RefreshMaster());

                refreshMasterTimer = DateTime.Now + refreshMasterInterval;
            }
        }

        private void SparseUpdate()
        {
            foreach (Character c in Character.CharacterList)
            {
                bool isClient = false;
                foreach (Client client in connectedClients)
                {
                    if (client.character != c) continue;
                    isClient = true;
                    break;
                }

                if (!isClient && (c.SimPosition==Vector2.Zero || c.SimPosition.Length() < 300.0f))
                {
                    c.LargeUpdateTimer -= 2;
                    new NetworkEvent(c.ID, false);
                }
            }

            if (gameStarted) new NetworkEvent(Submarine.Loaded.ID, false);

            sparseUpdateTimer = DateTime.Now + SparseUpdateInterval;
        }

        private void ReadMessage(NetIncomingMessage inc)
        {
            NetOutgoingMessage outmsg;

            switch (inc.MessageType)
            {
                case NetIncomingMessageType.ConnectionApproval:
                    if (inc.ReadByte() != (byte)PacketTypes.Login) break;
                    
                    DebugConsole.NewMessage("New player has joined the server", Color.White);

                    
                    Client existingClient = connectedClients.Find(c=> c.Connection == inc.SenderConnection);
                    if (existingClient==null)
                    {
                        string userPassword = "", version = "", packageName="", packageHash="", name = "";
                        try
                        {
                            userPassword    = inc.ReadString();
                            version     = inc.ReadString();
                            packageName = inc.ReadString();
                            packageHash = inc.ReadString();
                            name        = inc.ReadString();
                        }
                        catch
                        {
                            inc.SenderConnection.Deny("Connection error - server failed to read your ConnectionApproval message");
                            DebugConsole.NewMessage("Connection error - server failed to read the ConnectionApproval message", Color.Red);
                            break;
                        }

                        if (userPassword != password)
                        {
                            inc.SenderConnection.Deny("Wrong password!");
                            
                            break;
                        }
                        else if (version != Game1.Version.ToString())
                        {
                            inc.SenderConnection.Deny("Subsurface version " + Game1.Version + " required to connect to the server (Your version: " + version + ")");
                            DebugConsole.NewMessage("Connection error - wrong game version", Color.Red);
                            break;
                        } 
                        else if (packageName != Game1.SelectedPackage.Name)
                        {
                            inc.SenderConnection.Deny("Your content package ("+packageName+") doesn't match the server's version (" + Game1.SelectedPackage.Name + ")");
                            DebugConsole.NewMessage("Connection error - wrong content package name", Color.Red);
                            break;
                        }
                        else if (packageHash != Game1.SelectedPackage.MD5hash.Hash)
                        {
                            inc.SenderConnection.Deny("Your content package (MD5: " + packageHash + ") doesn't match the server's version (MD5: " + Game1.SelectedPackage.MD5hash.Hash + ")");
                            DebugConsole.NewMessage("Connection error - wrong content package hash", Color.Red);
                            break;
                        } 
                        else if (connectedClients.Find(c => c.name.ToLower() == name.ToLower())!=null)
                        {
                            inc.SenderConnection.Deny("The name ''" + name + "'' is already in use. Please choose another name.");
                            DebugConsole.NewMessage("Connection error - name already in use", Color.Red);
                            break;
                        }

                        int id = 1;
                        while (connectedClients.Find(c=>c.ID==id)!=null)
                        {
                            id++;
                        }
                        Client newClient = new Client(name, id);
                        newClient.Connection = inc.SenderConnection;
                        newClient.version = version;

                        connectedClients.Add(newClient);
                        
                        inc.SenderConnection.Approve();
                    }
                    else
                    {
                        inc.SenderConnection.Deny();
                    }
                    //Character ch = new Character("Content/Characters/Human/human.xml");

                    break;
                case NetIncomingMessageType.StatusChanged:
                    Debug.WriteLine(inc.SenderConnection + " status changed. " + (NetConnectionStatus)inc.SenderConnection.Status);
                    if (inc.SenderConnection.Status == NetConnectionStatus.Connected)
                    {
                        Client sender = connectedClients.Find(x => x.Connection == inc.SenderConnection);

                        if (sender == null) break;

                        if (sender.version != Game1.Version.ToString())
                        {
                            DisconnectClient(sender, sender.name+" was unable to connect to the server (nonmatching game version)", 
                                "Subsurface version " + Game1.Version + " required to connect to the server (Your version: " + sender.version + ")");
                        }
                        else if (connectedClients.Find(x => x.name == sender.name && x != sender)!=null)
                        {
                            DisconnectClient(sender, sender.name + " was unable to connect to the server (name already in use)",
                                "The name ''"+sender.name+"'' is already in use. Please choose another name.");
                        }
                        else
                        {
                            //AssignJobs();

                            Game1.NetLobbyScreen.AddPlayer(sender);

                            // Notify the client that they have logged in
                            outmsg = server.CreateMessage();

                            outmsg.Write((byte)PacketTypes.LoggedIn);

                            outmsg.Write(sender.ID);

                            outmsg.Write(gameStarted);

                            //notify the client about other clients already logged in
                            outmsg.Write((myClient == null) ? connectedClients.Count - 1 : connectedClients.Count);
                            foreach (Client c in connectedClients)
                            {
                                if (c.Connection == inc.SenderConnection) continue;
                                outmsg.Write(c.name);
                                outmsg.Write(c.ID);
                            }

                            if (myClient != null) outmsg.Write(myClient.name);

                            server.SendMessage(outmsg, inc.SenderConnection, NetDeliveryMethod.ReliableUnordered, 0);
                            
                            //notify other clients about the new client
                            outmsg = server.CreateMessage();
                            outmsg.Write((byte)PacketTypes.PlayerJoined);

                            outmsg.Write(sender.name);
                            outmsg.Write(sender.ID);
                        
                            //send the message to everyone except the client who just logged in
                            SendMessage(outmsg, NetDeliveryMethod.ReliableUnordered, inc.SenderConnection);

                            AddChatMessage(sender.name + " has joined the server", ChatMessageType.Server);

                            UpdateNetLobby(null);
                        }
                    }
                    else if (inc.SenderConnection.Status == NetConnectionStatus.Disconnected)
                    {
                        DisconnectClient(inc.SenderConnection);
                    }
                    
                    break;
                case NetIncomingMessageType.Data:

                    switch (inc.ReadByte())
                    {
                        case (byte)PacketTypes.NetworkEvent:
                            if (!gameStarted) break;
                            if (!NetworkEvent.ReadData(inc)) break;

                            outmsg = server.CreateMessage();
                            outmsg.Write(inc);

                            List<NetConnection> recipients = new List<NetConnection>();

                            foreach (Client client in connectedClients)
                            {
                                if (client.Connection == inc.SenderConnection) continue;
                                if (!client.inGame) continue;

                                recipients.Add(client.Connection);  
                            }

                            if (recipients.Count == 0) break;
                            server.SendMessage(outmsg, recipients, inc.DeliveryMethod, 0);

                            System.Diagnostics.Debug.WriteLine("Sending networkevent (" + outmsg.LengthBytes+" bytes)");
                            
                            break;
                        case (byte)PacketTypes.Chatmessage:
                            ChatMessageType messageType = (ChatMessageType)inc.ReadByte();
                            string message = inc.ReadString();
                            
                            SendChatMessage(message, messageType);

                            break;
                        case (byte)PacketTypes.PlayerLeft:
                            DisconnectClient(inc.SenderConnection);
                            break;
                        case (byte)PacketTypes.CharacterInfo:
                            ReadCharacterData(inc);
                            break;
                    }
                    break;
                case NetIncomingMessageType.WarningMessage:
                    Debug.WriteLine(inc.ReadString());
                    break;
            }
        }

        private void SendMessage(NetOutgoingMessage msg, NetDeliveryMethod deliveryMethod, NetConnection excludedConnection)
        {
            List<NetConnection> recipients = new List<NetConnection>();

            foreach (Client client in connectedClients)
            {
                if (client.Connection != excludedConnection) recipients.Add(client.Connection);                
            }

            if (recipients.Count == 0) return;

            server.SendMessage(msg, recipients, deliveryMethod, 0);  
            
        }

        private void SendNetworkEvents()
        {
            if (NetworkEvent.events.Count == 0) return;
                    
            //System.Diagnostics.Debug.WriteLine("*************************");
            foreach (NetworkEvent networkEvent in NetworkEvent.events)  
            {
                //System.Diagnostics.Debug.WriteLine("networkevent "+networkEvent.ID);

                List<NetConnection> recipients = new List<NetConnection>();

                if (!networkEvent.IsImportant)
                {
                    Entity e = Entity.FindEntityByID(networkEvent.ID);
                    foreach (Client c in connectedClients)
                    {
                        if (c.character==null) continue;
                        if (Vector2.Distance(e.SimPosition, c.character.SimPosition) > 2000.0f) continue;

                        recipients.Add(c.Connection);
                    }
                }
                else
                {
                    foreach (Client c in connectedClients)
                    {
                        if (c.character == null) continue;

                        recipients.Add(c.Connection);
                    }
                }

                if (recipients.Count == 0) return;

                NetOutgoingMessage message = server.CreateMessage();
                message.Write((byte)PacketTypes.NetworkEvent);
                //if (!networkEvent.IsClient) continue;
                            
                networkEvent.FillData(message);

                System.Diagnostics.Debug.WriteLine("Sending networkevent " + Entity.FindEntityByID(networkEvent.ID).ToString() + " (" + message.LengthBytes + " bytes)");

                if (server.ConnectionsCount>0)
                {
                    server.SendMessage(message, recipients, 
                        (networkEvent.IsImportant) ? NetDeliveryMethod.Unreliable : NetDeliveryMethod.ReliableUnordered, 0);  
                }
                            
            }
            NetworkEvent.events.Clear();                       
        }


        public bool StartGame(GUIButton button, object obj)
        {
            Submarine selectedMap = Game1.NetLobbyScreen.SelectedMap as Submarine;

            if (selectedMap == null)
            {
                Game1.NetLobbyScreen.SubList.Flash();
                return false;
            }

            int seed = DateTime.Now.Millisecond;
            Rand.SetSyncedSeed(seed);

            AssignJobs();            
           
            //selectedMap.Load();

            Game1.GameSession = new GameSession(selectedMap, "", Game1.NetLobbyScreen.SelectedMode);
            Game1.GameSession.StartShift(Game1.NetLobbyScreen.GameDuration, Game1.NetLobbyScreen.LevelSeed);
            //EventManager.SelectEvent(Game1.netLobbyScreen.SelectedEvent);

            List<CharacterInfo> characterInfos = new List<CharacterInfo>();

            foreach (Client client in connectedClients)
            {
                client.inGame = true;

                WayPoint spawnPoint = WayPoint.GetRandom(SpawnType.Human);

                if (client.characterInfo==null)
                {
                    client.characterInfo = new CharacterInfo(Character.HumanConfigFile, client.name);
                }
                characterInfos.Add(client.characterInfo);

                client.characterInfo.Job = new Job(client.assignedJob);

                //client.character = new Character(client.characterInfo, (spawnPoint == null) ? Vector2.Zero : spawnPoint.SimPosition, true);
            }

            List<Character> crew = new List<Character>();
            WayPoint[] assignedWayPoints = WayPoint.SelectCrewSpawnPoints(characterInfos);

            for (int i = 0; i < connectedClients.Count; i++ )
            {
                connectedClients[i].character = new Character(
                    connectedClients[i].characterInfo, assignedWayPoints[i], true);
                connectedClients[i].character.GiveJobItems(assignedWayPoints[i]);

                crew.Add(connectedClients[i].character);
            }

            //todo: fix
            if (myClient != null)
            {
                WayPoint spawnPoint = WayPoint.GetRandom(SpawnType.Human);
                CharacterInfo ch = new CharacterInfo(Character.HumanConfigFile, myClient.name);
                myClient.character = new Character(ch, (spawnPoint == null) ? Vector2.Zero : spawnPoint.SimPosition);
            }

            //foreach (Client client in connectedClients)
            //{
            NetOutgoingMessage msg = server.CreateMessage();
            msg.Write((byte)PacketTypes.StartGame);

            msg.Write(seed);

            msg.Write(Game1.NetLobbyScreen.LevelSeed);

            msg.Write(Game1.NetLobbyScreen.SelectedMap.Name);
            msg.Write(Game1.NetLobbyScreen.SelectedMap.Hash.Hash);
                
            msg.Write(Game1.NetLobbyScreen.GameDuration.TotalMinutes);

            //WriteCharacterData(msg, client.name, client.character);

            msg.Write((myClient == null) ? connectedClients.Count : connectedClients.Count+1);
            foreach (Client client in connectedClients)
            {
                //if (otherClient == client) continue;
                msg.Write(client.ID);
                WriteCharacterData(msg, client.name, client.character);
            }

            if (myClient!=null)
            {
                WriteCharacterData(msg, myClient.name, myClient.character);
            }

                SendMessage(msg, NetDeliveryMethod.ReliableUnordered, null);
            //}

            gameStarted = true;

            Game1.GameScreen.Cam.TargetPos = Vector2.Zero;

            Game1.GameScreen.Select();


            CreateCrewFrame(crew);

            return true;
        }

        private bool EndButtonHit(GUIButton button, object obj)
        {
            Game1.GameSession.gameMode.End("Server admin has ended the round");

            return true;
        }

        public void EndGame(string endMessage)
        {


            if (connectedClients.Count > 0)
            {
                NetOutgoingMessage msg = server.CreateMessage();
                msg.Write((byte)PacketTypes.EndGame);
                msg.Write(endMessage);

                if (server.ConnectionsCount > 0)
                {
                    server.SendMessage(msg, server.Connections, NetDeliveryMethod.ReliableOrdered, 0);
                }

                foreach (Client client in connectedClients)
                {
                    client.character = null;
                    client.inGame = false;
                }
            }

            Submarine.Unload();
                      
            gameStarted = false;

            Game1.NetLobbyScreen.Select();

            DebugConsole.ThrowError(endMessage);
        }

        private void DisconnectClient(NetConnection senderConnection)
        {
            Client client = connectedClients.Find(x => x.Connection == senderConnection);
            if (client != null) DisconnectClient(client);
        }

        private void DisconnectClient(Client client, string msg = "", string targetmsg = "")
        {
            if (client == null) return;

            if (gameStarted && client.character != null) client.character.Kill(true);

            if (msg == "") msg = client.name + " has left the server";
            if (targetmsg == "") targetmsg = "You have left the server";
            
            NetOutgoingMessage outmsg = server.CreateMessage();
            outmsg.Write((byte)PacketTypes.KickedOut);
            outmsg.Write(targetmsg);
            server.SendMessage(outmsg, client.Connection, NetDeliveryMethod.ReliableUnordered, 0);

            connectedClients.Remove(client);

            outmsg = server.CreateMessage();
            outmsg.Write((byte)PacketTypes.PlayerLeft);
            outmsg.Write(client.ID);
            outmsg.Write(msg);

            Game1.NetLobbyScreen.RemovePlayer(client);

            if (server.Connections.Count > 0)
            {
                server.SendMessage(outmsg, server.Connections, NetDeliveryMethod.ReliableUnordered, 0);
            }

            AddChatMessage(msg, ChatMessageType.Server);
        }

        public void KickPlayer(string playerName)
        {
            playerName = playerName.ToLower();
            foreach (Client c in connectedClients)
            {
                if (c.name.ToLower() == playerName) KickClient(c);
                break;               
            }
        }

        private void KickClient(Client client)
        {
            if (client == null) return;

            DisconnectClient(client, client.name + " has been kicked from the server", "You have been kicked from the server");
        }

        public void NewTraitor(Client traitor, Client target)
        {
            new GUIMessageBox("New traitor", traitor.name + " is the traitor and the target is " + target.name+".");

            NetOutgoingMessage msg = server.CreateMessage();
            msg.Write((byte)PacketTypes.Traitor);
            msg.Write(target.name);
            if (server.Connections.Count > 0)
            {
                server.SendMessage(msg, traitor.Connection, NetDeliveryMethod.ReliableUnordered, 0);
            }
        }

        public override void Draw(Microsoft.Xna.Framework.Graphics.SpriteBatch spriteBatch)
        {
            base.Draw(spriteBatch);

            if (!Game1.DebugDraw) return;

            int width = 200, height = 300;
            int x = Game1.GraphicsWidth - width, y = (int)(Game1.GraphicsHeight*0.3f);

            GUI.DrawRectangle(spriteBatch, new Rectangle(x,y,width,height), Color.Black*0.7f, true);
            spriteBatch.DrawString(GUI.Font, "Network statistics:", new Vector2(x+10, y+10), Color.White);

            spriteBatch.DrawString(GUI.SmallFont, "Connections: "+server.ConnectionsCount, new Vector2(x + 10, y + 30), Color.White);
            spriteBatch.DrawString(GUI.SmallFont, "Received bytes: " + server.Statistics.ReceivedBytes, new Vector2(x + 10, y + 45), Color.White);
            spriteBatch.DrawString(GUI.SmallFont, "Received packets: " + server.Statistics.ReceivedPackets, new Vector2(x + 10, y + 60), Color.White);

            spriteBatch.DrawString(GUI.SmallFont, "Sent bytes: " + server.Statistics.SentBytes, new Vector2(x + 10, y + 75), Color.White);
            spriteBatch.DrawString(GUI.SmallFont, "Sent packets: " + server.Statistics.SentPackets, new Vector2(x + 10, y + 90), Color.White);
            
            y += 110;
            foreach (Client c in connectedClients)
            {
                spriteBatch.DrawString(GUI.SmallFont, c.name + ":", new Vector2(x + 10, y), Color.White);
                spriteBatch.DrawString(GUI.SmallFont, "- avg roundtrip " + c.Connection.AverageRoundtripTime+" s", new Vector2(x + 20, y + 15), Color.White);
                y += 50;
            
            }

        }

        public bool UpdateNetLobby(object obj)
        {
            NetOutgoingMessage msg = server.CreateMessage();
            msg.Write((byte)PacketTypes.UpdateNetLobby);
            Game1.NetLobbyScreen.WriteData(msg);

            if (server.Connections.Count > 0)
            {
                server.SendMessage(msg, server.Connections, NetDeliveryMethod.ReliableUnordered, 0);
            }

            return true;
        }

        public override void SendChatMessage(string message, ChatMessageType type = ChatMessageType.Server)
        {
            AddChatMessage(message, type);

            if (server.Connections.Count == 0) return;

            NetOutgoingMessage msg = server.CreateMessage();
            msg.Write((byte)PacketTypes.Chatmessage);
            msg.Write((byte)type);
            msg.Write(message);

            if (type==ChatMessageType.Dead)
            {
                List<NetConnection> recipients = new List<NetConnection>();
                foreach (Client c in connectedClients)
                {
                    if (c.character != null && c.character.IsDead) recipients.Add(c.Connection);                    
                }
                if (recipients.Count>0)
                {
                    server.SendMessage(msg, recipients, NetDeliveryMethod.ReliableUnordered, 0);
                }                
            }
            else
            {
                server.SendMessage(msg, server.Connections, NetDeliveryMethod.ReliableUnordered, 0);
            }
            
        }

        private void ReadCharacterData(NetIncomingMessage message)
        {
            string name = "";
            Gender gender = Gender.Male;
            int headSpriteId = 0;

            try
            {
                name         = message.ReadString();
                gender       = message.ReadBoolean() ? Gender.Male : Gender.Female;
                headSpriteId    = message.ReadInt32();
            }
            catch
            {
                name = "";
                gender = Gender.Male;
                headSpriteId = 0;
            }


            List<JobPrefab> jobPreferences = new List<JobPrefab>();
            int count = message.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string jobName = message.ReadString();
                JobPrefab jobPrefab = JobPrefab.List.Find(jp => jp.Name == jobName);
                if (jobPrefab != null) jobPreferences.Add(jobPrefab);
            }

            foreach (Client c in connectedClients)
            {
                if (c.Connection != message.SenderConnection) continue;

                c.characterInfo = new CharacterInfo(Character.HumanConfigFile, name, gender);
                c.characterInfo.HeadSpriteId = headSpriteId;
                c.jobPreferences = jobPreferences;
                break;
            }
        }

        private void WriteCharacterData(NetOutgoingMessage message, string name, Character character)
        {
            message.Write(name);
            message.Write(character.ID);
            message.Write(character.Info.Gender == Gender.Female);
            message.Write(character.Inventory.ID);

            message.Write(character.Info.HeadSpriteId);

            message.Write(character.SimPosition.X);
            message.Write(character.SimPosition.Y);

            message.Write(character.Info.Job.Name);
        }

        private void AssignJobs()
        {
            List<Client> unassigned = new List<Client>(connectedClients);

            int[] assignedClientCount = new int[JobPrefab.List.Count];

            //if any of the players has chosen a job that is Always Allowed, give them that job
            for (int i = unassigned.Count - 1; i >= 0; i--)
            {
                if (!unassigned[i].jobPreferences[0].AllowAlways) continue;
                unassigned[i].assignedJob = unassigned[i].jobPreferences[0];
                unassigned.RemoveAt(i);
            }

            //go throught the jobs whose MinNumber>0 (i.e. at least one crew member has to have the job)
            bool unassignedJobsFound = true;
            while (unassignedJobsFound && unassigned.Count > 0)
            {
                unassignedJobsFound = false;
                for (int i = 0; i < JobPrefab.List.Count; i++)
                {
                    if (unassigned.Count == 0) break;
                    if (JobPrefab.List[i].MinNumber < 1 || assignedClientCount[i] >= JobPrefab.List[i].MinNumber) continue;

                    //find the client that wants the job the most, or force it to random client if none of them want it
                    Client assignedClient = FindClientWithJobPreference(unassigned, JobPrefab.List[i], true);

                    assignedClient.assignedJob = JobPrefab.List[i];

                    assignedClientCount[i]++;
                    unassigned.Remove(assignedClient);

                    //the job still needs more crew members, set unassignedJobsFound to true to keep the while loop running
                    if (assignedClientCount[i] < JobPrefab.List[i].MinNumber) unassignedJobsFound = true;
                }
            }

            for (int preferenceIndex = 0; preferenceIndex < 3; preferenceIndex++)
            {
                for (int i = unassigned.Count - 1; i >= 0; i--)
                {
                    int jobIndex = JobPrefab.List.FindIndex(jp => jp == unassigned[i].jobPreferences[preferenceIndex]);

                    //if there's enough crew members assigned to the job already, continue
                    if (assignedClientCount[jobIndex] >= JobPrefab.List[jobIndex].MaxNumber) continue;

                    unassigned[i].assignedJob = JobPrefab.List[jobIndex];

                    assignedClientCount[jobIndex]++;
                    unassigned.RemoveAt(i);
                }
            }

            //UpdateNetLobby(null);

        }

        private Client FindClientWithJobPreference(List<Client> clients, JobPrefab job, bool forceAssign = false)
        {
            int bestPreference = 0;
            Client preferredClient = null;
            foreach (Client c in clients)
            {
                int index = c.jobPreferences.FindIndex(jp => jp == job);
                if (preferredClient == null || index < bestPreference)
                {
                    bestPreference = index;
                    preferredClient = c;
                }
            }

            //none of the clients wants the job
            if (forceAssign && preferredClient == null)
            {
                preferredClient = clients[Rand.Int(clients.Count)];
            }

            return preferredClient;
        }


        private byte PlayerCountToByte(int playerCount, int maxPlayers)
        {
            byte byteVal = (byte)playerCount;

            byteVal |= (byte)((maxPlayers-1) << 4);

            return byteVal;
        }

        /// <summary>
        /// sends some random data to the clients
        /// use for debugging purposes
        /// </summary>
        public void SendRandomData()
        {
            NetOutgoingMessage msg = server.CreateMessage();
            switch (Rand.Int(5))
            {
                case 0:
                    msg.Write((byte)PacketTypes.NetworkEvent);
                    msg.Write((byte)Enum.GetNames(typeof(NetworkEventType)).Length);
                    msg.Write(Rand.Int(MapEntity.mapEntityList.Count));
                    break;
                case 1:
                    msg.Write((byte)PacketTypes.NetworkEvent);
                    msg.Write((byte)NetworkEventType.UpdateComponent);
                    msg.Write((int)Item.itemList[Rand.Int(Item.itemList.Count)].ID);
                    msg.Write(Rand.Int(8));
                    break;
                case 2:
                    msg.Write((byte)Enum.GetNames(typeof(PacketTypes)).Length);
                    break;
                case 3:
                    msg.Write((byte)PacketTypes.UpdateNetLobby);
                    break;
            }

            int bitCount = Rand.Int(100);
            for (int i = 0; i < bitCount; i++)
            {
                msg.Write((Rand.Int(2) == 0) ? true : false);
            }
            SendMessage(msg, (Rand.Int(2) == 0) ? NetDeliveryMethod.ReliableOrdered : NetDeliveryMethod.Unreliable, null);

        }

        public override void Disconnect()
        {
            server.Shutdown("");
        }
    }

    class Client
    {
        public string name;
        public int ID;

        public Character character;
        public CharacterInfo characterInfo;
        public NetConnection Connection { get; set; }
        public string version;
        public bool inGame;

        public List<JobPrefab> jobPreferences;
        public JobPrefab assignedJob;

        public Client(string name, int ID)
        {
            this.name = name;
            this.ID = ID;

            jobPreferences = new List<JobPrefab>(JobPrefab.List.GetRange(0,3));
        }
    }
}
