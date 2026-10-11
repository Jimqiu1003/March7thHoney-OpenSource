using March7thHoney.Enums;
using March7thHoney.Proto;
namespace March7thHoney.Data.Custom;

public static class GachaProtocol
{
    public static NEIMLKNMDBM BuildSelection(uint bannerId, uint decideItemType, IEnumerable<uint> order) => new()
    {
        MMELNCHIDNC = bannerId,
        GFANBHAEKOK = decideItemType,
        OJNEFBJHCCK = { order }
    };

    public static GachaInfo BuildInfo(BannerConfig banner, List<int> decideOrder,
        List<int> goldAvatar, int gachaPullCount = 0, int gachaPullLimit = 0,
        IEnumerable<int>? fourStarItems = null)
    {
        var info = new GachaInfo
        {
            GachaId = (uint)banner.GachaId,
            DetailUrl = "",
            HistoryUrl = ""
        };

        if (banner.GachaType != GachaTypeEnum.Normal)
        {
            info.BeginTime = banner.BeginTime;
            info.EndTime = banner.EndTime;
        }

        // TODO 4.3: 4.2 的新手池抽数/上限字段 FJIBOAGDNDG(tag13)/OKFNNHNLBOO(tag14) 在 4.3
        // proto 整体重排后已无对应 tag，且剩余混淆字段语义不明，暂不下发新手池进度计数。
        // 抽卡不在登录路径，不影响主流程。
        _ = gachaPullCount;
        _ = gachaPullLimit;

        if (banner.GachaId == 1001)
        {
            if (banner.RateUpItems5.Count > 0)
            {
                info.PrizeItemList.AddRange(banner.RateUpItems5.Select(id => (uint)id));
            }

            info.GachaCeiling = new GachaCeiling
            {
                IsClaimed = true, // TODO: Implement this
                AvatarList = { goldAvatar.Select(id => new GachaCeilingAvatar { AvatarId = (uint)id }) }
            };
        }
        else
        {
            if (banner.RateUpItems5.Count > 0)
            {
                info.PrizeItemList.AddRange(banner.RateUpItems5.Select(id => (uint)id));
            }

            // Observed official packet behavior: avatar event banners carry marker "11" here.
            if (banner.GachaType is GachaTypeEnum.AvatarUp or GachaTypeEnum.CollaborationAvatarUp)
                info.ItemDetailList.Add(11);
        }

        // Featured (rate-up) pool the client renders in the banner's probability panel. This is the
        // field the 4.2 code filled as CBHAEFNAFLE; the 4.3 proto rename left it unset, which leaves an
        // event banner with an empty rate-up section.
        info.ONIHOMCELJK.AddRange((fourStarItems ?? banner.RateUpItems4).Select(id => (uint)id));

        return info;
    }

    public static DoGachaScRsp BuildResult(int bannerId, int times, IEnumerable<GachaItem> items)
    {
        // shortcut: progress fields remain unset until the target live-build mapping is confirmed.
        var response = new DoGachaScRsp { GachaId = (uint)bannerId, GachaNum = (uint)times };
        response.GachaItemList.AddRange(items);
        return response;
    }

    public static DoGachaScRsp BuildError(int bannerId, Retcode retcode) => new()
    {
        GachaId = (uint)Math.Max(0, bannerId), Retcode = (uint)retcode
    };
}
