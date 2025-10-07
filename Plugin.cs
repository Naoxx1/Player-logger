using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using GorillaNetworking;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.Networking;

namespace Playerlogger
{
    [BepInPlugin("com.idk.playerlogger", "Player Logger", "1.0.0")]
    internal class Plugin : BaseUnityPlugin
    {
        private const string FOLDER_NAME = "Player Logger";
        private const string PLUGIN_PROPERTY = "Player Logger";
        private const float CHECK_DELAY = 9f;

        private string _webhookUrl;
        private bool _saveToFile = true;
        private bool _sendWebhook = true;

        public static Plugin Instance { get; private set; }
        private static readonly List<string> _checkedPlayers = new List<string>();
        private static string _lastRoomName = string.Empty;
        private bool _networkEventsHooked = false;

        public void Start()
        {
            InitializePlugin();
            SetupLogFiltering();
            HookNetworkEvents();
        }

        private void InitializePlugin()
        {
            try
            {
                GorillaTagger.OnPlayerSpawned(OnGameInit);
            }
            catch (Exception e)
            {
            }

            LoadConfiguration();
            EnsureDirectoryExists(FOLDER_NAME);
            Instance = this;
        }

        private void LoadConfiguration()
        {
            _webhookUrl = Config.Bind("General", "WebhookUrl", "", "Your Discord webhook URL").Value;
            _saveToFile = Config.Bind("General", "SaveToFile", true, "Save player data to files").Value;
            _sendWebhook = Config.Bind("General", "SendWebhook", true, "Send notifications to Discord").Value;
            
            Config.Save();
        }

        private void SetupLogFiltering()
        {
            Application.logMessageReceived += FilterAudioWarnings;
        }

        private void FilterAudioWarnings(string logString, string stackTrace, LogType type)
        {
            if (logString.Contains("timeSamples") && logString.Contains("audio source"))
            {
                return;
            }
        }

        private void HookNetworkEvents()
        {
            try
            {
                if (!_networkEventsHooked && NetworkSystem.Instance != null)
                {
                    NetworkSystem.Instance.OnJoinedRoomEvent += OnJoinedRoom;
                    NetworkSystem.Instance.OnReturnedToSinglePlayer += OnLeftRoom;
                    NetworkSystem.Instance.OnPlayerJoined += OnPlayerJoined;
                    NetworkSystem.Instance.OnPlayerLeft += OnPlayerLeft;
                    _networkEventsHooked = true;
                }
            }
            catch (Exception e)
            {
            }
        }

        public void OnGameInit()
        {
            HookNetworkEvents();
        }

        public void Update()
        {
            SetPlayerProperty();
            HandleRoomState();
            EnsureNetworkEventsHooked();
        }

        private void SetPlayerProperty()
        {
            try
            {
                // Let other players know we have this mod
                if (!PhotonNetwork.LocalPlayer.CustomProperties.ContainsKey(PLUGIN_PROPERTY))
                {
                    var properties = new ExitGames.Client.Photon.Hashtable();
                    properties.Add(PLUGIN_PROPERTY, true);
                    PhotonNetwork.LocalPlayer.SetCustomProperties(properties);
                }
            }
            catch (Exception e)
            {
            }
        }

        private void HandleRoomState()
        {
            if (PhotonNetwork.InRoom)
            {
                _lastRoomName = PhotonNetwork.CurrentRoom.Name;
                StartCoroutine(CheckServer());
            }
            else
            {
                _lastRoomName = string.Empty;
            }
        }

        private void EnsureNetworkEventsHooked()
        {
            if (!_networkEventsHooked && NetworkSystem.Instance != null)
            {
                HookNetworkEvents();
            }
        }

        public IEnumerator CheckServer()
        {
            yield return new WaitForSeconds(CHECK_DELAY);

            try
            {
                if (ShouldProcessPlayers())
                {
                    ProcessPlayersInRoom();
                }

                ClearPlayersIfNotInRoom();
            }
            catch (Exception e)
            {
            }
        }

        private bool ShouldProcessPlayers()
        {
            return PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom.PlayerCount > 1;
        }

