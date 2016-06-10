﻿using ClientCore;
using DTAClient.Online.EventArguments;
using Rampastring.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DTAClient.Online
{
    /// <summary>
    /// The CnCNet connection handler.
    /// </summary>
    public class Connection
    {
        const int MAX_NAME_LENGTH = 16;

        public Connection(IConnectionManager connectionManager)
        {
            this.connectionManager = connectionManager;
        }

        IConnectionManager connectionManager;

        /// <summary>
        /// The list of CnCNet / GameSurge IRC servers to connect to.
        /// </summary>
        private static readonly IList<Server> Servers = new List<Server>
        {
            new Server("irc.gamesurge.net", "GameSurge", new int[1] { 6667 }),
            new Server("Burstfire.UK.EU.GameSurge.net", "London, UK", new int[3] { 6667, 6668, 7000 }),
            new Server("ColoCrossing.IL.US.GameSurge.net", "Chicago, IL", new int[5] { 6660, 6666, 6667, 6668, 6669 }),
            new Server("Gameservers.NJ.US.GameSurge.net", "Newark, NJ", new int[7] { 6665, 6666, 6667, 6668, 6669, 7000, 8080 }),
            new Server("Krypt.CA.US.GameSurge.net", "Santa Ana, CA", new int[4] { 6666, 6667, 6668, 6669 }),
            new Server("NuclearFallout.WA.US.GameSurge.net", "Seattle, WA", new int[2] { 6667, 5960 }),
            new Server("Portlane.SE.EU.GameSurge.net", "Stockholm, Sweden", new int[5] { 6660, 6666, 6667, 6668, 6669 }),
            new Server("Prothid.NY.US.GameSurge.Net", "NYC, NY", new int[7] { 5960, 6660, 6666, 6667, 6668, 6669, 6697 }),
            new Server("TAL.DE.EU.GameSurge.net", "Wuppertal, Germany", new int[5] { 6660, 6666, 6667, 6668, 6669 })
        }.AsReadOnly();

        bool _isConnected = false;
        public bool IsConnected
        {
            get { return _isConnected; }
        }

        bool _attemptingConnection = false;
        public bool AttemptingConnection
        {
            get { return _attemptingConnection; }
        }

        private List<QueuedMessage> MessageQueue = new List<QueuedMessage>();
        private TimeSpan MessageQueueDelay;

        private NetworkStream serverStream;
        private TcpClient tcpClient;

        private volatile bool connectionCut = false;
        private volatile bool welcomeMessageReceived = false;
        private volatile bool sendQueueExited = false;
        private volatile bool disconnect = false;

        private string overMessage;

        private readonly Encoding encoding = Encoding.UTF8;

        private static readonly object locker = new object();
        private static readonly object messageQueueLocker = new object();

        /// <summary>
        /// Attempts to connects to CnCNet without blocking the calling thread.
        /// </summary>
        public void ConnectAsync()
        {
            if (_isConnected)
                throw new Exception("The client is already connected!");

            welcomeMessageReceived = false;
            connectionCut = false;
            _attemptingConnection = true;
            disconnect = false;

            MessageQueueDelay = TimeSpan.FromMilliseconds(DomainController.Instance().GetSendSleepInMs());

            Thread connection = new Thread(ConnectToServer);
            connection.Start();
        }

        /// <summary>
        /// Attempts to connect to CnCNet.
        /// </summary>
        private void ConnectToServer()
        {
            foreach (Server server in Servers)
            {
                try
                {
                    for (var i = 0; i < server.Ports.Length; i++)
                    {
                        connectionManager.OnAttemptedServerChanged(server.Name);

                        TcpClient client = new TcpClient(AddressFamily.InterNetwork);
                        var result = client.BeginConnect(server.Host, server.Ports[i], null, null);
                        result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3), false);

                        Logger.Log("Attempting connection to " + server.Host + ":" + server.Ports[i]);

                        if (!client.Connected)
                        {
                            Logger.Log("Connecting to " + server.Host + " port " + server.Ports[i] + " timed out!");
                            continue; // Start all over again, using the next port
                        }
                        else if (client.Connected)
                        {
                            Logger.Log("Succesfully connected to " + server.Host + " on port " + server.Ports[i]);
                            client.EndConnect(result);

                            _isConnected = true;
                            _attemptingConnection = false;

                            connectionManager.OnConnected();

                            Thread sendQueueHandler = new Thread(RunSendQueue);
                            sendQueueHandler.Start();

                            HandleComm(client);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("Unable to connect to the server. " + ex.Message);
                }
            }

            Logger.Log("Connecting to CnCNet failed!");
            _attemptingConnection = false;
            connectionManager.OnConnectAttemptFailed();
        }

        private void HandleComm(object client)
        {
            tcpClient = new TcpClient();
            tcpClient = (TcpClient)client;
            serverStream = tcpClient.GetStream();
            serverStream.ReadTimeout = 1000;
            int errorTimes = 0;

            byte[] message = new byte[1024];
            int bytesRead;

            Register();

            Timer timer = new Timer(new TimerCallback(AutoPing), null, 30000, 120000);

            connectionCut = true;

            while (true)
            {
                bytesRead = 0;

                if (disconnect)
                {
                    connectionManager.OnDisconnected();
                    connectionCut = false; // This disconnect is intentional
                    break;
                }

                try
                {
                    bytesRead = serverStream.Read(message, 0, 1024);
                }
                catch (Exception ex)
                {
                    errorTimes++;

                    if (errorTimes > 30)
                    {
                        Logger.Log("Disconnected from CnCNet due to a socket error. Message: " + ex.Message);
                        connectionManager.OnConnectionLost(ex.Message);
                        break;
                    }
                    else if (disconnect)
                    {
                        connectionManager.OnDisconnected();
                        connectionCut = false; // This disconnect is intentional
                        break;
                    }

                    continue;
                }

                if (bytesRead == 0)
                {
                    errorTimes++;

                    if (errorTimes > 30)
                    {
                        Logger.Log("Disconnected from CnCNet.");
                        connectionManager.OnConnectionLost("Server disconnected.");
                        break;
                    }

                    continue;
                }

                errorTimes = 0;

                // A message has been succesfully received
                string msg = encoding.GetString(message, 0, bytesRead);
                Logger.Log("Message received: " + msg);

                HandleMessage(msg);
                timer.Change(30000, 30000);
            }

            timer.Dispose();

            _isConnected = false;
            disconnect = false;

            if (connectionCut)
            {
                while (!sendQueueExited)
                    Thread.Sleep(100);

                Logger.Log("Attempting to reconnect to CnCNet.");

                connectionManager.OnReconnectAttempt();
            }
        }

        public void Disconnect()
        {
            disconnect = true;
            SendMessage("QUIT");

            tcpClient.Close();
            serverStream.Close();
        }

        #region Handling commands

        /// <summary>
        /// Checks if a message from the IRC server is a partial or full
        /// message, and handles it accordingly.
        /// </summary>
        /// <param name="message">The message.</param>
        private void HandleMessage(string message)
        {
            string msg = overMessage + message;
            overMessage = "";
            while (true)
            {
                int commandEndIndex = msg.IndexOf("\n");

                if (commandEndIndex == -1)
                {
                    overMessage = msg;
                    break;
                }
                else if (msg.Length != commandEndIndex + 1)
                {
                    string command = msg.Substring(0, commandEndIndex - 1);
                    PerformCommand(command);

                    msg = msg.Remove(0, commandEndIndex + 1);
                }
                else
                {
                    string command = msg.Substring(0, msg.Length - 1);
                    PerformCommand(command);
                    break;
                }
            }
        }

        /// <summary>
        /// Handles a specific command received from the IRC server.
        /// </summary>
        private void PerformCommand(string message)
        {
            string prefix = String.Empty;
            string command = String.Empty;
            message = message.Replace("\r", String.Empty);
            List<string> parameters = new List<string>();
            ParseIrcMessage(message, out prefix, out command, out parameters);
            string paramString = String.Empty;
            foreach (string param in parameters) { paramString = paramString + param + ","; }
            Logger.Log("RMP: " + prefix + " " + command + " " + paramString);

            try
            {
                bool success = false;
                int commandNumber = -1;
                success = Int32.TryParse(command, out commandNumber);

                if (success)
                {
                    string serverMessagePart = prefix + ": ";

                    switch (commandNumber)
                    {
                        // Command descriptions from https://www.alien.net.au/irc/irc2numerics.html

                        case 001: // Welcome message
                            message = serverMessagePart + parameters[1];
                            welcomeMessageReceived = true;
                            connectionManager.OnWelcomeMessageReceived(message);
                            break;
                        case 002: // "Your host is x, running version y"
                        case 003: // "This server was created..."
                        case 251: // There are <int> users and <int> invisible on <int> servers
                        case 255: // I have <int> clients and <int> servers
                        case 265: // Local user count
                        case 266: // Global user count
                        case 401: // Used to indicate the nickname parameter supplied to a command is currently unused
                        case 403: // Used to indicate the given channel name is invalid, or does not exist
                        case 404: // Used to indicate that the user does not have the rights to send a message to a channel
                        case 432: // Invalid nickname on registration
                        case 461: // Returned by the server to any command which requires more parameters than the number of parameters given
                        case 465: // Returned to a client after an attempt to register on a server configured to ban connections from that client
                            message = serverMessagePart + parameters[1];
                            connectionManager.OnGenericServerMessageReceived(message);
                            break;
                        case 252: // Number of operators online
                        case 254: // Number of channels formed
                            message = serverMessagePart + parameters[1] + " " + parameters[2];
                            connectionManager.OnGenericServerMessageReceived(message);
                            break;
                        case 301: // AWAY message
                            string awayTarget = parameters[0];
                            if (awayTarget != ProgramConstants.PLAYERNAME)
                                break;
                            string awayPlayer = parameters[1];
                            string awayReason = parameters[2];
                            connectionManager.OnAwayMessageReceived(awayPlayer, awayReason);
                            break;
                        case 332: // Channel topic message
                            string _target = parameters[0];
                            if (_target != ProgramConstants.PLAYERNAME)
                                break;
                            connectionManager.OnChannelTopicReceived(parameters[1], parameters[2]);
                            break;
                        case 353: // User list (reply to NAMES)
                            string target = parameters[0];
                            if (target != ProgramConstants.PLAYERNAME)
                                break;
                            string channelName = parameters[2];
                            string[] users = parameters[3].Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            connectionManager.OnUserListReceived(channelName, users);
                            break;
                        case 352: // Reply to WHO query
                            string wUserName = parameters[5];
                            string extraInfo = parameters[7];
                            connectionManager.OnWhoReplyReceived(wUserName, extraInfo);
                            break;
                        case 433: // Name already in use
                            message = serverMessagePart + parameters[1] + ": " + parameters[2];
                            connectionManager.OnGenericServerMessageReceived(message);
                            Disconnect();
                            break;
                        case 451: // Not registered
                            Register();
                            connectionManager.OnGenericServerMessageReceived(message);
                            break;
                        case 471: // Returned when attempting to join a channel that is full (basically, player limit met)
                            string fullChannelName = parameters[1];
                            connectionManager.OnChannelFull(fullChannelName);
                            break;
                        case 474: // Returned when attempting to join a channel a user is banned from
                            message = serverMessagePart + parameters[1];
                            connectionManager.OnGenericServerMessageReceived(message);
                            break;
                        case 475: // Returned when attempting to join a key-locked channel either without a key or with the wrong key
                            string invalidPasswordChannelName = parameters[1];
                            connectionManager.OnIncorrectChannelPassword(parameters[1]);
                            break;
                    }
                }
                else
                {
                    switch (command)
                    {
                        case "NOTICE":
                            int noticeExclamIndex = prefix.IndexOf('!');
                            if (noticeExclamIndex > -1)
                            {
                                if (parameters.Count > 1 && parameters[1][0] == 1)//Conversions.IntFromString(parameters[1].Substring(0, 1), -1) == 1)
                                {
                                    // CTCP
                                    string channelName = parameters[0];
                                    string ctcpMessage = parameters[1];
                                    ctcpMessage = ctcpMessage.Remove(0, 1).Remove(ctcpMessage.Length - 2);
                                    string ctcpSender = prefix.Substring(0, noticeExclamIndex);
                                    connectionManager.OnCTCPParsed(channelName, ctcpSender, ctcpMessage);

                                    return;
                                }
                                else
                                {
                                    string noticeUserName = prefix.Substring(0, prefix.IndexOf('!'));
                                    string notice = parameters[parameters.Count - 1];
                                    connectionManager.OnNoticeMessageParsed(notice, noticeUserName);
                                    break;
                                }
                            }
                            string noticeParamString = String.Empty;
                            foreach (string param in parameters)
                                noticeParamString = noticeParamString + param + " ";
                            connectionManager.OnGenericServerMessageReceived(prefix + " " + noticeParamString);
                            break;
                        case "JOIN":
                            string channel = parameters[0];
                            string userName = prefix.Substring(0, prefix.IndexOf('!'));
                            string ipAddress = prefix.Substring(prefix.IndexOf('!') + 1);
                            connectionManager.OnUserJoinedChannel(channel, userName, ipAddress);
                            break;
                        case "PART":
                            string pChannel = parameters[0];
                            string pUserName = prefix.Substring(0, prefix.IndexOf('!'));
                            connectionManager.OnUserLeftChannel(pChannel, pUserName);
                            break;
                        case "QUIT":
                            string qUserName = prefix.Substring(0, prefix.IndexOf('!'));
                            connectionManager.OnUserQuitIRC(qUserName);
                            break;
                        case "PRIVMSG":
                            if (parameters.Count > 1 && Convert.ToInt32(parameters[1][0]) == 1 && !parameters[1].Contains("ACTION"))
                            {
                                goto case "NOTICE";
                            }
                            string pmsgUserName = prefix.Substring(0, prefix.IndexOf('!'));
                            string[] recipients = new string[parameters.Count - 1];
                            for (int pid = 0; pid < parameters.Count - 1; pid++)
                                recipients[pid] = parameters[pid];
                            string privmsg = parameters[parameters.Count - 1];
                            foreach (string recipient in recipients)
                            {
                                if (recipient.StartsWith("#"))
                                    connectionManager.OnChatMessageReceived(recipient, pmsgUserName, privmsg);
                                else if (recipient == ProgramConstants.PLAYERNAME)
                                    connectionManager.OnPrivateMessageReceived(pmsgUserName, privmsg);
                                //else if (pmsgUserName == ProgramConstants.PLAYERNAME)
                                //{
                                //    DoPrivateMessageSent(privmsg, recipient);
                                //}
                            }
                            break;
                        case "MODE":
                            string modeUserName = prefix.Substring(0, prefix.IndexOf('!'));
                            string modeChannelName = parameters[0];
                            string modeString = parameters[1];
                            connectionManager.OnChannelModesChanged(modeUserName, modeChannelName, modeString);
                            break;
                        case "KICK":
                            string kickChannelName = parameters[0];
                            string kickUserName = parameters[1];
                            connectionManager.OnUserKicked(kickChannelName, kickUserName);
                            break;
                        case "ERROR":
                            connectionManager.OnErrorReceived(message);
                            break;
                        case "PING":
                            if (parameters.Count > 0)
                            {
                                QueueMessage(new QueuedMessage("PONG " + parameters[0], QueuedMessageType.SYSTEM_MESSAGE, 5000));
                                Logger.Log("PONG " + parameters[0]);
                            }
                            else
                            {
                                QueueMessage(new QueuedMessage("PONG", QueuedMessageType.SYSTEM_MESSAGE, 5000));
                                Logger.Log("PONG");
                            }
                            break;
                    }
                }
            }
            catch
            {
                Logger.Log("Warning: Failed to parse command " + message);
            }
        }

        /// <summary>
        /// Parses a single IRC message received from the server.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="prefix">(out) The message prefix.</param>
        /// <param name="command">(out) The command.</param>
        /// <param name="parameters">(out) The parameters of the command.</param>
        private void ParseIrcMessage(string message, out string prefix, out string command, out List<string> parameters)
        {
            int prefixEnd = -1, trailingStart = message.Length;
            string trailing = null;
            prefix = command = String.Empty;
            parameters = new List<string>();

            // Grab the prefix if it is present. If a message begins
            // with a colon, the characters following the colon until
            // the first space are the prefix.
            if (message.StartsWith(":"))
            {
                prefixEnd = message.IndexOf(" ");
                prefix = message.Substring(1, prefixEnd - 1);
            }

            // Grab the trailing if it is present. If a message contains
            // a space immediately following a colon, all characters after
            // the colon are the trailing part.
            trailingStart = message.IndexOf(" :");
            if (trailingStart >= 0)
                trailing = message.Substring(trailingStart + 2);
            else
                trailingStart = message.Length;

            // Use the prefix end position and trailing part start
            // position to extract the command and parameters.
            var commandAndParameters = message.Substring(prefixEnd + 1, trailingStart - prefixEnd - 1).Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (commandAndParameters.Length == 0)
            {
                command = String.Empty;
                Logger.Log("Nonexistant command!");
                return;
            }

            // The command will always be the first element of the array.
            command = commandAndParameters[0];

            // The rest of the elements are the parameters, if they exist.
            // Skip the first element because that is the command.
            if (commandAndParameters.Length > 1)
            {
                for (int id = 1; id < commandAndParameters.Length; id++)
                {
                    parameters.Add(commandAndParameters[id]);
                }
            }

            // If the trailing part is valid add the trailing part to the
            // end of the parameters.
            if (!String.IsNullOrEmpty(trailing))
                parameters.Add(trailing);
        }

        #endregion

        #region Sending commands

        private void RunSendQueue()
        {
            while (_isConnected)
            {
                string message = String.Empty;

                lock (messageQueueLocker)
                {
                    if (MessageQueue.Count > 0)
                    {
                        message = MessageQueue[0].Command;
                        MessageQueue.RemoveAt(0);
                    }
                }

                if (String.IsNullOrEmpty(message))
                {
                    Thread.Sleep(10);
                    continue;
                }

                SendMessage(message);

                Thread.Sleep(MessageQueueDelay);
            }

            lock (messageQueueLocker)
            {
                MessageQueue.Clear();
            }

            sendQueueExited = true;
        }

        /// <summary>
        /// Sends a PING message to the server to indicate that we're still connected.
        /// </summary>
        /// <param name="data">Just a dummy parameter so that this matches the delegate System.Threading.TimerCallback.</param>
        private void AutoPing(object data)
        {
            SendMessage("PING LAG" + new Random().Next(100000, 999999));
        }

        /// <summary>
        /// Registers the user.
        /// </summary>
        private void Register()
        {
            if (welcomeMessageReceived)
                return;

            Logger.Log("Registering.");

            string realname = ProgramConstants.GAME_VERSION + " " + DomainController.Instance().GetDefaultGame() + " CnCNet";

            SendMessage(string.Format("USER {0} 0 * :{1}", "DTA" + new Random().Next(10000, 99999).ToString(), realname));
            SendMessage("NICK " + ProgramConstants.PLAYERNAME);
        }

        public void QueueMessage(QueuedMessageType type, int priority, string message)
        {
            QueuedMessage qm = new QueuedMessage(message, type, priority);
            QueueMessage(qm);
        }

        /// <summary>
        /// Send a message to the CnCNet server.
        /// </summary>
        /// <param name="message">The message to send.</param>
        private void SendMessage(string message)
        {
            if (serverStream == null)
                return;

            Logger.Log("SRM: " + message);

            byte[] buffer = encoding.GetBytes(message + "\r\n");
            if (serverStream.CanWrite)
            {
                serverStream.Write(buffer, 0, buffer.Length);
                serverStream.Flush();
            }
        }

        /// <summary>
        /// Adds a message to the send queue.
        /// </summary>
        /// <param name="qm">The message to queue.</param>
        public void QueueMessage(QueuedMessage qm)
        {
            if (!_isConnected)
                return;

            lock (messageQueueLocker)
            {
                switch (qm.MessageType)
                {
                    case QueuedMessageType.GAME_BROADCASTING_MESSAGE:
                    case QueuedMessageType.GAME_PLAYERS_MESSAGE:
                    case QueuedMessageType.GAME_SETTINGS_MESSAGE:
                    case QueuedMessageType.GAME_PLAYERS_READY_STATUS_MESSAGE:
                    case QueuedMessageType.GAME_LOCKED_MESSAGE:
                    case QueuedMessageType.GAME_GET_READY_MESSAGE:
                    case QueuedMessageType.GAME_NOTIFICATION_MESSAGE:
                    case QueuedMessageType.GAME_HOSTING_MESSAGE:
                    case QueuedMessageType.WHOIS_MESSAGE:
                        AddSpecialQueuedMessage(qm);
                        break;
                    default:
                        int placeInQueue = MessageQueue.FindIndex(m => m.Priority < qm.Priority);
                        if (ProgramConstants.LOG_LEVEL > 1)
                            Logger.Log("QM Undefined: " + qm.Command + " " + placeInQueue);
                        if (placeInQueue == -1)
                            MessageQueue.Add(qm);
                        else
                            MessageQueue.Insert(placeInQueue, qm);
                        break;
                }
            }
        }

        /// <summary>
        /// Adds a "special" message to the send queue that replaces
        /// previous messages of the same type in the queue.
        /// </summary>
        /// <param name="qm">The message to queue.</param>
        private void AddSpecialQueuedMessage(QueuedMessage qm)
        {
            int broadcastingMessageIndex = MessageQueue.FindIndex(m => m.MessageType == qm.MessageType);

            if (broadcastingMessageIndex > -1)
            {
                if (ProgramConstants.LOG_LEVEL > 1)
                    Logger.Log("QM Replace: " + qm.Command + " " + broadcastingMessageIndex);
                MessageQueue[broadcastingMessageIndex] = qm;
            }
            else
            {
                int placeInQueue = MessageQueue.FindIndex(m => m.Priority < qm.Priority);
                if (ProgramConstants.LOG_LEVEL > 1)
                    Logger.Log("QM: " + qm.Command + " " + placeInQueue);
                if (placeInQueue == -1)
                    MessageQueue.Add(qm);
                else
                    MessageQueue.Insert(placeInQueue, qm);
            }
        }

        #endregion
    }
}