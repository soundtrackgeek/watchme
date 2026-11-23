using Watchme.Core;
using static Watchme.Core.QuestionParser;

namespace Watchme.Tests;

public class AskTests
{
    private const long Min = 60_000;

    // A Monday afternoon.
    private static readonly DateTime Now = new(2026, 11, 23, 15, 0, 0, DateTimeKind.Local);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

    private static long Day(int offset) => Dashboard.DayStart(Today.AddDays(offset));
    private static long At(int dayOffset, int hour, int minute = 0) => Day(dayOffset) + (hour * 60 + minute) * Min;

    // ---------- reading questions ----------

    [Fact]
    public void The_three_examples_are_read()
    {
        var q = Parse("What did I do yesterday between 14 and 15?", Now);
        Assert.Equal(Intent.Overview, q.Intent);
        Assert.Equal((At(-1, 14), At(-1, 15)), (q.FromMs, q.ToMs));
        Assert.Empty(q.Terms);
        Assert.Equal("yesterday 14:00–15:00", q.When);

        q = Parse("Did I listen to a-ha in the last 2 weeks?", Now);
        Assert.Equal(Intent.Exists, q.Intent);
        Assert.Equal("listen", q.Topic?.Name);
        Assert.Equal(["a-ha"], q.Terms);
        Assert.Equal((Day(-13), Day(1)), (q.FromMs, q.ToMs));
        Assert.Equal("in the last 2 weeks (10–23 November)", q.When);

        q = Parse("What git repos did I create in the last month?", Now);
        Assert.Equal(Intent.List, q.Intent);
        Assert.Equal("repo_init", q.Topic?.Name);
        Assert.Empty(q.Terms);
        Assert.Equal(Dashboard.DayStart(new DateOnly(2026, 10, 24)), q.FromMs);
    }

    [Theory]
    [InlineData("what did I do today", "2026-11-23", "2026-11-23")]
    [InlineData("what did I do on friday", "2026-11-20", "2026-11-20")]
    [InlineData("what did I do on monday", "2026-11-23", "2026-11-23")]
    [InlineData("what did I do last monday", "2026-11-16", "2026-11-16")]
    [InlineData("what did I do the day before yesterday", "2026-11-21", "2026-11-21")]
    [InlineData("what did I do on 2026-11-02", "2026-11-02", "2026-11-02")]
    [InlineData("what did I do on 20 November", "2026-11-20", "2026-11-20")]
    [InlineData("what did I do on November 3rd", "2026-11-03", "2026-11-03")]
    [InlineData("what did I do on 24 December", "2025-12-24", "2025-12-24")]
    [InlineData("what did I do last week", "2026-11-16", "2026-11-22")]
    [InlineData("what did I do the last week", "2026-11-17", "2026-11-23")]
    [InlineData("what did I do this week", "2026-11-23", "2026-11-23")]
    [InlineData("what did I do last month", "2026-10-01", "2026-10-31")]
    [InlineData("what did I do this month", "2026-11-01", "2026-11-23")]
    [InlineData("what did I do in the past 3 days", "2026-11-21", "2026-11-23")]
    [InlineData("what did I do 2 days ago", "2026-11-21", "2026-11-21")]
    [InlineData("what did I do a week ago", "2026-11-16", "2026-11-22")]
    [InlineData("what did I do in October", "2026-10-01", "2026-10-31")]
    [InlineData("what did I do in december", "2025-12-01", "2025-12-31")]
    [InlineData("what did I do since friday", "2026-11-20", "2026-11-23")]
    [InlineData("what did I do between 2026-11-20 and 2026-11-18", "2026-11-18", "2026-11-20")]
    public void Days_are_read(string question, string from, string to)
    {
        var q = Parse(question, Now);
        Assert.Equal(Dashboard.DayStart(DateOnly.Parse(from)), q.FromMs);
        Assert.Equal(Dashboard.DayStart(DateOnly.Parse(to).AddDays(1)), q.ToMs);
    }

