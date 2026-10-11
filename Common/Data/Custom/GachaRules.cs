using March7thHoney.Database.Gacha;
using March7thHoney.Enums;
using MemoryPack;

namespace March7thHoney.Data.Custom;

public sealed class GachaRules(BannerConfig banner, Random? randomSource = null)
{
    private readonly Random random = randomSource ?? Random.Shared;

    public (int ItemId, GachaData NextState) Evaluate(List<int> selected, List<int> goldAvatars,
        List<int> purpleAvatars, List<int> purpleWeapons, List<int> goldWeapons, List<int> blueWeapons,
        GachaData current)
    {
        var next = CloneState(current);
        var item = Draw(selected, goldAvatars, purpleAvatars, purpleWeapons, goldWeapons, blueWeapons, next);
        return (item, next);
    }

    internal int Draw(List<int> characterEventNonFeaturedPool, List<int> standardGoldAvatars,
        List<int> purpleAvatars, List<int> purpleWeapons, List<int> standardGoldWeapons,
        List<int> blueWeapons, GachaData data)
    {
        var profile = ResolveRuleProfile();
        var state = data.GetPityState(profile.Family);
        var fiveStarPullNumber = state.PullsSinceFiveStar + 1;
        var fourStarPullNumber = state.PullsSinceFourStarOrHigher + 1;

        if (Chance(profile.FiveStarChanceAtPull(fiveStarPullNumber)))
        {
            var item = ResolveFiveStarItem(profile, state, characterEventNonFeaturedPool, standardGoldAvatars,
                standardGoldWeapons);
            state.RecordFiveStar();
            if (banner.GachaType == GachaTypeEnum.Newbie) state.NewbieFiveStarClaimed = true;
            SyncLegacyState(data, state, profile.Family);
            return item;
        }

        if (fourStarPullNumber >= profile.FourStarHardPity || Chance(profile.FourStarBaseChance))
        {
            var item = ResolveFourStarItem(profile, state, purpleAvatars, purpleWeapons);
            state.RecordFourStar();
            SyncLegacyState(data, state, profile.Family);
            return item;
        }

        var threeStarItem = PickOne(blueWeapons);
        state.RecordThreeStar();
        SyncLegacyState(data, state, profile.Family);
        return threeStarItem;
    }

    private static GachaData CloneState(GachaData data) => new()
    {
        LastAvatarGachaFailed = data.LastAvatarGachaFailed,
        LastWeaponGachaFailed = data.LastWeaponGachaFailed,
        LastGachaFailedCount = data.LastGachaFailedCount,
        LastGachaPurpleFailedCount = data.LastGachaPurpleFailedCount,
        PityStates = MemoryPackSerializer.Deserialize<Dictionary<string, GachaPityState>>(
            MemoryPackSerializer.Serialize(data.PityStates))!
    };

    public static void ApplyState(GachaData target, GachaData next)
    {
        target.LastAvatarGachaFailed = next.LastAvatarGachaFailed;
        target.LastWeaponGachaFailed = next.LastWeaponGachaFailed;
        target.LastGachaFailedCount = next.LastGachaFailedCount;
        target.LastGachaPurpleFailedCount = next.LastGachaPurpleFailedCount;
        target.PityStates = next.PityStates;
    }

