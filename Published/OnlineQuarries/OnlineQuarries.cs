using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("Online Quarries", "mvrb/Arainrr", "1.2.8", ResourceId = 2216)]
    [Description("Automatically disable players' quarries when offline")]
    public class OnlineQuarries : RustPlugin
    {
        #region Fields

        [PluginReference]
        private Plugin Friends, Clans;

        private readonly Dictionary<ulong, Timer> _stopEngineTimer = new Dictionary<ulong, Timer>();
        private readonly HashSet<MiningQuarry> _miningQuarries = new HashSet<MiningQuarry>();

        #endregion Fields

        #region Oxide Hooks

        private void Init()
        {
            Unsubscribe(nameof(OnEntityDistanceCheck));
        }

        private void OnServerInitialized()
        {
            if (configData.preventOther)
            {
                Subscribe(nameof(OnEntityDistanceCheck));
            }
            foreach (var miningQuarry in BaseNetworkable.serverEntities.OfType<MiningQuarry>())
            {
                OnEntitySpawned(miningQuarry);
            }
            CheckQuarries();
        }

        private void OnEntitySpawned(MiningQuarry miningQuarry)
        {
            if (miningQuarry == null || miningQuarry.OwnerID == 0)
            {
                return;
            }
            _miningQuarries.Add(miningQuarry);
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }
            Timer value;
            if (_stopEngineTimer.TryGetValue(player.userID, out value))
            {
                value?.Destroy();
                _stopEngineTimer.Remove(player.userID);
            }

            if (configData.autoStart)
            {
                CheckQuarries(player, true);
            }
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }
            var playerId = player.userID;
            Timer value;
            if (_stopEngineTimer.TryGetValue(playerId, out value))
            {
                value?.Destroy();
                _stopEngineTimer.Remove(playerId);
            }
            _stopEngineTimer.Add(playerId, timer.Once(configData.offlineTime, () =>
            {
                CheckQuarries();
                _stopEngineTimer.Remove(playerId);
            }));
        }

        private object OnEntityDistanceCheck(EngineSwitch engineSwitch, BasePlayer player, uint id, string debugName, float maximumDistance)
        {
            if (player == null || engineSwitch == null)
            {
                return null;
            }
            if (id == 1739656243u && debugName == "StopEngine" || id == 1249530220u && debugName == "StartEngine")
            {
                var parentEntity = engineSwitch.GetParentEntity();
                if (parentEntity == null || !parentEntity.OwnerID.IsSteamId())
                {
                    return null;
                }
                if (AreFriends(parentEntity.OwnerID, player.userID))
                {
                    return false;
                }
            }
            return null;
        }

        #endregion Oxide Hooks

        #region Methods

        private void CheckQuarries(BasePlayer player = null, bool isOn = false)
        {
            foreach (var miningQuarry in _miningQuarries)
            {
                if (miningQuarry == null)
                {
                    continue;
                }
                if (player != null)
                {
                    if (AreFriends(miningQuarry.OwnerID, player.userID))
                    {
                        miningQuarry.SetOn(isOn);
                    }
                    continue;
                }

                if (!AnyOnlineFriends(miningQuarry.OwnerID))
                {
                    miningQuarry.SetOn(isOn);
                }
            }
        }

        private bool AnyOnlineFriends(ulong playerId)
        {
            foreach (var friend in BasePlayer.activePlayerList)
            {
                if (AreFriends(playerId, friend.userID))
                {
                    return true;
                }
            }

            return false;
        }

        #region AreFriends

        private bool AreFriends(ulong playerID, ulong friendID)
        {
            if (playerID == friendID)
            {
                return true;
            }
            if (configData.useTeam && SameTeam(friendID, playerID))
            {
                return true;
            }
            if (configData.useFriends && HasFriend(friendID, playerID))
            {
                return true;
            }
            if (configData.useClans && SameClan(friendID, playerID))
            {
                return true;
            }
            return false;
        }

        private bool SameTeam(ulong playerID, ulong friendID)
        {
            if (!RelationshipManager.TeamsEnabled())
            {
                return false;
            }
            var playerTeam = RelationshipManager.ServerInstance.FindPlayersTeam(playerID);
            if (playerTeam == null)
            {
                return false;
            }
            var friendTeam = RelationshipManager.ServerInstance.FindPlayersTeam(friendID);
            if (friendTeam == null)
            {
                return false;
            }
            return playerTeam == friendTeam;
        }

        private bool HasFriend(ulong playerID, ulong friendID)
        {
            if (Friends == null)
            {
                return false;
            }
            return (bool)Friends.Call("HasFriend", playerID, friendID);
        }

        private bool SameClan(ulong playerID, ulong friendID)
        {
            if (Clans == null)
            {
                return false;
            }
            //Clans
            var isMember = Clans.Call("IsClanMember", playerID.ToString(), friendID.ToString());
            if (isMember != null)
            {
                return (bool)isMember;
            }
            //Rust:IO Clans
            var playerClan = Clans.Call("GetClanOf", playerID);
            if (playerClan == null)
            {
                return false;
            }
            var friendClan = Clans.Call("GetClanOf", friendID);
            if (friendClan == null)
            {
                return false;
            }
            return (string)playerClan == (string)friendClan;
        }

        #endregion AreFriends

        #endregion Methods

        #region ConfigurationFile

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Use team")]
            public readonly bool useTeam = false;

            [JsonProperty(PropertyName = "Use clans")]
            public readonly bool useClans = false;

            [JsonProperty(PropertyName = "Use friends")]
            public readonly bool useFriends = false;

            [JsonProperty(PropertyName = "Prevent other players from turning the quarry on or off")]
            public readonly bool preventOther = false;

            [JsonProperty(PropertyName = "Automatically disable the delay of quarry (seconds)")]
            public readonly float offlineTime = 120f;

            [JsonProperty(PropertyName = "Quarry automatically starts after players are online")]
            public readonly bool autoStart = true;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                configData = Config.ReadObject<ConfigData>();
                if (configData == null)
                {
                    LoadDefaultConfig();
                }
            }
            catch (Exception ex)
            {
                PrintError($"The configuration file is corrupted. \n{ex}");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Creating a new configuration file");
            configData = new ConfigData();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(configData);
        }

        #endregion ConfigurationFile
    }
}