    [Theory]
    [InlineData("what did I do yesterday from 9:30 to 11", 9 * 60 + 30, 11 * 60)]
    [InlineData("what did I do yesterday 14-15", 14 * 60, 15 * 60)]
    [InlineData("what did I do yesterday between 2 and 4pm", 14 * 60, 16 * 60)]
    [InlineData("what did I do yesterday after 18", 18 * 60, 24 * 60)]
    [InlineData("what did I do yesterday before 10:15", 0, 10 * 60 + 15)]
    [InlineData("what did I do yesterday at 14", 14 * 60, 15 * 60)]
    [InlineData("what did I do yesterday around 14", 13 * 60 + 30, 14 * 60 + 30)]
    [InlineData("what did I do yesterday morning", 6 * 60, 12 * 60)]
    [InlineData("what did I do yesterday in the afternoon", 12 * 60, 18 * 60)]
    [InlineData("what did I do last night", 18 * 60, 24 * 60)]
    public void Times_of_day_narrow_one_day(string question, int fromMinute, int toMinute)
    {
        var q = Parse(question, Now);
        Assert.Equal(Day(-1) + fromMinute * Min, q.FromMs);
        Assert.Equal(Day(-1) + toMinute * Min, q.ToMs);
        Assert.Null(q.DailyMinutes);
    }

    [Fact]
    public void A_time_of_day_over_several_days_applies_to_each_day()
    {
        var q = Parse("did I listen to music in the mornings last week", Now);
        Assert.Equal((Day(-7), Day(0)), (q.FromMs, q.ToMs));
        Assert.Equal((6 * 60, 12 * 60), q.DailyMinutes);
    }

    [Fact]
    public void A_time_alone_means_today_and_hours_count_back_from_now()
    {
        var q = Parse("what did I do between 9 and 10", Now);
        Assert.Equal((At(0, 9), At(0, 10)), (q.FromMs, q.ToMs));

        q = Parse("what did I listen to in the last 2 hours", Now);
        Assert.Equal(new DateTimeOffset(Now.AddHours(-2)).ToUnixTimeMilliseconds(), q.FromMs);
        Assert.Null(q.ToMs);
    }

    [Fact]
    public void No_time_means_all_of_history_except_for_a_summary()
    {
        var q = Parse("Did I listen to Tiësto?", Now);
        Assert.Null(q.FromMs);
        Assert.Null(q.ToMs);
        Assert.Equal("in all of your history", q.When);

        Assert.Equal((Day(0), Day(1)), (Parse("What did I do?", Now).FromMs, Parse("What did I do?", Now).ToMs));
    }

    [Theory]
    [InlineData("Did I listen to Moby?", Intent.Exists)]
    [InlineData("Have I visited github today?", Intent.Exists)]
    [InlineData("How many songs did I play yesterday?", Intent.Count)]
    [InlineData("How often did I commit this week?", Intent.Count)]
    [InlineData("When did I last listen to a-ha?", Intent.When)]
    [InlineData("How long was I in Photoshop today?", Intent.HowLong)]
    [InlineData("How much time did I spend in T3 Code?", Intent.HowLong)]
    [InlineData("Which sites did I visit yesterday?", Intent.List)]
    [InlineData("What did I work on yesterday?", Intent.Overview)]
    [InlineData("Summarize last week", Intent.Overview)]
    [InlineData("yesterday", Intent.Overview)]
    public void The_shape_of_the_answer_follows_the_first_words(string question, Intent intent) =>
        Assert.Equal(intent, Parse(question, Now).Intent);