    private GachaRuleProfile ResolveRuleProfile()
    {
        return banner.GachaType switch
        {
            GachaTypeEnum.Newbie => new GachaRuleProfile
            {
                Family = GachaPityFamilyEnum.Newbie,
                FiveStarHardPity = 50,
                FourStarHardPity = 10,
                FiveStarFeaturedChance = 0,
                FourStarFeaturedChance = 0,
                FourStarBaseChance = 0.051,
                FiveStarChanceAtPull = FiveStarChanceNewbie
            },
            GachaTypeEnum.WeaponUp => new GachaRuleProfile
            {
                Family = GachaPityFamilyEnum.WeaponUp,
                FiveStarHardPity = 80,
                FourStarHardPity = 10,
                FiveStarFeaturedChance = 0.75,
                FourStarFeaturedChance = 0.75,
                FourStarBaseChance = 0.066,
                HasFeaturedFiveStarGuarantee = true,
                HasFeaturedFourStarGuarantee = true,
                FiveStarChanceAtPull = FiveStarChance80
            },
            GachaTypeEnum.AvatarUp => new GachaRuleProfile
            {
                Family = GachaPityFamilyEnum.AvatarUp,
                FiveStarHardPity = 90,
                FourStarHardPity = 10,
                FiveStarFeaturedChance = 0.5,
                FourStarFeaturedChance = 0.5,
                FourStarBaseChance = 0.051,
                HasFeaturedFiveStarGuarantee = true,
                HasFeaturedFourStarGuarantee = true,
                FiveStarChanceAtPull = FiveStarChance90
            },
            // Collaboration pools share the AvatarUp / WeaponUp rules (banner.GachaTypeBasicInfo.json lists the
            // same UpPropability 50 / 75) but keep their own pity counters, like the official server.
            GachaTypeEnum.CollaborationAvatarUp => new GachaRuleProfile
            {
                Family = GachaPityFamilyEnum.AvatarCollaboration,
                FiveStarHardPity = 90,
                FourStarHardPity = 10,
                FiveStarFeaturedChance = 0.5,
                FourStarFeaturedChance = 0.5,
                FourStarBaseChance = 0.051,
                HasFeaturedFiveStarGuarantee = true,
                HasFeaturedFourStarGuarantee = true,
                FiveStarChanceAtPull = FiveStarChance90
            },
            GachaTypeEnum.CollaborationWeaponUp => new GachaRuleProfile
            {
                Family = GachaPityFamilyEnum.WeaponCollaboration,
                FiveStarHardPity = 80,
                FourStarHardPity = 10,
                FiveStarFeaturedChance = 0.75,
                FourStarFeaturedChance = 0.75,
                FourStarBaseChance = 0.066,
                HasFeaturedFiveStarGuarantee = true,
                HasFeaturedFourStarGuarantee = true,
                FiveStarChanceAtPull = FiveStarChance80
            },
            _ => new GachaRuleProfile
            {
                Family = GachaPityFamilyEnum.Normal,
                FiveStarHardPity = 90,
                FourStarHardPity = 10,
                FiveStarFeaturedChance = 0,
                FourStarFeaturedChance = 0,
                FourStarBaseChance = 0.051,
                FiveStarChanceAtPull = FiveStarChance90
            }
        };
    }

    private int ResolveFiveStarItem(GachaRuleProfile profile, GachaPityState state,
        List<int> characterEventNonFeaturedPool, List<int> standardGoldAvatars, List<int> standardGoldWeapons)
    {
        var featured = banner.RateUpItems5.Distinct().ToList();

        return banner.GachaType switch
        {
            GachaTypeEnum.Newbie => PickOne(standardGoldAvatars),
            GachaTypeEnum.WeaponUp or GachaTypeEnum.CollaborationWeaponUp => ResolveFeaturedOrStandardFiveStar(
                profile, state, featured, standardGoldWeapons),
            GachaTypeEnum.AvatarUp or GachaTypeEnum.CollaborationAvatarUp => ResolveFeaturedOrStandardFiveStar(
                profile, state, featured,
                GetCharacterEventNonFeaturedPool(characterEventNonFeaturedPool, standardGoldAvatars, featured)),
            _ => PickByCategory(standardGoldAvatars, standardGoldWeapons)
        };
    }

    private int ResolveFeaturedOrStandardFiveStar(GachaRuleProfile profile, GachaPityState state, List<int> featured,
        List<int> standard)
    {
        if (featured.Count == 0) return PickOne(standard);

        if (state.NextFiveStarGuaranteedFeatured || Chance(profile.FiveStarFeaturedChance) || standard.Count == 0)
        {
            state.NextFiveStarGuaranteedFeatured = false;
            return PickOne(featured);
        }

        state.NextFiveStarGuaranteedFeatured = true;
        return PickOne(standard);
    }

