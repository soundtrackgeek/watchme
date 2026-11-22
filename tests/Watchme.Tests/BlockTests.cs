using Watchme.Core;

namespace Watchme.Tests;

public class BlockTests
{
    private const long Min = 60_000;
    private const long Sec = 1_000;

    [Fact]
    public void Short_detour_is_folded_into_the_surrounding_block()
    {
        var spans = new List<Span>
        {
            new(0, 10 * Min, "Code.exe", "a.cs"),
            new(10 * Min, 10 * Min + 30 * Sec, "Discord.exe", null),
            new(10 * Min + 30 * Sec, 20 * Min, "Code.exe", "b.cs"),
        };

        var blocks = DailyReport.BuildBlocks(spans, DailyReport.MergeGapMs);

        var code = Assert.Single(blocks, b => b.App == "Code.exe");
        Assert.Equal((0L, 20 * Min), (code.Start, code.End));
        Assert.Equal(20 * Min - 30 * Sec, code.ActiveMs);
        Assert.Equal("a.cs", code.MainTitle);
        Assert.Single(blocks, b => b.App == "Discord.exe");
    }

    [Fact]
    public void Long_switch_starts_a_new_block()
    {
        var spans = new List<Span>
        {
            new(0, 10 * Min, "Code.exe", null),
            new(10 * Min, 20 * Min, "TOTALCMD64.EXE", null),
            new(20 * Min, 30 * Min, "Code.exe", null),
        };

        var blocks = DailyReport.BuildBlocks(spans, DailyReport.MergeGapMs);

        Assert.Equal(["Code.exe", "TOTALCMD64.EXE", "Code.exe"], blocks.Select(b => b.App));
    }

    [Fact]
    public void Short_idle_gap_inside_one_app_stays_one_block()
    {
        var spans = new List<Span>
        {
            new(0, 10 * Min, "Code.exe", null),
            new(11 * Min, 20 * Min, "Code.exe", null),
        };

        var block = Assert.Single(DailyReport.BuildBlocks(spans, DailyReport.MergeGapMs));
        Assert.Equal(19 * Min, block.ActiveMs);
    }
}