    [Theory]
    [InlineData("How many commands did I run yesterday?", "command", new string[0])]
    [InlineData("Did I listen to music by Darude this week?", "listen", new[] { "Darude" })]
    [InlineData("What did I search for on Rate Your Music?", "search", new[] { "Rate Your Music" })]
    [InlineData("Did I listen to Music Instructor?", "listen", new[] { "Music Instructor" })]
    [InlineData("Which pages did I visit on rateyourmusic?", "visit", new[] { "rateyourmusic" })]
    [InlineData("When did I last commit to watchme?", "commit", new[] { "watchme" })]
    [InlineData("What did I ask Claude about the dashboard?", "run", new[] { "dashboard" })]
    [InlineData("Which albums did I add this week?", "album_added", new string[0])]
    [InlineData("Did I rate anything by Moby?", "rating", new[] { "Moby" })]
    [InlineData("What did I copy to d:\\backup today?", "file_op", new[] { "Copied", "d:\\backup" })]
    [InlineData("What did I download?", "download", new string[0])]
    [InlineData("Did I clone any repos?", "clone", new string[0])]
    public void Topics_and_words_are_found(string question, string topic, string[] terms)
    {
        var q = Parse(question, Now);
        Assert.Equal(topic, q.Topic?.Name);
        Assert.Equal(terms, q.Terms);
    }

    [Fact]
    public void Quoted_phrases_are_searched_for_as_they_are()
    {
        // "Take On Me" would otherwise lose "on" and "me"; "last" would be read as time.
        var q = Parse("Did I listen to \"Take On Me\" or “The Last Song”?", Now);
        Assert.Equal(["Take On Me", "The Last Song"], q.Terms);
        Assert.Null(q.FromMs);
    }

    [Fact]
    public void How_long_keeps_app_names_as_words()
    {
        var q = Parse("How long was I in T3 Code today?", Now);
        Assert.Null(q.Topic);
        Assert.Equal(["T3 Code"], q.Terms);

        // Listening has a time of its own.
        Assert.Equal("listen", Parse("How long did I listen to Moby?", Now).Topic?.Name);
    }

    [Fact]
    public void When_first_asks_for_the_oldest()
    {
        Assert.True(Parse("When did I first listen to a-ha?", Now).First);
        var q = Parse("When did I last listen to a-ha last week?", Now);
        Assert.False(q.First);
        Assert.Equal(["a-ha"], q.Terms);
        Assert.Equal(Day(-7), q.FromMs);
    }

    // ---------- answering ----------

    private static Database Seeded()
    {
        var db = Database.Open(":memory:");
        db.Insert([
            new(At(-1, 14, 0), EventSources.Focus, "foreground") { App = "Code.exe", Title = "Ask.cs - watchme" },
            new(At(-1, 14, 40), EventSources.Focus, "foreground") { App = "Photoshop.exe", Title = "cover.psd" },
            new(At(-1, 15, 10), EventSources.Idle, "idle_start"),
            new(At(-1, 14, 20), EventSources.Git, "commit") { ExtId = "c1", App = "git", Title = "Committed to watchme (master): Add ask", Data = """{"repo":"watchme"}""" },
            new(At(-3, 10), EventSources.Git, "repo_init") { ExtId = "r1", App = "git", Title = "Started a git repo in C:\\_code\\watchme", Data = """{"repo":"watchme"}""" },
            new(At(-40, 10), EventSources.Git, "repo_init") { ExtId = "r2", App = "git", Title = "Started a git repo in C:\\_code\\old", Data = """{"repo":"old"}""" },
            new(At(-2, 20), EventSources.AuroraNg, "listen") { ExtId = "l1", Title = "a-ha – Take On Me", Data = """{"artist":"a-ha","album":"Hunting High and Low","listened_s":225.5,"play":true}""" },
            new(At(-2, 20, 4), EventSources.AuroraNg, "listen") { ExtId = "l2", Title = "a-ha – The Sun Always Shines on T.V.", Data = """{"artist":"a-ha","album":"Hunting High and Low","listened_s":300,"play":true}""" },
            new(At(-30, 20), EventSources.Aurora, "listen") { ExtId = "l3", Title = "a-ha – Hunting High and Low", Data = """{"artist":"a-ha","album":"Hunting High and Low","listened_s":200,"play":true}""" },
            new(At(-1, 9), EventSources.Edge, "visit") { ExtId = "v1", Title = "Google", Data = """{"host":"www.google.com","search":"a-ha tour"}""" },
        ]);
        return db;
    }