    private int ResolveFourStarItem(GachaRuleProfile profile, GachaPityState state, List<int> purpleAvatars,
        List<int> purpleWeapons)
    {
        var isWeaponPool = banner.GachaType is GachaTypeEnum.WeaponUp or GachaTypeEnum.CollaborationWeaponUp;
        var featured = isWeaponPool
            ? banner.RateUpItems4.Where(purpleWeapons.Contains).Distinct().ToList()
            : banner.RateUpItems4.Where(id => purpleAvatars.Contains(id) || purpleWeapons.Contains(id)).Distinct().ToList();

        if (!profile.HasFeaturedFourStarGuarantee || featured.Count == 0)
        {
            return isWeaponPool
                ? PickOne(WithoutFeatured(purpleAvatars.Concat(purpleWeapons), featured))
                : PickByCategory(purpleAvatars, purpleWeapons);
        }

        var standard = WithoutFeatured(purpleAvatars.Concat(purpleWeapons), featured);
        if (state.NextFourStarGuaranteedFeatured || Chance(profile.FourStarFeaturedChance) || standard.Count == 0)
        {
            state.NextFourStarGuaranteedFeatured = false;
            return PickOne(featured);
        }

        state.NextFourStarGuaranteedFeatured = true;
        return PickOne(standard);
    }

    private static List<int> GetCharacterEventNonFeaturedPool(List<int> selectedPool, List<int> fallbackPool,
        List<int> featured)
    {
        var pool = WithoutFeatured(selectedPool, featured);
        return pool.Count > 0 ? pool : WithoutFeatured(fallbackPool, featured);
    }

    private static List<int> WithoutFeatured(IEnumerable<int> items, List<int> featured)
    {
        var featuredSet = featured.ToHashSet();
        return items.Where(id => !featuredSet.Contains(id)).Distinct().ToList();
    }

    private int PickByCategory(List<int> avatars, List<int> weapons)
    {
        if (avatars.Count == 0) return PickOne(weapons);
        if (weapons.Count == 0) return PickOne(avatars);
        return Chance(0.5) ? PickOne(avatars) : PickOne(weapons);
    }

    private int PickOne(IReadOnlyList<int> items)
    {
        return items.Count == 0 ? 0 : items[random.Next(items.Count)];
    }

    private bool Chance(double chance)
    {
        return random.NextDouble() < chance;
    }

    private static double FiveStarChance90(int pullNumber)
    {
        if (pullNumber >= 90) return 1.0;
        return pullNumber >= 76 ? 0.324 : 0.006;
    }

    private static double FiveStarChance80(int pullNumber)
    {
        if (pullNumber >= 80) return 1.0;
        return pullNumber >= 66 ? 0.219633 : 0.008;
    }

    private static double FiveStarChanceNewbie(int pullNumber)
    {
        return pullNumber >= 50 ? 1.0 : 0.006;
    }

    private static void SyncLegacyState(GachaData data, GachaPityState state, GachaPityFamilyEnum family)
    {
        if (family is not (GachaPityFamilyEnum.AvatarUp or GachaPityFamilyEnum.WeaponUp)) return;

        data.LastGachaFailedCount = state.PullsSinceFiveStar;
        data.LastGachaPurpleFailedCount = state.PullsSinceFourStarOrHigher;
        if (family == GachaPityFamilyEnum.AvatarUp)
            data.LastAvatarGachaFailed = state.NextFiveStarGuaranteedFeatured;
        else
            data.LastWeaponGachaFailed = state.NextFiveStarGuaranteedFeatured;
    }

    private sealed class GachaRuleProfile
    {
        public GachaPityFamilyEnum Family { get; init; }
        public int FiveStarHardPity { get; init; }
        public int FourStarHardPity { get; init; }
        public double FiveStarFeaturedChance { get; init; }
        public double FourStarFeaturedChance { get; init; }
        public double FourStarBaseChance { get; init; }
        public Func<int, double> FiveStarChanceAtPull { get; init; } = _ => 0;
        public bool HasFeaturedFiveStarGuarantee { get; init; }
        public bool HasFeaturedFourStarGuarantee { get; init; }
    }
}
