// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Game.UI.Gumps.CharCreation;
using ClassicUO.Game.UI.Gumps.Login;
using ClassicUO.IO;
using ClassicUO.Network;
using ClassicUO.Network.Encryption;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.Scenes
{
    public enum LoginSteps
    {
        Main,
        Connecting,
        VerifyingAccount,
        ServerSelection,
        LoginInToServer,
        CharacterSelection,
        EnteringBritania,
        CharacterCreation,
        CharacterCreationDone,
        PopUpMessage
    }

    public sealed class LoginScene : Scene
    {
        public static LoginScene Instance { get; private set; }

        private Gump _currentGump;
        private LoginSteps _lastLoginStep;
        private uint _pingTime;
        private bool _autoLogin;
        private readonly World _world;
        private readonly LoginHandshake _handshake;

        public LoginScene(World world)
        {
            Instance?.Dispose();
            _world = world;
            Instance = this;
            _handshake = new LoginHandshake(Settings.GlobalSettings.Reconnect);
            _handshake.LoginStepChanged += OnLoginStepChanged;
        }

        public bool Reconnect
        {
            get => _handshake.Reconnect;
            set => _handshake.Reconnect = value;
        }

        public LoginSteps CurrentLoginStep
        {
            get => _handshake.CurrentLoginStep;
            set => _handshake.CurrentLoginStep = value;
        }

        public ServerListEntry[] Servers => _handshake.Servers;
        public CityInfo[] Cities
        {
            get => _handshake.Cities;
            set => _handshake.Cities = value;
        }
        public string[] Characters => _handshake.Characters;
        public string PopupMessage { get; set; }
        public byte ServerIndex => _handshake.ServerIndex;
        public static string Account { get; internal set; }
        public string Password => _handshake.Password;
        public bool CanAutologin => _autoLogin || Reconnect;
        public (int min, int max) LoginDelay => _handshake.LoginDelay;


        public override void Load()
        {
            base.Load();

            Client.Game.Window.AllowUserResizing = false;

            _autoLogin = Settings.GlobalSettings.AutoLogin;

            UIManager.Add(new LoginBackground(_world));

            if (string.IsNullOrEmpty(Settings.GlobalSettings.IP))
            {
                UIManager.Add(new InputRequest(_world, "Please enter a server IP to connect to", "Save", "Cancel", (result, input) =>
                {
                    if (result == InputRequest.Result.BUTTON1 && !string.IsNullOrEmpty(input))
                    {
                        if (Settings.GlobalSettings.Port <= 0)
                        {
                            UIManager.Add(new InputRequest(_world, "Please enter the port for this server", "Save", "Cancel", (result, input) =>
                            {
                                if (result == InputRequest.Result.BUTTON1 && !string.IsNullOrEmpty(input))
                                {
                                    if (ushort.TryParse(input, out ushort p))
                                    {
                                        Settings.GlobalSettings.Port = p;
                                    }
                                }
                                UIManager.Add(_currentGump = new LoginGump(_world, this));
                            })
                            { X = 130, Y = 150 });
                        }
                        else //Port is > 0, possibly valid
                        {
                            UIManager.Add(_currentGump = new LoginGump(_world, this));
                        }
                        Settings.GlobalSettings.IP = input;
                    }
                    else //Cancel ip entry
                    {
                        UIManager.Add(_currentGump = new LoginGump(_world, this));
                    }
                })
                { X = 130, Y = 150 });
            }
            else
            {
                UIManager.Add(_currentGump = new LoginGump(_world, this));
            }

            Client.Game.Audio.PlayMusic(Client.Game.Audio.LoginMusicIndex, false, true);

            if (CanAutologin && CurrentLoginStep != LoginSteps.Main || CUOEnviroment.SkipLoginScreen && _currentGump != null)
            {
                if (!string.IsNullOrEmpty(Settings.GlobalSettings.Username))
                {
                    // disable if it's the 2nd attempt
                    CUOEnviroment.SkipLoginScreen = false;
                    Connect(Settings.GlobalSettings.Username, Crypter.Decrypt(Settings.GlobalSettings.Password));
                }
            }

            if (Client.Game.IsWindowMaximized())
            {
                Client.Game.RestoreWindow();
            }

            Client.Game.SetWindowSize(640, 480);
        }


        public override void Unload()
        {
            if (IsDestroyed)
            {
                return;
            }

            Client.Game.Audio?.StopMusic();
            Client.Game.Audio?.StopSounds();

            UIManager.GetGump<LoginBackground>()?.Dispose();

            _currentGump?.Dispose();

            Client.Game.UO.GameCursor.IsLoading = false;
            base.Unload();
        }

        private void OnLoginStepChanged(object sender, LoginSteps newStep)
        {
            if (newStep == LoginSteps.PopUpMessage)
            {
                if(_handshake.ErrorPacket != byte.MaxValue)
                    PopupMessage = ServerErrorMessages.GetError(_handshake.ErrorPacket, _handshake.ErrorCode, LoginDelay);
                else if(!string.IsNullOrEmpty(_handshake.ErrorMessage))
                    PopupMessage = _handshake.ErrorMessage;
            }

            if (_lastLoginStep != newStep)
            {
                Client.Game.UO.GameCursor.IsLoading = false;

                // this trick avoid the flickering
                Gump g = _currentGump;
                UIManager.Add(_currentGump = GetGumpForStep());
                g?.Dispose();

                _lastLoginStep = newStep;
            }

            if (newStep == LoginSteps.LoginInToServer)
            {
                Settings.GlobalSettings.LastServerNum = _handshake.LastServerNum;
                Settings.GlobalSettings.LastServerName = _handshake.LastServerName;
                Settings.GlobalSettings.Save();
            }
        }

        public override void Update()
        {
            base.Update();

            _handshake.HandleReconnect(Settings.GlobalSettings.ReconnectTime * 1000);

            if ((CurrentLoginStep == LoginSteps.CharacterCreation || CurrentLoginStep == LoginSteps.CharacterSelection) && Time.Ticks > _pingTime)
            {
                // Note that this will not be an ICMP ping, so it's better that this *not* be affected by -no_server_ping.

                if (AsyncNetClient.Socket.IsConnected)
                {
                    AsyncNetClient.Socket.Statistics.SendPing();
                }

                _pingTime = Time.Ticks + 60000;
            }
        }

        private Gump GetGumpForStep()
        {
            foreach (Item item in _world.Items.Values)
            {
                _world.RemoveItem(item);
            }

            foreach (Mobile mobile in _world.Mobiles.Values)
            {
                _world.RemoveMobile(mobile);
            }

            _world.Mobiles.Clear();
            _world.Items.Clear();

            switch (CurrentLoginStep)
            {
                case LoginSteps.Main:
                    PopupMessage = null;

                    return new LoginGump(_world,this);

                case LoginSteps.Connecting:
                case LoginSteps.VerifyingAccount:
                case LoginSteps.LoginInToServer:
                case LoginSteps.EnteringBritania:
                case LoginSteps.PopUpMessage:
                case LoginSteps.CharacterCreationDone:
                    Client.Game.UO.GameCursor.IsLoading = CurrentLoginStep != LoginSteps.PopUpMessage;

                    return GetLoadingScreen();

                case LoginSteps.CharacterSelection: return new CharacterSelectionGump(_world);

                case LoginSteps.ServerSelection:
                    _pingTime = Time.Ticks + 60000; // reset ping timer

                    return new ServerSelectionGump(_world);

                case LoginSteps.CharacterCreation:
                    _pingTime = Time.Ticks + 60000; // reset ping timer

                    return new CharCreationGump(_world,this);
            }

            return null;
        }

        private LoadingGump GetLoadingScreen()
        {
            string labelText = "No Text";
            LoginButtons showButtons = LoginButtons.None;

            if (!string.IsNullOrEmpty(PopupMessage))
            {
                labelText = PopupMessage;
                showButtons = LoginButtons.OK;
                PopupMessage = null;
            }
            else
            {
                switch (CurrentLoginStep)
                {
                    case LoginSteps.Connecting:
                        labelText = Client.Game.UO.FileManager.Clilocs.GetString(3000002, ResGeneral.Connecting); // "Connecting..."

                        showButtons = LoginButtons.Cancel;

                        break;

                    case LoginSteps.VerifyingAccount:
                        labelText = Client.Game.UO.FileManager.Clilocs.GetString(3000003, ResGeneral.VerifyingAccount); // "Verifying Account..."

                        showButtons = LoginButtons.Cancel;

                        break;

                    case LoginSteps.LoginInToServer:
                        labelText = Client.Game.UO.FileManager.Clilocs.GetString(3000053, ResGeneral.LoggingIntoShard); // logging into shard

                        showButtons = LoginButtons.Cancel;
                        break;

                    case LoginSteps.EnteringBritania:
                        labelText = Client.Game.UO.FileManager.Clilocs.GetString(3000001, ResGeneral.EnteringBritannia); // Entering Britania...

                        break;

                    case LoginSteps.CharacterCreationDone:
                        labelText = ResGeneral.CreatingCharacter;

                        break;
                }
            }

            return new LoadingGump(_world, labelText, showButtons, OnLoadingGumpButtonClick);
        }

        private void OnLoadingGumpButtonClick(int buttonId)
        {
            LoginButtons butt = (LoginButtons)buttonId;

            if (butt == LoginButtons.OK || butt == LoginButtons.Cancel)
            {
                StepBack();
            }
        }

        public void Connect(string account, string password)
        {
            Account = account;
            _handshake.Connect(account, password, Settings.GlobalSettings.IP, Settings.GlobalSettings.Port);

            // Save credentials to config file
            if (Settings.GlobalSettings.SaveAccount)
            {
                Settings.GlobalSettings.Username = account;
                Settings.GlobalSettings.Password = Crypter.Encrypt(password);
                try
                {
                    Settings.GlobalSettings.Save();
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to save settings: {ex}");
                }
            }
        }

        public int GetServerIndexByName(string name)
        {
            return _handshake.GetServerIndexByName(name);
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

        public void SelectServer(byte index)
        {
            if (Servers != null && Servers.Length > 0)
            {
                _world.ServerName = Servers[ServerIndex].Name;
            }

            _handshake.SelectServer(index, _world.ServerName);
        }

        public void SelectCharacter(uint index)
        {
            if (CurrentLoginStep == LoginSteps.CharacterSelection)
            {
                LastCharacterManager.Save(Account, _world.ServerName, Characters[index]);

                //CurrentLoginStep = LoginSteps.EnteringBritania;
                _handshake.SetLoginStep(LoginSteps.EnteringBritania);
                AsyncNetClient.Socket.Send_SelectCharacter(index, Characters[index], AsyncNetClient.Socket.LocalIP);
            }
        }

        public void StartCharCreation()
        {
            if (CurrentLoginStep == LoginSteps.CharacterSelection)
            {
                _handshake.SetLoginStep(LoginSteps.CharacterCreation);
            }
        }

        public void CreateCharacter(PlayerMobile character, int cityIndex, byte profession)
        {
            int i = 0;

            for (; i < Characters.Length; i++)
            {
                if (string.IsNullOrEmpty(Characters[i]))
                {
                    break;
                }
            }

            LastCharacterManager.Save(Account, _world.ServerName, character.Name);

            AsyncNetClient.Socket.Send_CreateCharacter(character,
                                                  cityIndex,
                                                  AsyncNetClient.Socket.LocalIP,
                                                  ServerIndex,
                                                  (uint)i,
                                                  profession);

            _handshake.SetLoginStep(LoginSteps.CharacterCreationDone);
        }

        public void DeleteCharacter(uint index)
        {
            if (CurrentLoginStep == LoginSteps.CharacterSelection)
            {
                AsyncNetClient.Socket.Send_DeleteCharacter((byte)index, AsyncNetClient.Socket.LocalIP);
            }
        }

        public void StepBack()
        {
            PopupMessage = null;

            if (Characters != null && CurrentLoginStep != LoginSteps.CharacterCreation && CurrentLoginStep != LoginSteps.ServerSelection)
            {
                _handshake.SetLoginStep(LoginSteps.LoginInToServer);
            }

            switch (CurrentLoginStep)
            {
                case LoginSteps.Connecting:
                case LoginSteps.VerifyingAccount:
                case LoginSteps.ServerSelection:
                    _handshake.Disconnect();
                    _handshake.SetLoginStep(LoginSteps.Main);

                    break;

                case LoginSteps.LoginInToServer:
                    _handshake.Disconnect();
                    Connect(Account, Password);

                    break;

                case LoginSteps.CharacterCreation:
                    _handshake.SetLoginStep(LoginSteps.CharacterSelection);

                    break;

                case LoginSteps.PopUpMessage:
                case LoginSteps.CharacterSelection:
                    _handshake.Disconnect();
                    _handshake.SetLoginStep(LoginSteps.Main);

                    break;
            }
        }

        public CityInfo GetCity(int index)
        {
            return _handshake.GetCity(index);
        }


        public void ServerListReceived(ref StackDataReader p)
        {
            _handshake.ServerListReceived(ref p);

            if (CanAutologin && Servers != null && Servers.Length != 0)
            {
                int index = GetServerIndexFromSettings();
                SelectServer((byte)Servers[index].Index);
            }
        }

        public void UpdateCharacterList(ref StackDataReader p)
        {
            _handshake.UpdateCharacterList(ref p);

            UIManager.GetGump<CharacterSelectionGump>()?.Dispose();

            _currentGump?.Dispose();

            UIManager.Add(_currentGump = new CharacterSelectionGump(_world));
            if (!string.IsNullOrWhiteSpace(PopupMessage))
            {
                Gump g = null;
                g = new LoadingGump(_world, PopupMessage, LoginButtons.OK, (but) => g.Dispose()) { IsModal = true };
                UIManager.Add(g);
                PopupMessage = null;
            }
        }

        public void ReceiveCharacterList(ref StackDataReader p)
        {
            _handshake.ReceiveCharacterList(ref p, 0);
            _world.ClientFeatures.SetFlags((CharacterListFlags)p.ReadUInt32BE());

            uint charToSelect = 0;
            bool haveAnyCharacter = false;
            bool canLogin = CanAutologin;

            if (_autoLogin)
            {
                _autoLogin = false;
            }

            string lastCharName = LastCharacterManager.GetLastCharacter(Account, _world.ServerName);

            if (Characters != null)
            {
                for (byte i = 0; i < Characters.Length; i++)
                {
                    if (Characters[i].Length > 0)
                    {
                        haveAnyCharacter = true;

                        if (Characters[i] == lastCharName)
                        {
                            charToSelect = i;
                            break;
                        }
                    }
                }
            }

            if (canLogin && haveAnyCharacter)
            {
                SelectCharacter(charToSelect);
            }
            else if (!haveAnyCharacter)
            {
                StartCharCreation();
            }
        }

        public void HandleErrorCode(ref StackDataReader p)
        {
            _handshake.HandleErrorCode(ref p);
        }

        public void HandleLoginDelayPacket(ref StackDataReader p)
        {
            _handshake.HandleLoginDelayPacket(ref p);
        }

        public override void Dispose()
        {
            base.Dispose();
            _handshake?.Dispose();
        }
    }

    public class ServerListEntry
    {
        private IPAddress _ipAddress;
        private IPAddress _ipAddressLittleEndian;
        private Ping _pinger = new Ping();
        private bool _sending;
        private readonly bool[] _last10Results = new bool[10];
        private int _resultIndex;

        private ServerListEntry()
        {
        }

        public static ServerListEntry Create(ref StackDataReader p)
        {
            ServerListEntry entry = new ServerListEntry()
            {
                Index = p.ReadUInt16BE(),
                Name = p.ReadASCII(32, true),
                PercentFull = p.ReadUInt8(),
                Timezone = p.ReadUInt8(),
                Address = p.ReadUInt32BE()
            };

            // some server sends invalid ip.
            try
            {
                entry._ipAddress = new IPAddress
                (
                    new byte[]
                    {
                        (byte) ((entry.Address >> 24) & 0xFF),
                        (byte) ((entry.Address >> 16) & 0xFF),
                        (byte) ((entry.Address >> 8) & 0xFF),
                        (byte) (entry.Address & 0xFF)
                    }
                );

                // IP address in little-endian format, required for server ping
                entry._ipAddressLittleEndian = new IPAddress
                (
                    new byte[]
                    {
                        (byte) (entry.Address & 0xFF),
                        (byte) ((entry.Address >> 8) & 0xFF),
                        (byte) ((entry.Address >> 16) & 0xFF),
                        (byte) ((entry.Address >> 24) & 0xFF)
                    }
                );

            }
            catch (Exception e)
            {
                Log.Error(e.ToString());
            }

            entry._pinger.PingCompleted += entry.PingerOnPingCompleted;

            return entry;
        }


        public uint Address;
        public ushort Index;
        public string Name;
        public byte PercentFull;
        public byte Timezone;
        public int Ping = -1;
        public int PacketLoss;
        public IPStatus PingStatus;

        private static byte[] _buffData = new byte[32];
        private static PingOptions _pingOptions = new PingOptions(64, true);

        public void DoPing()
        {
            if (_ipAddress != null && !_sending && _pinger != null)
            {
                if (_resultIndex >= _last10Results.Length)
                {
                    _resultIndex = 0;
                }

                try
                {
                    _pinger.SendAsync
                    (
                        _ipAddressLittleEndian,
                        1000,
                        _buffData,
                        _pingOptions,
                        _resultIndex++
                    );

                    _sending = true;
                }
                catch
                {
                    _ipAddress = null;
                    Dispose();
                }
            }
        }

        private void PingerOnPingCompleted(object sender, PingCompletedEventArgs e)
        {
            int index = (int)e.UserState;

            if (e.Reply != null)
            {
                Ping = (int)e.Reply.RoundtripTime;
                PingStatus = e.Reply.Status;

                _last10Results[index] = e.Reply.Status == IPStatus.Success;
            }

            //if (index >= _last10Results.Length - 1)
            {
                PacketLoss = 0;

                for (int i = 0; i < _resultIndex; i++)
                {
                    if (!_last10Results[i])
                    {
                        ++PacketLoss;
                    }
                }

                PacketLoss = (Math.Max(1, PacketLoss) / Math.Max(1, _resultIndex)) * 100;

                //_resultIndex = 0;
            }

            _sending = false;
        }

        public void Dispose()
        {
            if (_pinger != null)
            {
                _pinger.PingCompleted -= PingerOnPingCompleted;

                if (_sending)
                {
                    try
                    {
                        _pinger.SendAsyncCancel();
                    }
                    catch { }

                }

                _pinger.Dispose();
                _pinger = null;
            }
        }
    }

    public class CityInfo
    {
        public CityInfo
        (
            int index,
            string city,
            string building,
            string description,
            ushort x,
            ushort y,
            sbyte z,
            uint map,
            bool isNew
        )
        {
            Index = index;
            City = city;
            Building = building;
            Description = description;
            X = x;
            Y = y;
            Z = z;
            Map = map;
            IsNewCity = isNew;
        }

        public readonly string Building;
        public readonly string City;
        public readonly string Description;
        public readonly int Index;
        public readonly bool IsNewCity;
        public readonly uint Map;
        public readonly ushort X, Y;
        public readonly sbyte Z;
    }
}
