namespace Oxide.Plugins
{
    [Info("Air Loot Supply", "Arainrr", "1.0.1")]
    [Description("Allow supply to be loot when it's dropping")]
    public class AirLootSupply : RustPlugin
    {
        #region Oxide Hooks

        private void Init()
        {
            Unsubscribe(nameof(OnEntitySpawned));
        }

        private void OnServerInitialized()
        {
            Subscribe(nameof(OnEntitySpawned));
            foreach (var serverEntity in BaseNetworkable.serverEntities)
            {
                var supplyDrop = serverEntity as SupplyDrop;
                if (supplyDrop != null)
                {
                    OnEntitySpawned(supplyDrop);
                }
            }
        }

        private void OnEntitySpawned(SupplyDrop supplyDrop)
        {
            if (supplyDrop == null) return;
            supplyDrop.MakeLootable();
        }

        #endregion Oxide Hooks
    }
}