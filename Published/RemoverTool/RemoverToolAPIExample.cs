﻿using System.Collections.Generic;

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
                /// SkinId of the item.
                /// Less than 0 is not specified skin.
                /// </summary>
                public long SkinId { get; set; }

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

        private const string RefundItemName = "Drone Item"; // Make sure the custom ItemName is unique.
        private const string PriceItemName = "Custom Currency"; // Make sure the custom ItemName is unique.

        private readonly Dictionary<string, object> _droneEntityInfo = new Dictionary<string, object>
        {
            ["DisplayName"] = "Drone",
            ["Price"] = new Dictionary<string, object>
            {
                // All items are built-in.
                ["wood"] = new Dictionary<string, object>
                {
                    ["Amount"] = 1000,
                    ["DisplayName"] = "Wood..."
                },
                // economics and serverrewards are built-in not custom.
                ["economics"] = new Dictionary<string, object>
                {
                    ["Amount"] = 100,
                    ["DisplayName"] = "Economics..."
                },
                // Custom ItemName.
                [PriceItemName] = new Dictionary<string, object>
                {
                    ["Amount"] = 100,
                    ["DisplayName"] = "Custom Gold"
                }
            },
            ["Refund"] = new Dictionary<string, object>
            {
                // Custom ItemName.
                [RefundItemName] = new Dictionary<string, object>
                {
                    ["Amount"] = 1,
                    ["DisplayName"] = "Refund Drone",
                },
                // Custom SkinId
                ["box.wooden.large"] = new Dictionary<string, object>
                {
                    ["Amount"] = 1,
                    ["SkinId"] = 1742653197L,
                    ["DisplayName"] = "MiniCopter",
                }
            }
        };

        #region RemoverTool Hooks

        /// <summary>
        /// Used to check if the player can pay. It is only called when there is a custom ItemName
        /// in the price
        /// </summary>
        /// <param name="entity"> Entity </param>
        /// <param name="player"> Player </param>
        /// <param name="itemName"> Item name </param>
        /// <param name="itemAmount"> Item amount </param>
        /// <param name="skinId"> Less than 0 is not specified skin </param>
        /// <param name="check"> If true, check if the player can pay. If false, consume the item </param>
        /// <returns> Returns whether payment can be made or whether payment was successful </returns>
        private bool OnRemovableEntityCheckOrPay(BaseEntity entity, BasePlayer player, string itemName, int itemAmount, long skinId, bool check)
        {
            PrintWarning($"OnRemovableEntityCheckOrPay: {player.userID} | {entity.ShortPrefabName} | {itemName} | {itemAmount} | {skinId} | {check}");
            if (itemName == PriceItemName)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Called when giving refund items. It is only called when there is a custom item name in
        /// the refund.
        /// </summary>
        /// <param name="entity"> Entity </param>
        /// <param name="player"> Player </param>
        /// <param name="itemName"> Item name </param>
        /// <param name="itemAmount"> Item amount </param>
        /// <param name="skinId"> Less than 0 is not specified skin </param>
        /// <returns> Returns whether the refund has been granted successful </returns>
        private bool OnRemovableEntityGiveRefund(BaseEntity entity, BasePlayer player, string itemName, int itemAmount, long skinId)
        {
            PrintWarning($"OnRemovableEntityGiveRefund: {player.userID} | {entity.ShortPrefabName} | {itemName} | {itemAmount} | {skinId}");
            if (itemName == RefundItemName)
            {
                var item = ItemManager.CreateByName("drone", itemAmount);
                player.GiveItem(item);
            }

            return true;
        }

        /// <summary>
        /// Return information about the removable entity.
        /// </summary>
        /// <param name="entity"> Entity </param>
        /// <param name="player"> Player </param>
        /// <returns> Serialized information </returns>
        private Dictionary<string, object> OnRemovableEntityInfo(BaseEntity entity, BasePlayer player)
        {
            PrintWarning($"OnRemovableEntityInfo: {entity.ShortPrefabName} | {player.userID}");
            if (entity is Drone)
            {
                return _droneEntityInfo;
            }

            return null;
        }

        #endregion RemoverTool Hooks
    }
}