using System;
using System.Net;
using System.Diagnostics;
using System.Text;
using System.Linq;
using System.Windows.Forms;
using System.IO;
using System.IO.Compression;
using WebSocketSharp;

namespace SM64O
{
    public class Client
    {
        private static bool DEBUG = false;
        private readonly byte[] EMPTY = new byte[0x18];

        public int PlayerID = -1;

        private Form1 _gui;
        private WebSocketConnection _connection;
        private IEmulatorAccessor _memory;
        private bool _connected;

        public Client(Form1 gui, int port, IEmulatorAccessor memory, IPAddress target, byte[] payload)
        {
            _connected = false;
            _gui = gui;
            _memory = memory;
            _connection = new WebSocketConnection("ws://" + target + ":" + port);
            _connection.OnMessage += (sender, e) =>
            {
                OnMessage(e);
            };
            _connection.OnOpen += (sender, e) =>
            {
                _connected = true;
                Console.WriteLine("connected");
                _connection.Send(payload);
                // SetMessage("connected");
                _gui.addChatMessage("[SERVER]", "Connected");
            };
            _connection.OnError += (sender, e) =>
            {
                MessageBox.Show(null, e.Message + "\n\n" + e.Exception, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            };
            _connection.OnClose += (sender, e) =>
            {
                _connected = false;
                Console.WriteLine("disconnected");
                _memory.WriteMemory(0x365FFC, new byte[1]{ 0 }, 1);
                _gui.addChatMessage("[SERVER]", "Disconnected");
            };
            _connection.Connect();
        }
        
        private void OnMessage(MessageEventArgs e)
        {
            byte[] data = e.RawData;
            if (data.Length == 0) return;

            PacketType type = (PacketType)data[0];
            byte[] payload = data.Skip(1).ToArray();

            switch (type)
            {
                case PacketType.Handshake:
                    _memory.WriteMemory(0x365FFC, new byte[1]{ 2 }, 1);
                    _memory.WriteMemory(0x367703, payload, 1);
                    PlayerID = (int)payload[0];
                    Console.WriteLine("player ID: " + PlayerID);
                    break;
                case PacketType.PlayerData:
                    ReceivePlayerData(payload);
                    break;
                case PacketType.GameMode:
                    _memory.WriteMemory(0x365FF7, payload, 1);
                    break;
                case PacketType.ChatMessage:
                    ReceiveChatMessage(payload);
                    break;
                case PacketType.RoundtripPing:
                    // We got our pong
                    int sendTime = BitConverter.ToInt32(payload, 0);
                    int currentTime = Environment.TickCount;

                    int elapsed = currentTime - sendTime;

                    _gui.setPing(elapsed / 2);
                    break;
            }
        }

        private void ReceivePlayerData(byte[] compressed)
        {
            using (MemoryStream ms = new MemoryStream(compressed))
            using (GZipStream gs = new GZipStream(ms, CompressionMode.Decompress))
            using (MemoryStream res = new MemoryStream())
            {
                gs.CopyTo(res);
                byte[] data = res.ToArray();
                byte[] playerData = new byte[0x18];
                int j = 1;
                for (int i = 0; i < data.Length; i += 0x18, j++)
                {
                    Array.Copy(data, i, playerData, 0, 0x18);
                    _memory.WriteMemory(0x367700 + 0x100 * j, playerData, 0x18);
                }
                for (; j < 24; j++) {
                    _memory.WriteMemory(0x367700 + 0x100 * j, EMPTY, 0x18);
                }
            }
        }

        private void ReceiveChatMessage(byte[] data)
        {
            if (!_gui.ChatEnabled) return;

            string message = "";
            string sender = "";

            int msgLen = data[0];
            message = Program.GetASCIIString(data, 1, msgLen);
            int nameLen = data[msgLen + 1];
            sender = Program.GetASCIIString(data, msgLen + 2, nameLen);
            _gui.addChatMessage(sender, message);
        }

        public void sendPlayerData()
        {
            byte[] payload = new byte[0x18];
            _memory.ReadMemory(0x367700, payload, 0x18);
            if (payload[0xF] != 0)
            {
                _connection.SendPacket(PacketType.PlayerData, payload);
            }

            if (DEBUG)
            {
                for (int i = 0; i < 24; i++)
                {
                    _memory.ReadMemory(0x367700 + 0x100 * i, payload, 0x18);
                    if (i == 0)
                    {
                        Console.Write("own player data: ");
                        Console.WriteLine(PrintBytes(payload));
                    }
                    else if ((int)payload[3] != 0)
                    {
                        Console.Write("player " + (int)payload[3] + ": ");
                        Console.WriteLine(PrintBytes(payload));
                    }
                }
            }
        }
        
        public void SetMessage(string msg)
        {
            byte[] strBuf = Encoding.ASCII.GetBytes(msg.Where(isPrintable).ToArray());
            byte[] buffer = new byte[strBuf.Length + 4];
            strBuf.CopyTo(buffer, 0);
            for (int i = 0; i < buffer.Length; i += 4)
            {
                byte[] buf = buffer.Skip(i).Take(4).ToArray();
                buf = buf.Reverse().ToArray();
                _memory.WriteMemory(0x367684 + i, buf, 4);
            }

            byte[] empty = new byte[4];
            _memory.WriteMemory(0x367680, empty, 4);
        }

        private static readonly char[] _printables = new[] { ' ', '+', '-', ',', };
        private static bool isPrintable(char c)
        {
            if (char.IsLetterOrDigit(c)) return true;
            if (Array.IndexOf(_printables, c) != -1) return true;
            return false;
        }

        public void sendAllChat(string username, string message)
        {
            string name = "HOST";

            if (!string.IsNullOrWhiteSpace(username))
                name = username;

            if (message.Length > Form1.MAX_CHAT_LENGTH)
                message = message.Substring(0, Form1.MAX_CHAT_LENGTH);

            if (name.Length > Form1.MAX_CHAT_LENGTH)
                name = name.Substring(0, Form1.MAX_CHAT_LENGTH);

            byte[] messageBytes = Encoding.ASCII.GetBytes(message);
            byte[] usernameBytes = Encoding.ASCII.GetBytes(name);


            byte[] payload = new byte[1 + messageBytes.Length + 1 + usernameBytes.Length];

            payload[0] = (byte)messageBytes.Length;

            Array.Copy(messageBytes, 0, payload, 1, messageBytes.Length);

            payload[messageBytes.Length + 1] = (byte)usernameBytes.Length;

            Array.Copy(usernameBytes, 0, payload, 1 + messageBytes.Length + 1, usernameBytes.Length);

            _connection.SendPacket(PacketType.ChatMessage, payload);

        }

        public void sendChatTo(string username, string message)
        {
            string name = "HOST";

            if (!string.IsNullOrWhiteSpace(username))
                name = username;

            if (message.Length > Form1.MAX_CHAT_LENGTH)
                message = message.Substring(0, Form1.MAX_CHAT_LENGTH);

            if (name.Length > Form1.MAX_CHAT_LENGTH)
                name = name.Substring(0, Form1.MAX_CHAT_LENGTH);

            byte[] messageBytes = Encoding.ASCII.GetBytes(message);
            byte[] usernameBytes = Encoding.ASCII.GetBytes(name);


            byte[] payload = new byte[1 + messageBytes.Length + 1 + usernameBytes.Length];

            payload[0] = (byte)messageBytes.Length;

            Array.Copy(messageBytes, 0, payload, 1, messageBytes.Length);

            payload[messageBytes.Length + 1] = (byte)usernameBytes.Length;

            Array.Copy(usernameBytes, 0, payload, 1 + messageBytes.Length + 1, usernameBytes.Length);

            _connection.SendPacket(PacketType.ChatMessage, payload);
        }

        public void setCharacter(int index)
        {
            _connection.SendPacket(PacketType.CharacterSwitch, new byte[] { (byte)(index) });
        }

        public void Ping()
        {
            byte[] buffer = new byte[4];
            Array.Copy(BitConverter.GetBytes(Environment.TickCount), 0, buffer, 0, 4);

            _connection.SendPacket(PacketType.RoundtripPing, buffer);
        }

        private string PrintBytes(byte[] byteArray)
        {
            var sb = new StringBuilder("new byte[] { ");
            for(var i = 0; i < byteArray.Length; i++)
            {
                var b = byteArray[i];
                sb.Append(b);
                if (i < byteArray.Length -1)
                {
                    sb.Append(", ");
                }
            }
            sb.Append(" }");
            return sb.ToString();
        }
    }
}