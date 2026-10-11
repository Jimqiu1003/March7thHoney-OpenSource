using GachaRegression;
using March7thHoney.Data.Custom;
using March7thHoney.Database.Gacha;
using March7thHoney.Enums;
using MemoryPack;
using Newtonsoft.Json;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}

var types = new[] { GachaTypeEnum.Newbie, GachaTypeEnum.Normal, GachaTypeEnum.AvatarUp,
    GachaTypeEnum.WeaponUp, GachaTypeEnum.CollaborationAvatarUp, GachaTypeEnum.CollaborationWeaponUp };
foreach (var type in types)
{
    var banner = new BannerConfig { GachaId = 1001, GachaType = type,
        RateUpItems5 = [999], RateUpItems4 = [301, 401] };
    var family = type switch {
        GachaTypeEnum.Newbie => GachaPityFamilyEnum.Newbie,
        GachaTypeEnum.AvatarUp => GachaPityFamilyEnum.AvatarUp,
        GachaTypeEnum.WeaponUp => GachaPityFamilyEnum.WeaponUp,
        GachaTypeEnum.CollaborationAvatarUp => GachaPityFamilyEnum.AvatarCollaboration,
        GachaTypeEnum.CollaborationWeaponUp => GachaPityFamilyEnum.WeaponCollaboration,
        _ => GachaPityFamilyEnum.Normal };
    var data = new GachaData();
    var state = data.GetPityState(family);
    state.PullsSinceFiveStar = type == GachaTypeEnum.Newbie ? 49 :
        type is GachaTypeEnum.WeaponUp or GachaTypeEnum.CollaborationWeaponUp ? 79 : 89;
    state.PullsSinceFourStarOrHigher = 9;
    state.NextFiveStarGuaranteedFeatured = true;
    var result = banner.DoGacha([101], [101], [301], [401], [201], [501], data);
    Check(result is 101 or 201 or 999, $"{type}: hard pity returns five-star");
    Check(state.TotalPulls == 1 && state.PullsSinceFiveStar == 0 && state.PullsSinceFourStarOrHigher == 0,
        $"{type}: hard pity counters");
    Check(data.PityStates.Count == 1, $"{type}: separate family");
    var bytes = MemoryPackSerializer.Serialize(state);
    Check(MemoryPackSerializer.Serialize(MemoryPackSerializer.Deserialize<GachaPityState>(bytes)!).SequenceEqual(bytes),
        $"{type}: persistence layout roundtrip");
}
var existing = new GachaData { PityStates = { ["11"] = new GachaPityState { TotalPulls = 123 } },
    LastGachaFailedCount = 80 };
var before = JsonConvert.SerializeObject(existing);
existing.EnsurePityStateMigrated();
Check(JsonConvert.SerializeObject(existing) == before, "existing pity is not remigrated");
var legacy = new GachaData { LastGachaFailedCount = 74, LastGachaPurpleFailedCount = 8,
    LastAvatarGachaFailed = true };
legacy.EnsurePityStateMigrated();
Check(legacy.GetPityState(GachaPityFamilyEnum.AvatarUp).PullsSinceFiveStar == 74, "legacy migration");
Check(legacy.GetPityState(GachaPityFamilyEnum.AvatarUp).NextFiveStarGuaranteedFeatured, "legacy guarantee");
Console.WriteLine($"PASS {checks} baseline checks");
