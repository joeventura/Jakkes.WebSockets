﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Threading.Tasks;

namespace Jakkes.WebSockets.Server
{
    public delegate void WebSocketClientConnectedEventHandler(WebSocketServer source, Connection conn);
    public delegate void WebSocketServerStateChangedEventHandler(WebSocketServer source, WebSocketServerState state);
    
    public class WebSocketServer
    {
        
        public WebSocketServerState State
        {
            get
            {
                return _state;
            }
            set
            {
                _state = value;
                onStateChange(_state);
                StateChanged?.Invoke(this,_state);
            }
        }

        private WebSocketServerState _state = WebSocketServerState.Closed;

        #region Events
        public event WebSocketClientConnectedEventHandler ClientConnected;
        protected virtual void onClientConnect(Connection conn) { }

        public event WebSocketServerStateChangedEventHandler StateChanged;
        protected virtual void onStateChange(WebSocketServerState state) { }
        protected virtual void onMessageReceived(Connection conn, string message) { }

        protected virtual void onBinaryReceived(Connection conn, byte[] data) { }
        #endregion

        public IEnumerable<Connection> Connections
        {
            get
            {
                lock (_connections)
                    return _connections.Values.AsEnumerable();
            }
        }

        private TcpListener _server;
        
        private Dictionary<string,Connection> _connections = new Dictionary<string, Connection>();


        #region Constructors
        public WebSocketServer(int port) : this(IPAddress.Any, port) { }
        public WebSocketServer(IPAddress ip, int port)
        {
            _server = new TcpListener(ip, port);
        }
        #endregion

        #region Public methods
        public void Start()
        {
            _server.Start();
            State = WebSocketServerState.Open;
            Task.Run(() => Listen());
        }
        public void Broadcast(string message)
        {
            foreach (var conn in Connections)
                if (conn.State == WebSocketState.Open)
                    conn.Send(message);
        }
        /// <summary>
        /// Closes the server.
        /// </summary>
        /// <param name="hardquit">Set true to abandon all connections instead of performing a clean exit.</param>
        public void Close(bool hardquit)
        {
            State = WebSocketServerState.Closing;
            foreach (var conn in Connections)
                conn.Close(hardquit);

            if (hardquit)
                Shutdown();
        }
        public Connection GetConnection(string ID)
        {
            return _connections[ID];
        }
        #endregion

        #region Private methods
        private void Shutdown()
        {
            _server.Stop();
            _connections.Clear();
            State = WebSocketServerState.Closed;
        }
        private async void Listen()
        {
            if (State != WebSocketServerState.Open)
                return;

            var conn = await _server.AcceptTcpClientAsync();
            HandleConnection(conn);
            Task.Run(() => Listen());
        }
        private void HandleConnection(TcpClient conn)
        {
            try
            {
                _handshake(conn);

                string id = Guid.NewGuid().ToString();
                while (_connections.ContainsKey(id))
                    id = Guid.NewGuid().ToString();

                Connection socket = new Connection();
                socket.Init(conn, id);
                socket.Closed += Socket_Closed;
                socket.MessageReceived += onMessageReceived;
                socket.BinaryReceived += onBinaryReceived;

                lock (_connections)
                    _connections.Add(socket.ID, socket);

                ClientConnected?.Invoke(this, socket);
                onClientConnect(socket);
            } catch (Exception) { }
        }
        private void Socket_Closed(Connection source)
        {
            lock (_connections)
                _connections.Remove(source.ID);

            if (State == WebSocketServerState.Closing && _connections.Count == 0)
                Shutdown();
        }
        private void _handshake(TcpClient conn)
        {
            var stream = conn.GetStream();
            StreamReader reader = new StreamReader(stream);
            StreamWriter writer = new StreamWriter(stream);
            Dictionary<string, string> dict = new Dictionary<string, string>();

            string line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                if (line.StartsWith("Sec-WebSocket-Key"))
                    dict.Add("Key", line.Split(':')[1].Trim());
                if (line.StartsWith("GET"))
                    dict.Add("Protocol", line.Split(' ')[1].Trim().Substring(1));
            }

            if (!dict.ContainsKey("Key"))
                throw new NotImplementedException("Failed to receive the key necessary to upgrade the connection.");
            string acceptKey = Convert.ToBase64String(
                                    SHA1.Create().ComputeHash(
                                        Encoding.UTF8.GetBytes(
                                            dict["Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
                                        )
                                    )
                                );

            string response = "HTTP/1.1 101 Switching Protocols" + Environment.NewLine;
            response += "Upgrade: websocket" + Environment.NewLine;
            response += "Connection: Upgrade" + Environment.NewLine;
            response += "Sec-WebSocket-Accept: " + acceptKey + Environment.NewLine;
            if (dict.ContainsKey("Protocol") && !string.IsNullOrEmpty(dict["Protocol"]))
                response += "Sec-WebSocket-Protocol: " + dict["Protocol"] + Environment.NewLine;
            response += Environment.NewLine;
            var bytes = Encoding.UTF8.GetBytes(response);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        #endregion
    }

    public enum WebSocketServerState
    {
        Open,
        Closing,
        Closed
    }

}
