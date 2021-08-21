using System.Collections.Generic;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("Remover Tool API Example", "Arainrr", "1.0.0")]
    [Description("Example of api usage for the Remover Tool plugin")]
    public class RemoverToolAPIExample : RustPlugin
    {
        private class RemovableEntityInfo
        {
            /// <summary>
            /// Id of the entity image.
            /// </summary>
            public string ImageId { get; set; }

            /// <summary>
            /// Display name of the entity.
            /// </summary>
            public string DisplayName { get; set; }

            /// <summary>
            /// Remove the price of the entity. ItemName to ItemInfo
            /// </summary>
            public Dictionary<string, ItemInfo> Price { get; set; }

            /// <summary>
            /// Remove the refund of the entity. ItemName to ItemInfo
            /// </summary>
            public Dictionary<string, ItemInfo> Refund { get; set; }

            public struct ItemInfo
            {
                /// <summary>
                /// Amount of the item.
                /// </summary>
                public int Amount { get; set; }

                /// <summary>
                /// Id of the item image.
                /// </summary>
                public string ImageId { get; set; }

                /// <summary>
                /// Display name of the item.
                /// </summary>
                public string DisplayName { get; set; }
            }
        }

        private const string RefundItemName = "Drone Item";// Make sure the custom ItemName is unique.
        private const string PriceItemName = "Custom Currency";// Make sure the custom ItemName is unique.

        private readonly RemovableEntityInfo _droneEntityInfo = new RemovableEntityInfo
        {
            DisplayName = "Drone",
            Price = new Dictionary<string, RemovableEntityInfo.ItemInfo>
            {
                // All items are built-in.
                ["wood"] = new RemovableEntityInfo.ItemInfo
                {
                    Amount = 1000,
                    DisplayName = "Wood..."
                },
                // economics and serverrewards are built-in not custom.
                ["economics"] = new RemovableEntityInfo.ItemInfo
                {
                    Amount = 100,
                },
                // Custom ItemName
                [PriceItemName] = new RemovableEntityInfo.ItemInfo
                {
                    Amount = 100,
                    DisplayName = "Custom Gold"
                }
            },
            Refund = new Dictionary<string, RemovableEntityInfo.ItemInfo>
            {
                [RefundItemName] = new RemovableEntityInfo.ItemInfo
                {
                    Amount = 1,
                    DisplayName = "Refund Drone",
                }
            }
        };

        #region RemoverTool Hooks

        /// <summary>
        /// Return information about the removable entity.
        /// </summary>
        /// <param name="entity">Entity</param>
        /// <param name="player">Player</param>
        /// <returns>Serialized information</returns>
        private string OnRemovableEntityInfo(BaseEntity entity, BasePlayer player)
        {
            PrintWarning($"OnRemovableEntityInfo: {entity.ShortPrefabName} | {player.userID}");
            if (entity is Drone)
            {
                return JsonConvert.SerializeObject(_droneEntityInfo);
            }

            return null;
        }
        /// <summary>
        /// Used to check if the player can pay.
        /// Called only when the price is not empty.
        /// </summary>
        /// <param name="entity">Entity</param>
        /// <param name="player">Player</param>
        /// <param name="itemName">Item name</param>
        /// <param name="itemAmount">Item amount</param>
        /// <param name="check">If true, check if the player can pay. If false, consume the item</param>
        /// <returns>Returns whether payment can be made or whether payment was successful</returns>
        private bool OnRemovableEntityCheckOrPay(BaseEntity entity, BasePlayer player, string itemName, int itemAmount, bool check)
        {
            PrintWarning($"OnRemovableEntityCheckOrPay: {player.userID} | {entity.ShortPrefabName} | {itemName} | {itemAmount} | {check}");
            if (itemName == PriceItemName)
            {
                return true;
            }
            return false;
        }
        /// <summary>
        /// Called when giving refund items.
        /// Called only for custom items.
        /// </summary>
        /// <param name="entity">Entity</param>
        /// <param name="player">Player</param>
        /// <param name="itemName">Item name</param>
        /// <param name="itemAmount">Item amount</param>
        /// <returns>Please return a non-null value</returns>
        private bool OnRemovableEntityGiveRefund(BaseEntity entity, BasePlayer player, string itemName, int itemAmount)
        {
            PrintWarning($"OnRemovableEntityGiveRefund: {player.userID} | {entity.ShortPrefabName} | {itemName} | {itemAmount}");
            if (itemName == RefundItemName)
            {
                var item = ItemManager.CreateByName("drone", itemAmount);
                player.GiveItem(item);
            }
            return true;
        }

        #endregion RemoverTool Hooks
    }
}