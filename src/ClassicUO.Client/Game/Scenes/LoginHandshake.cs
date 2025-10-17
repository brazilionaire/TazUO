using System;
using System.Net.Sockets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.IO;
using ClassicUO.Network;
using ClassicUO.Network.Encryption;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.Scenes
{
    public class LoginHandshake : IDisposable
    {
        private ushort _retries;
        private int _reconnectTryCounter = 1;
        private long _reconnectTime;
        private bool _isDisposed;

        public LoginHandshake()
        {
        }

        public LoginSteps CurrentLoginStep { get; set; } = LoginSteps.Main;
        public ServerListEntry[] Servers { get; private set; }
        public CityInfo[] Cities { get; set; }
        public string[] Characters { get; private set; }
        public string PopupMessage { get; set; }
        public byte ServerIndex { get; private set; }
        public string Account { get; private set; }
        public string Password { get; private set; }
        public (int min, int max) LoginDelay { get; private set; }
        public bool Reconnect { get; set; }

        public event EventHandler<LoginSteps> LoginStepChanged;
        public event EventHandler<SocketError> ConnectionFailed;
        public event EventHandler<string> ErrorOccurred;

        public void Connect(string account, string password)
        {
            if (CurrentLoginStep == LoginSteps.Connecting)
            {
                return;
            }

            Account = account;
            Password = password;

            // Save credentials to config file
            if (Settings.GlobalSettings.SaveAccount)
            {
                Settings.GlobalSettings.Username = Account;
                Settings.GlobalSettings.Password = Crypter.Encrypt(Password);
                try
                {
                    Settings.GlobalSettings.Save();
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to save settings: {ex}");
                }
            }

            Log.Trace($"[HandShake] Start login to: {Settings.GlobalSettings.IP},{Settings.GlobalSettings.Port}");

            if (!Reconnect)
            {
                SetLoginStep(LoginSteps.Connecting);
            }

            AsyncNetClient.Socket.Connected -= OnNetClientConnected;
            AsyncNetClient.Socket.Disconnected -= OnNetClientDisconnected;
            AsyncNetClient.Socket?.Disconnect();
            AsyncNetClient.Socket = new AsyncNetClient();
            AsyncNetClient.Socket.Connected += OnNetClientConnected;
            AsyncNetClient.Socket.Disconnected += OnNetClientDisconnected;
            var status = AsyncNetClient.Socket.Connect(Settings.GlobalSettings.IP, Settings.GlobalSettings.Port);
        }

        public void Disconnect()
        {
            Log.Trace("[HandShake] Disconnecting...");
            AsyncNetClient.Socket.Connected -= OnNetClientConnected;
            AsyncNetClient.Socket.Disconnected -= OnNetClientDisconnected;
            AsyncNetClient.Socket?.Disconnect();
        }

        public void SelectServer(byte index, string serverName)
        {
            Log.Trace($"[HandShake] Selecting server {serverName}.");
            if (CurrentLoginStep == LoginSteps.ServerSelection)
            {
                for (byte i = 0; i < Servers.Length; i++)
                {
                    if (Servers[i].Index == index)
                    {
                        ServerIndex = i;
                        break;
                    }
                }

                Settings.GlobalSettings.LastServerNum = (ushort)(1 + ServerIndex);
                Settings.GlobalSettings.LastServerName = Servers[ServerIndex].Name;
                Settings.GlobalSettings.Save();

                SetLoginStep(LoginSteps.LoginInToServer);

                AsyncNetClient.Socket.Send_SelectServer(index);
            }
        }

        public void HandleReconnect()
        {
            if (Reconnect && (CurrentLoginStep == LoginSteps.PopUpMessage || CurrentLoginStep == LoginSteps.Main)
                && !AsyncNetClient.Socket.IsConnected)
            {
                if (_reconnectTime < Time.Ticks)
                {
                    Log.Trace($"[HandShake] Reconnecting...");
                    if (!string.IsNullOrEmpty(Account))
                    {
                        Connect(Account, Crypter.Decrypt(Settings.GlobalSettings.Password));
                    }
                    else if (!string.IsNullOrEmpty(Settings.GlobalSettings.Username))
                    {
                        Connect(Settings.GlobalSettings.Username, Crypter.Decrypt(Settings.GlobalSettings.Password));
                    }

                    int timeT = Settings.GlobalSettings.ReconnectTime * 1000;

                    if (timeT < 1000)
                    {
                        timeT = 1000;
                    }

                    _reconnectTime = (long)Time.Ticks + timeT;
                    _reconnectTryCounter++;
                }
            }
        }

        public void ServerListReceived(ref StackDataReader p)
        {
            Log.Trace($"[HandShake] Got server list.");
            byte flags = p.ReadUInt8();
            ushort count = p.ReadUInt16BE();
            DisposeAllServerEntries();
            Servers = new ServerListEntry[count];

            for (ushort i = 0; i < count; i++)
            {
                Servers[i] = ServerListEntry.Create(ref p);
            }

            SetLoginStep(LoginSteps.ServerSelection);
        }

        public void ReceiveCharacterList(ref StackDataReader p, uint clientFeatureFlags)
        {
            Log.Trace($"[HandShake] Got character list.");
            ParseCharacterList(ref p);
            ParseCities(ref p);

            SetLoginStep(LoginSteps.CharacterSelection);
        }

        public void UpdateCharacterList(ref StackDataReader p)
        {
            Log.Trace($"[HandShake] Updated character list.");
            ParseCharacterList(ref p);

            if (CurrentLoginStep != LoginSteps.PopUpMessage)
            {
                PopupMessage = null;
            }

            SetLoginStep(LoginSteps.CharacterSelection);
        }

        public void HandleErrorCode(ref StackDataReader p)
        {
            byte code = p.ReadUInt8();
            PopupMessage = ServerErrorMessages.GetError(p[0], code, LoginDelay);
            SetLoginStep(LoginSteps.PopUpMessage);
            LoginDelay = default;
        }

        public void HandleLoginDelayPacket(ref StackDataReader p)
        {
            var delay = p.ReadUInt8();
            LoginDelay = ((delay - 1) * 10, delay * 10);
        }

        public void HandleRelayServerPacket(ref StackDataReader p)
        {
            Log.Trace($"[HandShake] Got server relay packet.");
            long ip = p.ReadUInt32LE(); // use LittleEndian here
            ushort port = p.ReadUInt16BE();
            uint seed = p.ReadUInt32BE();

            if (Settings.GlobalSettings.IgnoreRelayIp || ip == 0)
            {
                Log.Trace("Ignoring relay server packet IP address");
                ip = long.Parse(Settings.GlobalSettings.IP);
                port = Settings.GlobalSettings.Port;
            }

            AfterRelayConnect(ip, port, seed);
        }

        public int GetServerIndexByName(string name)
        {
            if (!string.IsNullOrWhiteSpace(name) && Servers != null)
            {
                for (int i = 0; i < Servers.Length; i++)
                {
                    if (Servers[i].Name.Equals(name, StringComparison.InvariantCultureIgnoreCase))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        public int GetServerIndexFromSettings()
        {
            string name = Settings.GlobalSettings.LastServerName;
            int index = GetServerIndexByName(name);

            if (index == -1)
            {
                index = Settings.GlobalSettings.LastServerNum;
            }

            if (Servers == null || index < 0 || index >= Servers.Length)
            {
                index = 0;
            }

            return index;
        }

        public CityInfo GetCity(int index)
        {
            if (Cities != null && index < Cities.Length)
            {
                return Cities[index];
            }

            return null;
        }

        internal void SetLoginStep(LoginSteps step)
        {
            Log.Trace($"[HandShake] Set login step to {step}.");
            CurrentLoginStep = step;
            LoginStepChanged?.Invoke(this, step);
        }

        private void OnNetClientConnected(object sender, EventArgs e)
        {
            Log.Info("[HandShake] Connected!");
            SetLoginStep(LoginSteps.VerifyingAccount);

            uint address = AsyncNetClient.Socket.LocalIP;

            AsyncNetClient.Encryption?.Initialize(true, address);

            if (Client.Game.UO.Version >= ClientVersion.CV_6040)
            {
                uint clientVersion = (uint)Client.Game.UO.Version;

                byte major = (byte)(clientVersion >> 24);
                byte minor = (byte)(clientVersion >> 16);
                byte build = (byte)(clientVersion >> 8);
                byte extra = (byte)clientVersion;

                AsyncNetClient.Socket.Send_Seed(address, major, minor, build, extra);
            }
            else
            {
                AsyncNetClient.Socket.Send_Seed_Old(address);
            }

            AsyncNetClient.Socket.Send_FirstLogin(Account, Password);
        }

        private void OnNetClientDisconnected(object sender, SocketError e)
        {
            Log.Warn("[HandShake] Disconnected");

            if (CurrentLoginStep == LoginSteps.CharacterCreation)
            {
                return;
            }

            if (e == SocketError.Success)
            {
                return;
            }

            Characters = null;
            DisposeAllServerEntries();

            if (Settings.GlobalSettings.Reconnect)
            {
                Reconnect = true;

                PopupMessage = string.Format(
                    ResGeneral.ReconnectPleaseWait01,
                    _reconnectTryCounter,
                    StringHelper.AddSpaceBeforeCapital(e.ToString())
                );
            }
            else
            {
                PopupMessage = string.Format(
                    ResGeneral.ConnectionLost0,
                    StringHelper.AddSpaceBeforeCapital(e.ToString())
                );
            }

            SetLoginStep(LoginSteps.PopUpMessage);
            ConnectionFailed?.Invoke(this, e);
        }

        private void AfterRelayConnect(long ip, ushort port, uint seed)
        {
            AsyncNetClient.Socket.Connected -= OnNetClientConnected;
            AsyncNetClient.Socket.Disconnected -= OnNetClientDisconnected;
            AsyncNetClient.Socket.Disconnect().Wait();
            AsyncNetClient.Socket = new AsyncNetClient();

            _retries++;
            Log.Trace($"[HandShake] Reconnecting to relay server...");
            AsyncNetClient.Socket.Connect(new System.Net.IPAddress(ip).ToString(), port).Wait(3000);

            if (AsyncNetClient.Socket.IsConnected)
            {
                EncryptionHelper.Instance?.Initialize(false, seed);
                AsyncNetClient.Socket.EnableCompression();
                unsafe
                {
                    Span<byte> b = stackalloc byte[4]
                    {
                        (byte)(seed >> 24),
                        (byte)(seed >> 16),
                        (byte)(seed >> 8),
                        (byte)seed
                    };
                    AsyncNetClient.Socket.Send(b, true, true);
                }

                AsyncNetClient.Socket.Send_SecondLogin(Account, Password, seed);
                Log.Trace($"[HandShake] Sent second login.");
            }
            else
            {
                Log.Trace($"[HandShake] Failed to connect, trying again.");
                if (_retries > 5)
                {
                    _retries = 0;
                    PopupMessage = "Failed to connect to game server after multiple attempts.";
                    SetLoginStep(LoginSteps.PopUpMessage);
                    ErrorOccurred?.Invoke(this, PopupMessage);
                    return;
                }

                AfterRelayConnect(ip, port, seed);
            }
        }

        private void ParseCharacterList(ref StackDataReader p)
        {
            int count = p.ReadUInt8();
            Characters = new string[count];

            for (ushort i = 0; i < count; i++)
            {
                Characters[i] = p.ReadASCII(30).TrimEnd('\0');
                p.Skip(30);
            }
        }

        private void ParseCities(ref StackDataReader p)
        {
            byte count = p.ReadUInt8();
            Cities = new CityInfo[count];

            bool isNew = Client.Game.UO.Version >= ClientVersion.CV_70130;

            Point[] oldtowns =
            {
                new(105, 130), new(245, 90),
                new(165, 200), new(395, 160),
                new(200, 305), new(335, 250),
                new(160, 395), new(100, 250),
                new(270, 130), new(0xFFFF, 0xFFFF)
            };

            for (int i = 0; i < count; i++)
            {
                CityInfo cityInfo;

                if (isNew)
                {
                    byte cityIndex = p.ReadUInt8();
                    string cityName = p.ReadASCII(32);
                    string cityBuilding = p.ReadASCII(32);
                    ushort cityX = (ushort)p.ReadUInt32BE();
                    ushort cityY = (ushort)p.ReadUInt32BE();
                    sbyte cityZ = (sbyte)p.ReadUInt32BE();
                    uint cityMapIndex = p.ReadUInt32BE();
                    uint cityDescription = p.ReadUInt32BE();
                    p.Skip(4);

                    cityInfo = new CityInfo
                    (
                        cityIndex,
                        cityName,
                        cityBuilding,
                        Client.Game.UO.FileManager.Clilocs.GetString((int)cityDescription),
                        cityX,
                        cityY,
                        cityZ,
                        cityMapIndex,
                        isNew
                    );
                }
                else
                {
                    byte cityIndex = p.ReadUInt8();
                    string cityName = p.ReadASCII(31);
                    string cityBuilding = p.ReadASCII(31);

                    cityInfo = new CityInfo
                    (
                        cityIndex,
                        cityName,
                        cityBuilding,
                        string.Empty,
                        (ushort)oldtowns[i % oldtowns.Length].X,
                        (ushort)oldtowns[i % oldtowns.Length].Y,
                        0,
                        0,
                        isNew
                    );
                }

                Cities[i] = cityInfo;
            }
        }

        private void DisposeAllServerEntries()
        {
            if (Servers != null)
            {
                for (int i = 0; i < Servers.Length; i++)
                {
                    if (Servers[i] != null)
                    {
                        Servers[i].Dispose();
                        Servers[i] = null;
                    }
                }

                Servers = null;
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            AsyncNetClient.Socket.Disconnected -= OnNetClientDisconnected;
            AsyncNetClient.Socket.Connected -= OnNetClientConnected;

            DisposeAllServerEntries();
            Characters = null;
            Cities = null;
        }
    }
}