        private void ProcessPlayersInRoom()
        {
            string roomFolder = Path.Combine(FOLDER_NAME, PhotonNetwork.CurrentRoom.Name);
            EnsureDirectoryExists(roomFolder);

            foreach (Player player in PhotonNetwork.PlayerListOthers)
            {
                if (player == null || _checkedPlayers.Contains(player.UserId))
                {
                    continue;
                }

                ProcessPlayer(player, roomFolder);
            }
        }

        private void ProcessPlayer(Player player, string roomFolder)
        {
            string cosmetics = GetPlayerCosmetics(player);

            if (!string.IsNullOrEmpty(cosmetics))
            {
                if (_saveToFile)
                {
                    SavePlayerData(player, roomFolder, cosmetics);
                }
                
                if (_sendWebhook)
                {
                    SendWebhookNotification(player, cosmetics);
                }
                
                _checkedPlayers.Add(player.UserId);
            }
        }

        private string GetPlayerCosmetics(Player player)
        {
            try
            {
                var gameManager = GorillaGameManager.instance;
                var cosmeticsController = CosmeticsController.instance;
                
                if (gameManager?.FindPlayerVRRig(player) == null || cosmeticsController == null)
                {
                    return string.Empty;
                }

                var playerRig = gameManager.FindPlayerVRRig(player);
                var allowedCosmetics = playerRig.concatStringOfCosmeticsAllowed;

                return string.Join("\n", cosmeticsController.allCosmetics
                    .Where(cosmetic => allowedCosmetics.Contains(cosmetic.itemName))
                    .Select(cosmetic => cosmetic.itemName));
            }
            catch (Exception ex)
            {
                return string.Empty;
            }
        }

        private void SavePlayerData(Player player, string roomFolder, string cosmetics)
        {
            try
            {
                string filename = Path.Combine(roomFolder, $"{player.UserId} - {player.NickName}.txt");
                string fileContent = FormatPlayerData(player, cosmetics);
                File.WriteAllText(filename, fileContent);
            }
            catch (Exception ex)
            {
            }
        }

        private void SendWebhookNotification(Player player, string cosmetics)
        {
            try
            {
                string content = FormatPlayerData(player, cosmetics);
                StartCoroutine(PostToWebhook(content));
            }
            catch (Exception ex)
            {
            }
        }

        private string FormatPlayerData(Player player, string cosmetics)
        {
            return $"Name:\n{player.NickName}\n\nUser ID:\n{player.UserId}\n\nTime Found:\n{DateTime.Now:F}\n\n\nCustom Properties:\n{GetPlayerProperties(player)}\n\nCosmetics:\n{cosmetics}";
        }

        private string GetPlayerProperties(Player player)
        {
            if (player.CustomProperties == null || player.CustomProperties.Count <= 0)
            {
                return "No Custom Properties.";
            }

            try
            {
                return string.Join("\n", player.CustomProperties
                    .Cast<System.Collections.DictionaryEntry>()
                    .Select(kvp => $"{kvp.Key}: {kvp.Value}"));
            }
            catch
            {
                return string.Join("\n", player.CustomProperties.Keys
                    .Cast<object>()
                    .Select(key => $"{key}: {player.CustomProperties[key]}"));
            }
        }

        private void ClearPlayersIfNotInRoom()
        {
            if (!PhotonNetwork.InRoom && _checkedPlayers.Count > 0)
            {
                _checkedPlayers.Clear();
            }
        }

        private void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        private void OnJoinedRoom()
        {
        }

        private void OnLeftRoom()
        {
            _checkedPlayers.Clear();
        }

        private void OnPlayerJoined(NetPlayer netPlayer)
        {
        }

        private void OnPlayerLeft(NetPlayer netPlayer)
        {
        }

        private IEnumerator PostToWebhook(string content)
        {
            if (string.IsNullOrEmpty(_webhookUrl))
            {
                yield break;
            }

            string json = FormatWebhookJson(content);

            using (var request = new UnityWebRequest(_webhookUrl, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                yield return request.SendWebRequest();
            }
        }

        private string FormatWebhookJson(string content)
        {
            string escapedContent = content
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
            
            return $"{{\"content\":\"{escapedContent}\"}}";
        }

        private IEnumerator SendSimpleWebhook(string message)
        {
            yield return PostToWebhook(message);
        }
    }
}