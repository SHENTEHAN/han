namespace InvoiceEye.Core.Classification;

/// <summary>品名關鍵字與行業別對應到分類的字典。可以自行增減。</summary>
public static class KeywordDictionary
{
    // 順序有意義：越前面越優先（例如「停車」要先於「車」）
    private static readonly (string Keyword, string Category)[] ItemKeywords =
    {
        // 交通
        ("汽油", Categories.Transport), ("柴油", Categories.Transport), ("加油", Categories.Transport),
        ("停車", Categories.Transport), ("車票", Categories.Transport), ("高鐵", Categories.Transport),
        ("台鐵", Categories.Transport), ("客運", Categories.Transport), ("計程車", Categories.Transport),
        ("悠遊卡", Categories.Transport), ("一卡通", Categories.Transport), ("月票", Categories.Transport),
        ("ETC", Categories.Transport), ("洗車", Categories.Transport),

        // 醫療
        ("掛號", Categories.Medical), ("藥", Categories.Medical), ("口罩", Categories.Medical),
        ("OK繃", Categories.Medical), ("維他命", Categories.Medical), ("酸痛貼布", Categories.Medical),

        // 日用品
        ("衛生紙", Categories.Daily), ("面紙", Categories.Daily), ("洗衣", Categories.Daily),
        ("洗髮", Categories.Daily), ("沐浴", Categories.Daily), ("牙膏", Categories.Daily),
        ("牙刷", Categories.Daily), ("洗碗", Categories.Daily), ("垃圾袋", Categories.Daily),
        ("電池", Categories.Daily), ("清潔", Categories.Daily), ("濕紙巾", Categories.Daily),
        ("文具", Categories.Daily), ("原子筆", Categories.Daily), ("膠帶", Categories.Daily),
        ("購物袋", Categories.Daily), ("塑膠袋", Categories.Daily),

        // 教育
        ("書", Categories.Education), ("課程", Categories.Education), ("講義", Categories.Education),
        ("補習", Categories.Education),

        // 娛樂
        ("電影", Categories.Entertainment), ("遊戲", Categories.Entertainment), ("KTV", Categories.Entertainment),
        ("門票", Categories.Entertainment), ("點數卡", Categories.Entertainment), ("桌遊", Categories.Entertainment),

        // 居住
        ("房租", Categories.Housing), ("租金", Categories.Housing), ("水費", Categories.Housing),
        ("電費", Categories.Housing), ("瓦斯", Categories.Housing), ("管理費", Categories.Housing),
        ("網路", Categories.Housing),

        // 餐飲（放最後，因為字比較泛）
        ("便當", Categories.Food), ("飯糰", Categories.Food), ("御飯糰", Categories.Food),
        ("麵包", Categories.Food), ("三明治", Categories.Food), ("漢堡", Categories.Food),
        ("咖啡", Categories.Food), ("拿鐵", Categories.Food), ("美式", Categories.Food),
        ("奶茶", Categories.Food), ("紅茶", Categories.Food), ("綠茶", Categories.Food),
        ("豆漿", Categories.Food), ("鮮奶", Categories.Food), ("牛奶", Categories.Food),
        ("優格", Categories.Food), ("果汁", Categories.Food), ("汽水", Categories.Food),
        ("可樂", Categories.Food), ("礦泉水", Categories.Food), ("茶葉蛋", Categories.Food),
        ("關東煮", Categories.Food), ("便利餐", Categories.Food), ("飯", Categories.Food),
        ("麵", Categories.Food), ("餅乾", Categories.Food), ("零食", Categories.Food),
        ("糖果", Categories.Food), ("巧克力", Categories.Food), ("冰淇淋", Categories.Food),
        ("雞", Categories.Food), ("豬", Categories.Food), ("牛肉", Categories.Food),
        ("蛋", Categories.Food), ("水果", Categories.Food), ("蔬菜", Categories.Food),
        ("餐", Categories.Food), ("飲料", Categories.Food), ("茶", Categories.Food),
    };

    private static readonly (string Keyword, string Category)[] IndustryKeywords =
    {
        ("加油站", Categories.Transport), ("停車場", Categories.Transport), ("汽車", Categories.Transport),
        ("機車", Categories.Transport), ("運輸", Categories.Transport), ("計程車", Categories.Transport),
        ("藥局", Categories.Medical), ("藥品", Categories.Medical), ("醫療", Categories.Medical),
        ("診所", Categories.Medical), ("醫院", Categories.Medical),
        ("電影", Categories.Entertainment), ("遊樂", Categories.Entertainment), ("視聽歌唱", Categories.Entertainment),
        ("運動場館", Categories.Entertainment),
        ("書籍", Categories.Education), ("文具", Categories.Education), ("補習", Categories.Education),
        ("教育", Categories.Education),
        ("不動產租賃", Categories.Housing), ("電力", Categories.Housing), ("自來水", Categories.Housing),
        ("燃氣", Categories.Housing), ("電信", Categories.Housing),
        ("餐館", Categories.Food), ("餐廳", Categories.Food), ("飲料店", Categories.Food),
        ("飲料", Categories.Food), ("小吃", Categories.Food), ("烘焙", Categories.Food),
        ("早餐", Categories.Food), ("便當", Categories.Food), ("咖啡", Categories.Food),
        ("日常用品", Categories.Daily), ("清潔用品", Categories.Daily), ("五金", Categories.Daily),
        ("化粧品", Categories.Daily),
        // 便利商店、量販店、超市品項太雜，不直接給分類，交給品名或使用者決定
    };

    public static string? CategoryForItem(string itemName)
    {
        foreach (var (k, c) in ItemKeywords)
            if (itemName.Contains(k, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    public static string? CategoryForIndustry(string industry)
    {
        foreach (var (k, c) in IndustryKeywords)
            if (industry.Contains(k, StringComparison.Ordinal)) return c;
        return null;
    }
}
