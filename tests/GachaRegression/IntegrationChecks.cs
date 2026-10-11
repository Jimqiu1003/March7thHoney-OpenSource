using System.Reflection;
using March7thHoney.Data;
using March7thHoney.Data.Custom;
using March7thHoney.Data.Excel;
using March7thHoney.Database;
using March7thHoney.Database.Avatar;
using March7thHoney.Database.Gacha;
using March7thHoney.Database.Generated;
using March7thHoney.Database.Inventory;
using March7thHoney.Database.Player;
using March7thHoney.Enums;
using March7thHoney.Enums.Item;
using March7thHoney.GameServer.Game.Avatar;
using March7thHoney.GameServer.Game.Gacha;
using March7thHoney.GameServer.Game.Inventory;
using March7thHoney.GameServer.Game.Player;
using March7thHoney.Proto;
using March7thHoney.Util;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace GachaRegression;

internal static class IntegrationChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        // No real configuration, account or runtime files are read by this process.
        var file = Path.Combine(Path.GetTempPath(), $"m7h-gacha-test-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={file};Pooling=False";
        SQLitePCL.Batteries_V2.Init();
        typeof(DatabaseHelper).GetField("_connString", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, connectionString);
        var database = new DatabaseHelper();
        try
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                foreach (var store in EntityStores.Map.Values) store.EnsureTable(connection);
            }
            foreach (var id in new[] { 101, 102, 251, 252 })
                GameData.ItemConfigData[id] = new ItemConfigExcel { ID = id, ItemMainType = ItemMainTypeEnum.Material,
                    PileLimit = int.MaxValue };
            GameData.ItemConfigData[20001] = new ItemConfigExcel { ID = 20001, ItemMainType = ItemMainTypeEnum.Equipment };
            GameData.EquipmentConfigData[20001] = new EquipmentConfigExcel { EquipmentID = 20001, Release = true,
                Rarity = RarityEnum.CombatPowerLightconeRarity3 };
            var banner = new BannerConfig { GachaId = 1001, GachaType = GachaTypeEnum.Normal };
            GameData.BannersConfig.Banners.Clear();
            GameData.BannersConfig.Banners.Add(banner);
            var player = new PlayerInstance(new PlayerData { Uid = 1 }) { Uid = 1 };
            var inventory = new InventoryManager(player);
            var avatars = new AvatarManager(player);
            typeof(PlayerInstance).GetProperty(nameof(PlayerInstance.InventoryManager))!.SetValue(player, inventory);
            typeof(PlayerInstance).GetProperty(nameof(PlayerInstance.AvatarManager))!.SetValue(player, avatars);
            var manager = new GachaManager(player);
            inventory.Data.MaterialItems.Add(new ItemData { ItemId = 101, Count = 100 });
            string State() => JsonConvert.SerializeObject(new { Gacha = manager.Data, Inventory = inventory.Data });
            async Task Reject(int id, int count, Retcode expected)
            {
                var before = State();
                var response = (await manager.DoGacha(id, count))!;
                check(response.Retcode == (uint)expected, "request error code");
                check(State() == before, "rejected request leaves inventory and pity unchanged");
            }
            await Reject(9999, 1, Retcode.RetGachaIdNotExist);
            await Reject(1001, 0, Retcode.RetGachaNumInvalid);
            await Reject(1001, 11, Retcode.RetGachaNumInvalid);
            banner.EndTime = 1;
            await Reject(1001, 1, Retcode.RetGachaIdNotExist);
            banner.EndTime = 0;
            banner.GachaType = GachaTypeEnum.Unknown;
            await Reject(1001, 1, Retcode.RetGachaIdNotExist);
            banner.GachaType = GachaTypeEnum.Normal;
            banner.RateUpItems5 = [999999];
            await Reject(1001, 1, Retcode.RetGachaIdNotExist);
            banner.RateUpItems5.Clear();
            inventory.Data.MaterialItems[0].Count = 0;
            await Reject(1001, 1, Retcode.RetItemNotEnough);
            inventory.Data.MaterialItems[0].Count = 100;
            inventory.Data.EquipmentItems.AddRange(Enumerable.Range(0, GameConstants.INVENTORY_MAX_EQUIPMENT)
                .Select(_ => new ItemData { ItemId = 20001, Count = 1 }));
            await Reject(1001, 1, Retcode.RetEquipmentExceedLimit);
            inventory.Data.EquipmentItems.Clear();
            var single = (await manager.DoGacha(1001, 1))!;
            check(single.Retcode == 0 && single.GachaNum == 1 && single.GachaItemList.Count == 1, "single response");
            check(inventory.GetItem(101)!.Count == 99 && inventory.Data.EquipmentItems.Count == 1, "single cost and reward");
            var ten = (await manager.DoGacha(1001, 10))!;
            check(ten.Retcode == 0 && ten.GachaItemList.Count == 10 && inventory.GetItem(101)!.Count == 89,
                "ten response and cost");
            check(inventory.GetItem(251)!.Count == 220 && manager.Data.GachaHistory.Count == 11, "dust and history");
            banner.GachaType = GachaTypeEnum.Newbie;
            var newbie = (await manager.DoGacha(1001, 10))!;
            check(newbie.Retcode == 0 && inventory.GetItem(101)!.Count == 81, "newbie discount");
            manager.Data.GetPityState(GachaPityFamilyEnum.Newbie).TotalPulls = 50;
            await Reject(1001, 1, Retcode.RetGachaNewbieClose);
            banner.GachaType = GachaTypeEnum.Normal;
            inventory.GetItem(101)!.Count = 10;
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ => (await manager.DoGacha(1001, 10))!));
            check(outcomes.Count(r => r.Retcode == 0) == 1 && outcomes.Count(r => r.Retcode == (uint)Retcode.RetItemNotEnough) == 1,
                "concurrent ten draws cannot spend the same tickets");
            foreach (var id in manager.GetGoldAvatars())
                GameData.AvatarConfigData[id] = new AvatarConfigExcel { AvatarID = id, MaxRank = 6,
                    Rarity = RarityEnum.CombatPowerAvatarRarityType5 };
            GameData.BannersConfig.Banners.Add(new BannerConfig { GachaId = 2001, GachaType = GachaTypeEnum.AvatarUp });
            var selectionBefore = State();
            check(manager.SetCharacterEventNonFeaturedPool(2001, 1, [1003]) == Retcode.RetGachaDecideItemIdInvalid,
                "incomplete selection rejected");
            check(State() == selectionBefore, "invalid selection preserves state");
            check(manager.SetCharacterEventNonFeaturedPool(2001, 1, manager.GetGoldAvatars().Select(x => (uint)x)) == Retcode.RetSucc,
                "valid selection accepted");
            GameData.ItemConfigData[1101] = new ItemConfigExcel { ID = 1101, ItemMainType = ItemMainTypeEnum.AvatarCard };
            GameData.ItemConfigData[11101] = new ItemConfigExcel { ID = 11101, ItemMainType = ItemMainTypeEnum.Material };
            avatars.Data.FormalAvatars.Add(new FormalAvatarInfo { BaseAvatarId = 1101, AvatarId = 1101,
                PathInfos = { [1101] = new PathInfo(1101) { Rank = 6 } } });
            await inventory.AddItem(1101, 1, false, sync: false);
            check(inventory.GetItem(11101) == null, "rank-six duplicate cannot grant an extra rank-up item");
            var beforeSave = State();
            database.SaveDatabase();
            using var loadedConnection = new SqliteConnection(connectionString);
            loadedConnection.Open();
            var loadedGacha = (GachaData)EntityStores.Map[typeof(GachaData)].LoadAll(loadedConnection).Single();
            var loadedInventory = (InventoryData)EntityStores.Map[typeof(InventoryData)].LoadAll(loadedConnection).Single();
            check(JsonConvert.SerializeObject(new { Data = loadedGacha, Inventory = loadedInventory }) ==
                JsonConvert.SerializeObject(new { Data = manager.Data, Inventory = inventory.Data }), "SQLite reload preserves full state");
            check(State() == beforeSave, "save cannot mutate gameplay state");
            var gachaStore = EntityStores.Map[typeof(GachaData)];
            var changed = manager.Data.GetPityState(GachaPityFamilyEnum.Normal);
            changed.TotalPulls += 7;
            DatabaseHelper.MarkDirty(player.Uid);
            EntityStores.Map[typeof(GachaData)] = gachaStore with
            {
                Update = (_, _) => throw new InvalidOperationException("synthetic save failure")
            };
            try { database.SaveDatabase(); }
            finally { EntityStores.Map[typeof(GachaData)] = gachaStore; }
            var afterFailure = (GachaData)gachaStore.LoadAll(loadedConnection).Single();
            check(afterFailure.GetPityState(GachaPityFamilyEnum.Normal).TotalPulls == changed.TotalPulls - 7,
                "failed save rolls back persisted state");
            database.SaveDatabase();
            var afterRetry = (GachaData)gachaStore.LoadAll(loadedConnection).Single();
            check(afterRetry.GetPityState(GachaPityFamilyEnum.Normal).TotalPulls == changed.TotalPulls,
                "failed batch stays dirty and retries");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }
}