    [Fact]
    public void Did_I_answers_yes_with_the_matches_or_no()
    {
        using var db = Seeded();

        var yes = Ask.Run(db, "Did I listen to a-ha in the last 2 weeks?", Now);
        Assert.StartsWith("Yes: 2 listens (8m 45s listened) matching “a-ha” in the last 2 weeks", yes.Headline);
        Assert.Contains("a-ha – The Sun Always Shines on T.V.", yes.Headline); // the newest
        Assert.Equal(2, yes.Sections.Single(s => s.Title == "Newest first").Rows.Count);
        // The Google search is about a-ha too, but it isn't a listen.
        Assert.DoesNotContain(yes.Sections.SelectMany(s => s.Rows), r => r.Label == "Edge");

        var no = Ask.Run(db, "Did I listen to Moby?", Now);
        Assert.StartsWith("No: no listens matching “Moby” in all of your history.", no.Headline);
        Assert.Empty(no.Sections);
    }

    [Fact]
    public void Lists_and_counts_keep_to_the_time_asked_about()
    {
        using var db = Seeded();
        var repos = Ask.Run(db, "What git repos did I create in the last month?", Now);
        Assert.StartsWith("1 new git repo in the last month", repos.Headline);
        Assert.Equal("Started a git repo in C:\\_code\\watchme", Assert.Single(repos.Sections.Single().Rows).Text);

        Assert.StartsWith("3 listens", Ask.Run(db, "How many songs did I play?", Now).Headline);
    }

    [Fact]
    public void Searches_are_only_pages_with_search_words()
    {
        using var db = Seeded();
        var a = Ask.Run(db, "What did I search for yesterday?", Now);
        Assert.StartsWith("1 search yesterday", a.Headline);
    }

    [Fact]
    public void How_long_adds_up_focus_time_or_listening_time()
    {
        using var db = Seeded();
        var photoshop = Ask.Run(db, "How long was I in Photoshop yesterday?", Now);
        Assert.Equal("In front matching “Photoshop” for 30m 00s yesterday.", photoshop.Headline);

        var all = Ask.Run(db, "How long was I active yesterday?", Now);
        Assert.StartsWith("Active for 1h 10m yesterday", all.Headline);

        var music = Ask.Run(db, "How long did I listen to a-ha?", Now);
        Assert.StartsWith("12m 05s of listening matching “a-ha” in all of your history: 3 tracks.", music.Headline);
    }

    [Fact]
    public void What_did_I_do_sums_up_the_stretch_of_time()
    {
        using var db = Seeded();
        var a = Ask.Run(db, "What did I do yesterday between 14 and 15?", Now);
        Assert.Equal("overview", a.Mode);
        Assert.StartsWith("Active for 1h 00m yesterday 14:00–15:00, mostly in Code, Photoshop; 1 thing done in apps.", a.Headline);
        Assert.Equal(["Code", "Photoshop"], a.Sections.Single(s => s.Title == "Apps").Rows.Select(r => r.Text));
        Assert.Equal(2, a.Sections.Single(s => s.Title == "Timeline").Rows.Count);
        var commit = Assert.Single(a.Sections.Single(s => s.Title == "Activity in apps").Rows);
        Assert.Equal(("14:20", "Git"), (commit.When, commit.Label));
        Assert.Equal(At(-1, 14, 20), commit.Ts);
        // The visit at 09:00 is outside the hour.
        Assert.DoesNotContain(a.Sections.SelectMany(s => s.Rows), r => r.Text.Contains("Google"));
    }

    [Fact]
    public void Printed_answers_say_how_the_question_was_read()
    {
        using var db = Seeded();
        var w = new StringWriter();
        Ask.Print(Ask.Run(db, "Did I listen to a-ha in the last 2 weeks?", Now), w, color: false);
        var text = w.ToString();
        Assert.Contains("Looked for listens in Aurora and Aurora NG matching “a-ha”, in the last 2 weeks (10–23 November).", text);
        Assert.Contains("a-ha – Take On Me", text);
    }
}
