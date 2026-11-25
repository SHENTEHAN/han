using InvoiceEye.Core.Lottery;
using InvoiceEye.Core.Models;

namespace InvoiceEye.Tests;

public class LotteryTests
{
    private static readonly InvoicePeriod P = new(115, 12);
    private static readonly WinningNumbers W = new(P, "11111111", "22222222", new[] { "12345678", "87654321", "55554444" });

    [Theory]
    [InlineData("11111111", Prize.Special)]
    [InlineData("22222222", Prize.Grand)]
    [InlineData("12345678", Prize.First)]
    [InlineData("02345678", Prize.Second)]
    [InlineData("00345678", Prize.Third)]
    [InlineData("00045678", Prize.Fourth)]
    [InlineData("00005678", Prize.Fifth)]
    [InlineData("00000678", Prize.Sixth)]
    [InlineData("00000078", Prize.None)]
    [InlineData("99994444", Prize.Fifth)]     // 比對第三組頭獎
    public void Matches_prizes(string digits, Prize expected) => Assert.Equal(expected, LotteryChecker.Match(digits, W));

    [Fact]
    public void Additional_sixth_prize()
    {
        var w = W with { AdditionalSixth = new[] { "999" } };
        Assert.Equal(Prize.Sixth, LotteryChecker.Match("00000999", w));
    }

    [Fact]
    public void Period_dates_follow_draw_and_claim_rules()
    {
        // 115 年 11-12 月：116/1/25 開獎，116/2/6 ~ 116/5/5 領獎
        Assert.Equal(new DateOnly(2027, 1, 25), P.DrawDate);
        Assert.Equal(new DateOnly(2027, 2, 6), P.ClaimStart);
        Assert.Equal(new DateOnly(2027, 5, 5), P.ClaimEnd);

        var sepOct = new InvoicePeriod(115, 10);
        Assert.Equal(new DateOnly(2026, 11, 25), sepOct.DrawDate);
        Assert.Equal(new DateOnly(2027, 3, 5), sepOct.ClaimEnd);
    }

    [Fact]
    public void Status_depends_on_date_and_numbers()
    {
        Assert.Equal(LotteryStatus.NotDrawn, LotteryChecker.Check("12345678", P, W, new DateOnly(2027, 1, 24)).Status);
        Assert.Equal(LotteryStatus.WinningNumbersMissing, LotteryChecker.Check("12345678", P, null, new DateOnly(2027, 1, 25)).Status);
        var won = LotteryChecker.Check("00000678", P, W, new DateOnly(2027, 2, 1));
        Assert.Equal(LotteryStatus.Won, won.Status);
        Assert.Equal(200m, won.PrizeAmount);
        Assert.Contains("六獎", won.Message);
        Assert.Contains("2027/5/5", won.Message);
        Assert.Equal(LotteryStatus.WonExpired, LotteryChecker.Check("00000678", P, W, new DateOnly(2027, 5, 6)).Status);
        Assert.Equal(LotteryStatus.NoPrize, LotteryChecker.Check("00000000", P, W, new DateOnly(2027, 2, 1)).Status);
        Assert.Equal(LotteryStatus.InvalidNumber, LotteryChecker.Check(null, P, W, new DateOnly(2027, 2, 1)).Status);
    }

    [Theory]
    [InlineData("115年11-12月", "11512")]
    [InlineData("115 11-12", "11512")]
    [InlineData("中華民國115年01-02月", "11502")]
    public void Parses_printed_period(string text, string key)
    {
        Assert.True(InvoicePeriod.TryParsePrinted(text, out var p));
        Assert.Equal(key, p.Key);
    }

    [Theory]
    [InlineData("115年11-13月")]
    [InlineData("115年10-11月")]
    [InlineData("沒有期別")]
    public void Rejects_bad_printed_period(string text) => Assert.False(InvoicePeriod.TryParsePrinted(text, out _));

    [Fact]
    public void Validates_winning_number_format()
    {
        var bad = new WinningNumbers(P, "123", "22222222", Array.Empty<string>());
        Assert.Equal(2, bad.Validate().Count());
        Assert.Empty(W.Validate());
    }
}
