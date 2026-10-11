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
    private static readonly ConditionalWeakTable<GachaData, SemaphoreSlim> DrawGates = new();

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
        if (GetAllGoldAvatars().Contains(itemId) || GetAllGoldWeapons().Contains(itemId)) return 5;

        if (GetPurpleAvatars().Contains(itemId) || GetPurpleWeapons().Contains(itemId)) return 4;

        if (GetBlueWeapons().Contains(itemId)) return 3;

        return 0;
    }

    public int GetType(int itemId)
    {
        if (GetAllGoldAvatars().Contains(itemId) || GetPurpleAvatars().Contains(itemId)) return 1;

        if (GetAllGoldWeapons().Contains(itemId) || GetPurpleWeapons().Contains(itemId) ||
            GetBlueWeapons().Contains(itemId)) return 2;

        return 0;
    }

    public async ValueTask<DoGachaScRsp?> DoGacha(int bannerId, int times)
    {
        var gate = DrawGates.GetValue(Data, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { return await ExecuteDraw(bannerId, times); }
        finally { gate.Release(); }
    }

    private async ValueTask<DoGachaScRsp> ExecuteDraw(int bannerId, int times)
    {
        var banner = GameData.BannersConfig.Banners.Find(x => x.GachaId == bannerId);
        if (banner == null || !banner.IsActiveAt(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) ||
            banner.GachaType.GetCostItemId() == 0 || Player.InventoryManager == null || Player.AvatarManager == null)
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

        // Resolved before anything is charged: a banner can list rate-up ids the loaded resources do not
        // contain, in which case a draw yields 0 and the ItemConfigData lookup further down would throw
        // after the tickets were already spent.
        var fallbackItem = GetBlueWeapons().FirstOrDefault(GameData.ItemConfigData.ContainsKey);
        if (fallbackItem == 0) return BuildGachaError(bannerId, Retcode.RetGachaIdNotExist);

        var selected = Data.CharacterEventNonFeaturedPool.Take(CelestialInvitationPoolSize).ToList();
        var goldAvatars = GetGoldAvatars();
        var purpleAvatars = GetPurpleAvatars();
        var purpleWeapons = GetPurpleWeapons();
        var goldWeapons = GetGoldWeapons();
        var blueWeapons = GetBlueWeapons();
        if (banner.RateUpItems5.Concat(banner.RateUpItems4).Any(id =>
                !GameData.ItemConfigData.ContainsKey(id) || GetRarity(id) == 0))
            return BuildGachaError(bannerId, Retcode.RetGachaIdNotExist);

        var rules = new GachaRules(banner);
        var next = Data;
        var items = new List<int>();
        for (var index = 0; index < times; index++)
        {
            var roll = rules.Evaluate(selected, goldAvatars, purpleAvatars, purpleWeapons,
                goldWeapons, blueWeapons, next);
            var item = roll.ItemId;
            if (item == 0 || !GameData.ItemConfigData.ContainsKey(item)) item = fallbackItem;
            items.Add(item);
            next = roll.NextState;
        }

        // PutItem otherwise returns a detached light cone when the inventory is full.
        var equipmentCount = items.Count(id => GameData.ItemConfigData[id].ItemMainType == ItemMainTypeEnum.Equipment);
        if (Player.InventoryManager.Data.EquipmentItems.Count + equipmentCount > GameConstants.INVENTORY_MAX_EQUIPMENT)
            return BuildGachaError(bannerId, Retcode.RetEquipmentExceedLimit);
        if (items.Any(id => GameData.ItemConfigData[id].ItemMainType == ItemMainTypeEnum.AvatarCard &&
                (!GameData.AvatarConfigData.TryGetValue(id, out var avatar) || avatar.MaxRank < 6)))
            return BuildGachaError(bannerId, Retcode.RetGachaIdNotExist);

        var removedCostItem = await Player.InventoryManager.RemoveItem(costItemId, costItemCount, sync: false);
        if (removedCostItem == null) return BuildGachaError(bannerId, Retcode.RetItemNotEnough);
        GachaRules.ApplyState(Data, next);
        MarkGachaDataDirty();
        var gachaItems = await GrantResults(bannerId, items, removedCostItem);
        return GachaProtocol.BuildResult(bannerId, times, gachaItems);
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
                await Player.AvatarManager!.AddAvatar(item, isGacha: true);
            }

            else
            {
                i = await Player.InventoryManager!.AddItem(item, 1, false, sync: false, returnRaw: true);
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

        await Player.SendPacket(new PacketPlayerSyncScNotify(syncItems));
        return gachaItems;
    }

    public GetGachaInfoScRsp ToProto()
    {
        var proto = new GetGachaInfoScRsp
        {
            GachaRandom = (uint)Random.Shared.Next(1000, 1999)
        };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var banner in GameData.BannersConfig.Banners)
        {
            if (banner.GachaType == GachaTypeEnum.Newbie &&
                Data.GetPityState(GachaPityFamilyEnum.Newbie).TotalPulls >= NewbieGachaLimit)
                continue;

            // The client hides a banner whose window has closed anyway; skipping it here keeps the
            // response honest and makes DoGacha's RetGachaIdNotExist the only "no such pool" path.
            if (!banner.IsActiveAt(now)) continue;

            var newbiePulls = banner.GachaType == GachaTypeEnum.Newbie
                ? Data.GetPityState(GachaPityFamilyEnum.Newbie).TotalPulls
                : 0;
            var newbieLimit = banner.GachaType == GachaTypeEnum.Newbie ? NewbieGachaLimit : 0;
            proto.GachaInfoList.Add(banner.ToInfo(GetCharacterEventNonFeaturedPool(), GetGoldAvatars(), newbiePulls,
                newbieLimit));
        }

        // TODO 4.3: 4.2 的角色活动 decide-item / featured pool 下发结构 (NMBAAOBBJMI / OEIEJHBCOOM)
        // 在 4.3 GetGachaInfoScRsp 重排后无对应字段，暂不下发自选池信息。

        return proto;
    }

    public Retcode SetCharacterEventNonFeaturedPool(int gachaId, int decideItemType, IEnumerable<uint> selectedItems)
    {
        var banner = GameData.BannersConfig.Banners.Find(x => x.GachaId == gachaId);
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
        EnsureCharacterEventNonFeaturedPool();
        return Data.CharacterEventNonFeaturedPool.Take(CelestialInvitationPoolSize).ToList();
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
