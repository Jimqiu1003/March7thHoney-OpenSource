using March7thHoney.Data;
using March7thHoney.Data.Custom;
using March7thHoney.Util;
using System.Runtime.CompilerServices;
using March7thHoney.Database;
using March7thHoney.Database.Gacha;
using March7thHoney.Database.Inventory;
using March7thHoney.Enums;
using March7thHoney.Enums.Item;
using March7thHoney.GameServer.Game.Player;
using March7thHoney.GameServer.Server.Packet.Send.PlayerSync;
using March7thHoney.Proto;
using GachaInfo = March7thHoney.Database.Gacha.GachaInfo;

namespace March7thHoney.GameServer.Game.Gacha;

public class GachaManager : BasePlayerManager<GachaData>
{
    // Reconnected managers share the cached GachaData; the gate must share that lifetime too.
    private sealed class ExecutionState
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        // shortcut: quarantine lasts for this process; reconcile partial state manually before a restart.
        internal bool SettlementFailed;
    }
    private static readonly ConditionalWeakTable<GachaData, ExecutionState> ExecutionStates = new();
    private static readonly Logger Log = new("Gacha");

    private const int NewbieGachaLimit = 50;
    private const int NewbieTenPullCost = 8;
    private const int CelestialInvitationPoolSize = 7;

    private static readonly HashSet<int> KnownBattlePassLightCones =
    [
        21028, 21029, 21030, 21031, 21032, 21033, 21034,
        21052, 21053, 21055, 21056, 21057, 21058, 21060, 21061, 21062, 21065
    ];

    public GachaManager(PlayerInstance player) : base(player)
    {
        Data.EnsurePityStateMigrated();

        if (Data.GachaHistory.Count >= 50)
            Data.GachaHistory.RemoveRange(0, Data.GachaHistory.Count - 50);

        foreach (var order in GameData.DecideAvatarOrderData.Values.ToList().OrderBy(x => -x.Order))
        {
            if (Data.GachaDecideOrder.Contains(order.ItemID)) continue;
            Data.GachaDecideOrder.Add(order.ItemID);
        }

        EnsureCharacterEventNonFeaturedPool();
    }


    public List<int> GetPurpleAvatars()
    {
        var purpleAvatars = new List<int>();
        foreach (var avatar in GameData.AvatarConfigData.Values)
            if (avatar.Rarity == RarityEnum.CombatPowerAvatarRarityType4 &&
                !(GameData.MultiplePathAvatarConfigData.ContainsKey(avatar.AvatarID) &&
                  GameData.MultiplePathAvatarConfigData[avatar.AvatarID].BaseAvatarID != avatar.AvatarID) &&
                avatar.MaxRank > 0)
                purpleAvatars.Add(avatar.AvatarID);
        return purpleAvatars;
    }

    public List<int> GetGoldAvatars()
    {
        return [1003, 1004, 1101, 1107, 1104, 1209, 1211];
    }

    public List<int> GetAllGoldAvatars()
    {
        var avatars = new List<int>();
        foreach (var avatar in GameData.AvatarConfigData.Values)
            if (avatar.Rarity == RarityEnum.CombatPowerAvatarRarityType5)
                avatars.Add(avatar.AvatarID);
        return avatars;
    }

    public List<int> GetBlueWeapons()
    {
        var purpleWeapons = new List<int>();
        foreach (var weapon in GameData.EquipmentConfigData.Values)
            if (weapon.Release && weapon.Rarity == RarityEnum.CombatPowerLightconeRarity3)
                purpleWeapons.Add(weapon.EquipmentID);
        return purpleWeapons;
    }

    public List<int> GetPurpleWeapons()
    {
        var purpleWeapons = new List<int>();
        foreach (var weapon in GameData.EquipmentConfigData.Values)
            if (weapon.Release && weapon.Rarity == RarityEnum.CombatPowerLightconeRarity4 &&
                IsGachaEligibleFourStarLightCone(weapon.EquipmentID))
                purpleWeapons.Add(weapon.EquipmentID);
        return purpleWeapons;
    }

    public List<int> GetGoldWeapons()
    {
        return [23000, 23002, 23003, 23004, 23005, 23012, 23013];
    }

    public List<int> GetAllGoldWeapons()
    {
        var weapons = new List<int>();
        foreach (var weapon in GameData.EquipmentConfigData.Values)
            if (weapon.Release && weapon.Rarity == RarityEnum.CombatPowerLightconeRarity5)
                weapons.Add(weapon.EquipmentID);
        return weapons;
    }

    public int GetRarity(int itemId)
    {
        if (GameData.AvatarConfigData.TryGetValue(itemId, out var avatar))
        {
            if (avatar.Rarity == RarityEnum.CombatPowerAvatarRarityType5) return 5;
            if (avatar.Rarity == RarityEnum.CombatPowerAvatarRarityType4 && avatar.MaxRank > 0 &&
                (!GameData.MultiplePathAvatarConfigData.TryGetValue(itemId, out var path) || path.BaseAvatarID == itemId))
                return 4;
        }
        if (GameData.EquipmentConfigData.TryGetValue(itemId, out var weapon) && weapon.Release)
        {
            if (weapon.Rarity == RarityEnum.CombatPowerLightconeRarity5) return 5;
            if (weapon.Rarity == RarityEnum.CombatPowerLightconeRarity4 && IsGachaEligibleFourStarLightCone(itemId)) return 4;
            if (weapon.Rarity == RarityEnum.CombatPowerLightconeRarity3) return 3;
        }
        return 0;
    }

    public int GetType(int itemId)
    {
        if (GetRarity(itemId) == 0) return 0;
        return GameData.AvatarConfigData.ContainsKey(itemId) ? 1 : 2;
    }

    public async ValueTask<DoGachaScRsp?> DoGacha(int bannerId, int times)
    {
        var execution = ExecutionStates.GetValue(Data, _ => new ExecutionState());
        await execution.Gate.WaitAsync();
        try
        {
            return execution.SettlementFailed
                ? BuildGachaError(bannerId, Retcode.RetServerInternalError)
                : await ExecuteDraw(bannerId, times, execution);
        }
        finally { execution.Gate.Release(); }
    }

    private async ValueTask<DoGachaScRsp> ExecuteDraw(int bannerId, int times, ExecutionState execution)
    {
        var pools = GetPools();
        var banner = GetAvailableBanners(pools).FirstOrDefault(x => x.GachaId == bannerId);
        if (banner == null || Player.InventoryManager == null || Player.AvatarManager == null)
            return BuildGachaError(bannerId, Retcode.RetGachaIdNotExist);

        if (times is not (1 or 10))
            return BuildGachaError(bannerId, Retcode.RetGachaNumInvalid);

        var pityState = Data.PityStates.GetValueOrDefault(((int)GetPityFamily(banner.GachaType)).ToString())
            ?? new GachaPityState();
        if (banner.GachaType == GachaTypeEnum.Newbie && pityState.TotalPulls + times > NewbieGachaLimit)
            return BuildGachaError(bannerId, Retcode.RetGachaNewbieClose);

        var costItemId = banner.GachaType.GetCostItemId();
        var costItemCount = GetGachaCost(banner.GachaType, times);
        if (!HasEnoughItem(costItemId, costItemCount))
            return BuildGachaError(bannerId, Retcode.RetItemNotEnough);

        var rules = new GachaRules(banner);
        var (items, next) = rules.EvaluateBatch(times, pools.Selected, pools.GoldAvatars,
            pools.PurpleAvatars, pools.PurpleWeapons, pools.GoldWeapons, pools.BlueWeapons, Data);
        if (items.Any(id => !IsValidReward(id)))
            return BuildGachaError(bannerId, Retcode.RetGachaIdNotExist);

        // PutItem otherwise returns a detached light cone when the inventory is full.
        var equipmentCount = items.Count(id => GameData.ItemConfigData[id].ItemMainType == ItemMainTypeEnum.Equipment);
        if (Player.InventoryManager.Data.EquipmentItems.Count + equipmentCount > GameConstants.INVENTORY_MAX_EQUIPMENT)
            return BuildGachaError(bannerId, Retcode.RetEquipmentExceedLimit);
        if (items.Any(id => GameData.ItemConfigData[id].ItemMainType == ItemMainTypeEnum.AvatarCard &&
                (!GameData.AvatarConfigData.TryGetValue(id, out var avatar) || avatar.MaxRank < 6)))
            return BuildGachaError(bannerId, Retcode.RetGachaIdNotExist);
        if (items.Any(id => Player.AvatarManager.GetFormalAvatar(id) is { } owned && !owned.PathInfos.ContainsKey(id)))
            return BuildGachaError(bannerId, Retcode.RetServerInternalError);

        // Inventory managers are not transactional. Quarantine uncertain settlement instead of retrying it.
        execution.SettlementFailed = true;
        try
        {
            var removedCostItem = await Player.InventoryManager.RemoveItem(costItemId, costItemCount, sync: false);
            if (removedCostItem == null)
            {
                execution.SettlementFailed = false;
                return BuildGachaError(bannerId, Retcode.RetItemNotEnough);
            }
            var gachaItems = await GrantResults(bannerId, items, removedCostItem);
            GachaRules.ApplyState(Data, next);
            MarkGachaDataDirty();
            execution.SettlementFailed = false;
            return GachaProtocol.BuildResult(bannerId, times, gachaItems);
        }
        catch (Exception error)
        {
            MarkDirty();
            Log.Error($"Settlement interrupted ({error.GetType().Name}); further draws blocked until operator recovery.");
            return BuildGachaError(bannerId, Retcode.RetServerInternalError);
        }
    }

    private async ValueTask<List<GachaItem>> GrantResults(int bannerId, List<int> items, ItemData removedCostItem)
    {
        var gachaItems = new List<GachaItem>();
        var syncItems = new List<ItemData>();
        if (removedCostItem != null) syncItems.Add(removedCostItem);
        // get rarity of item
        foreach (var item in items)
        {
            var rarity = GetRarity(item);
            var avatar = Player.AvatarManager!.GetFormalAvatar(item);
            var rankUpItemId = item + 10000;
            var rankUpCount = Player.InventoryManager!.GetItem(rankUpItemId)?.Count ?? 0;
            var conversion = GachaRewards.GetConversion(rarity, GetType(item) == 1, avatar != null,
                avatar?.PathInfos[item].Rank ?? 0, rankUpCount);
            var dirt = conversion.Dust;
            var star = conversion.Star;
            Data.GachaHistory.Add(new GachaInfo
            {
                GachaId = bannerId, ItemId = item, Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            var gachaItem = new GachaItem();
            if (conversion.RankUp)
                gachaItem.TransferItemList = new ItemList
                {
                    ItemList_ = { new Item { ItemId = (uint)rankUpItemId, Num = 1 } }
                };

            ItemData? i;
            var isNewAvatar = false;
            if (GameData.ItemConfigData[item].ItemMainType == ItemMainTypeEnum.AvatarCard &&
                Player.AvatarManager!.GetFormalAvatar(item) == null)
            {
                i = null;
                isNewAvatar = true;
                if (await Player.AvatarManager!.AddAvatar(item, isGacha: true) == null)
                    throw new InvalidOperationException("Avatar reward was not applied");
            }

            else
            {
                i = await Player.InventoryManager!.AddItem(item, 1, false, sync: false, returnRaw: true);
                if (GetType(item) == 2 && i == null)
                    throw new InvalidOperationException("Equipment reward was not applied");
            }

            if (i != null) syncItems.Add(i);

            gachaItem.GachaItem_ = new Item
            {
                ItemId = (uint)item,
                Num = 1,
                Level = 1,
                Rank = 1
            };
            gachaItem.IsNew = isNewAvatar;

            var tokenItem = new ItemList();
            if (dirt > 0)
            {
                var it = await Player.InventoryManager!.AddItem(251, dirt, false, sync: false, returnRaw: true);
                if (it == null) throw new InvalidOperationException("Dust reward was not applied");
                if (it != null)
                {
                    var oldItem = syncItems.Find(x => x.ItemId == 251);
                    if (oldItem == null)
                        syncItems.Add(it);
                    else
                        oldItem.Count = it.Count;
                }

                tokenItem.ItemList_.Add(new Item
                {
                    ItemId = 251,
                    Num = (uint)dirt
                });
            }

            if (star > 0)
            {
                var it = await Player.InventoryManager!.AddItem(252, star, false, sync: false, returnRaw: true);
                if (it == null) throw new InvalidOperationException("Star reward was not applied");
                if (it != null)
                {
                    var oldItem = syncItems.Find(x => x.ItemId == 252);
                    if (oldItem == null)
                        syncItems.Add(it);
                    else
                        oldItem.Count = it.Count;
                }

                tokenItem.ItemList_.Add(new Item
                {
                    ItemId = 252,
                    Num = (uint)star
                });
            }

            gachaItem.TokenItem = tokenItem;

            gachaItem.TransferItemList ??= new ItemList();

            gachaItems.Add(gachaItem);
        }

        try { await Player.SendPacket(new PacketPlayerSyncScNotify(syncItems)); }
        catch (Exception error)
        {
            Log.Warn($"Inventory synchronization failed ({error.GetType().Name}); reconnect to refresh, do not replay settlement.");
        }
        return gachaItems;
    }

    public GetGachaInfoScRsp ToProto()
    {
        var execution = ExecutionStates.GetValue(Data, _ => new ExecutionState());
        execution.Gate.Wait();
        try { return BuildPage(); }
        finally { execution.Gate.Release(); }
    }

    private GetGachaInfoScRsp BuildPage()
    {
        var proto = new GetGachaInfoScRsp
        {
            GachaRandom = (uint)Random.Shared.Next(1000, 1999)
        };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var pools = GetPools();
        var fourStarCandidates = pools.PurpleAvatars.Concat(pools.PurpleWeapons).ToHashSet();
        foreach (var banner in GetAvailableBanners(pools, now))
        {
            if (banner.GachaType == GachaTypeEnum.Newbie &&
                Data.GetPityState(GachaPityFamilyEnum.Newbie).TotalPulls >= NewbieGachaLimit)
                continue;

            var newbiePulls = banner.GachaType == GachaTypeEnum.Newbie
                ? Data.GetPityState(GachaPityFamilyEnum.Newbie).TotalPulls
                : 0;
            var newbieLimit = banner.GachaType == GachaTypeEnum.Newbie ? NewbieGachaLimit : 0;
            proto.GachaInfoList.Add(GachaProtocol.BuildInfo(banner, pools.Selected, pools.GoldAvatars, newbiePulls,
                newbieLimit, banner.RateUpItems4.Where(fourStarCandidates.Contains).Distinct()));
            if (banner.GachaType is GachaTypeEnum.AvatarUp or GachaTypeEnum.CollaborationAvatarUp)
                proto.AKMDBPNEHDE.Add(GachaProtocol.BuildSelection((uint)banner.GachaId,
                    (uint)Data.CharacterEventDecideItemType, pools.Selected.Select(id => (uint)id)));
        }

        return proto;
    }

    public Retcode SetCharacterEventNonFeaturedPool(int gachaId, int decideItemType, IEnumerable<uint> selectedItems)
    {
        var execution = ExecutionStates.GetValue(Data, _ => new ExecutionState());
        execution.Gate.Wait();
        try
        {
            if (execution.SettlementFailed) return Retcode.RetServerInternalError;
            return SetSelection(gachaId, decideItemType, selectedItems);
        }
        finally { execution.Gate.Release(); }
    }

    public async ValueTask<Retcode> SetCharacterEventNonFeaturedPoolAsync(int gachaId, int decideItemType,
        IEnumerable<uint> selectedItems)
    {
        var execution = ExecutionStates.GetValue(Data, _ => new ExecutionState());
        await execution.Gate.WaitAsync();
        try
        {
            if (execution.SettlementFailed) return Retcode.RetServerInternalError;
            return SetSelection(gachaId, decideItemType, selectedItems);
        }
        finally { execution.Gate.Release(); }
    }

    private Retcode SetSelection(int gachaId, int decideItemType, IEnumerable<uint> selectedItems)
    {
        var banner = GetAvailableBanners(GetPools()).FirstOrDefault(x => x.GachaId == gachaId);
        if (banner == null)
            return Retcode.RetGachaIdNotExist;

        if (banner.GachaType is not (GachaTypeEnum.AvatarUp or GachaTypeEnum.CollaborationAvatarUp))
            return Retcode.RetGachaDecideItemTypeInvalid;

        var selected = selectedItems.Select(id => (int)id).Distinct().ToList();
        if (selected.Count != CelestialInvitationPoolSize)
            return Retcode.RetGachaDecideItemIdInvalid;

        var allowed = GetAllGoldAvatars().ToHashSet();
        if (selected.Any(id => !allowed.Contains(id)))
            return Retcode.RetGachaDecideItemIdInvalid;

        Data.CharacterEventNonFeaturedPool = selected;
        Data.GachaDecideOrder = selected;
        Data.CharacterEventDecideItemType = decideItemType;
        MarkDirty();
        return Retcode.RetSucc;
    }

    public List<int> GetCharacterEventNonFeaturedPool()
    {
        var execution = ExecutionStates.GetValue(Data, _ => new ExecutionState());
        execution.Gate.Wait();
        try { return Data.CharacterEventNonFeaturedPool.Take(CelestialInvitationPoolSize).ToList(); }
        finally { execution.Gate.Release(); }
    }

    private void EnsureCharacterEventNonFeaturedPool()
    {
        Data.CharacterEventNonFeaturedPool ??= [];
        var allowed = GetAllGoldAvatars().ToHashSet();
        var selected = Data.CharacterEventNonFeaturedPool
            .Where(allowed.Contains)
            .Distinct()
            .Take(CelestialInvitationPoolSize)
            .ToList();

        foreach (var id in Data.GachaDecideOrder.Where(allowed.Contains))
        {
            if (selected.Count >= CelestialInvitationPoolSize) break;
            if (!selected.Contains(id)) selected.Add(id);
        }

        foreach (var id in GetGoldAvatars().Where(allowed.Contains))
        {
            if (selected.Count >= CelestialInvitationPoolSize) break;
            if (!selected.Contains(id)) selected.Add(id);
        }

        Data.CharacterEventNonFeaturedPool = selected;
        Data.GachaDecideOrder = selected;
    }

    private static GachaPityFamilyEnum GetPityFamily(GachaTypeEnum gachaType)
    {
        return gachaType switch
        {
            GachaTypeEnum.Newbie => GachaPityFamilyEnum.Newbie,
            GachaTypeEnum.Normal => GachaPityFamilyEnum.Normal,
            GachaTypeEnum.WeaponUp => GachaPityFamilyEnum.WeaponUp,
            GachaTypeEnum.AvatarUp => GachaPityFamilyEnum.AvatarUp,
            GachaTypeEnum.CollaborationAvatarUp => GachaPityFamilyEnum.AvatarCollaboration,
            GachaTypeEnum.CollaborationWeaponUp => GachaPityFamilyEnum.WeaponCollaboration,
            _ => GachaPityFamilyEnum.Normal
        };
    }

    private sealed record Pools(List<int> Selected, List<int> GoldAvatars, List<int> PurpleAvatars,
        List<int> PurpleWeapons, List<int> GoldWeapons, List<int> BlueWeapons);

    private Pools GetPools() => new(Data.CharacterEventNonFeaturedPool.Take(CelestialInvitationPoolSize).ToList(),
        GetGoldAvatars(), GetPurpleAvatars(), GetPurpleWeapons(), GetGoldWeapons(), GetBlueWeapons());

    private bool IsValidReward(int id)
    {
        if (!GameData.ItemConfigData.TryGetValue(id, out var item)) return false;
        return item.ItemMainType switch
        {
            ItemMainTypeEnum.AvatarCard => GameData.AvatarConfigData.TryGetValue(id, out var avatar) &&
                avatar.MaxRank >= 6 && GameData.ItemConfigData.ContainsKey(id + 10000),
            ItemMainTypeEnum.Equipment => GameData.EquipmentConfigData.ContainsKey(id),
            _ => false
        };
    }

    private IEnumerable<BannerConfig> GetAvailableBanners(Pools pools, long? time = null)
    {
        var now = time ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var duplicates = GameData.BannersConfig.Banners.GroupBy(b => b.GachaId)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        foreach (var banner in GameData.BannersConfig.Banners)
        {
            if (banner.GachaId <= 0 || duplicates.Contains(banner.GachaId) ||
                !banner.IsActiveAt(now) || banner.GachaType.GetCostItemId() == 0) continue;
            // Existing banner files mix preview five-stars into RateUpItems4; rules filter that list by rarity.
            if (banner.RateUpItems5.Any(id => GetRarity(id) != 5) ||
                banner.RateUpItems4.Any(id => GetRarity(id) is not (4 or 5))) continue;

            var gold = banner.GachaType switch
            {
                GachaTypeEnum.Newbie => pools.GoldAvatars,
                GachaTypeEnum.AvatarUp or GachaTypeEnum.CollaborationAvatarUp =>
                    pools.Selected.Count > 0 ? pools.Selected : pools.GoldAvatars,
                GachaTypeEnum.WeaponUp or GachaTypeEnum.CollaborationWeaponUp => pools.GoldWeapons,
                _ => pools.GoldAvatars.Concat(pools.GoldWeapons).ToList()
            };
            var purple = pools.PurpleAvatars.Concat(pools.PurpleWeapons).ToList();
            var candidates = gold.Concat(purple).Concat(pools.BlueWeapons)
                .Concat(banner.RateUpItems5).Concat(banner.RateUpItems4);
            if (gold.Count == 0 || purple.Count == 0 || pools.BlueWeapons.Count == 0 ||
                candidates.Any(id => !IsValidReward(id))) continue;
            if (new[] { banner.GachaType.GetCostItemId(), 251, 252 }.Any(id =>
                    !GameData.ItemConfigData.TryGetValue(id, out var item) ||
                    item.ItemMainType != ItemMainTypeEnum.Material || item.PileLimit < 100)) continue;
            yield return banner;
        }
    }

    private bool HasEnoughItem(int itemId, int count)
    {
        if (itemId <= 0 || count <= 0) return true;
        var item = Player.InventoryManager?.GetItem(itemId);
        return item is { Count: >= 0 } && item.Count >= count;
    }

    private static int GetGachaCost(GachaTypeEnum gachaType, int times)
    {
        if (gachaType == GachaTypeEnum.Newbie && times == 10)
            return NewbieTenPullCost;

        return times;
    }

    private static bool IsGachaEligibleFourStarLightCone(int equipmentId)
    {
        return !IsBattlePassLightCone(equipmentId) && !IsEventLightCone(equipmentId);
    }

    private static bool IsBattlePassLightCone(int equipmentId)
    {
        return GameData.BattlePassRewardItemIds.Contains(equipmentId) ||
               KnownBattlePassLightCones.Contains(equipmentId);
    }

    private static bool IsEventLightCone(int equipmentId)
    {
        return equipmentId is >= 22000 and < 23000;
    }

    private static DoGachaScRsp BuildGachaError(int bannerId, Retcode retcode)
    {
        return GachaProtocol.BuildError(bannerId, retcode);
    }

    private int GetFirstAvatarUpGachaId()
    {
        return GameData.BannersConfig.Banners.FirstOrDefault(banner => banner.GachaType == GachaTypeEnum.AvatarUp)
            ?.GachaId ?? 0;
    }

    private void MarkGachaDataDirty()
    {
        MarkDirty();
    }
}
