using Watchme.Core;

namespace Watchme.Cli;

/// <summary><c>watchme ask "What did I do yesterday between 14 and 15?"</c></summary>
internal static class AskCommand
{
    public const string Usage = """
        Usage: watchme ask "QUESTION"
          watchme ask "What did I do yesterday between 14 and 15?"
          watchme ask "Did I listen to a-ha in the last 2 weeks?"
          watchme ask "What git repos did I create in the last month?"
          watchme ask "How long was I in Photoshop this week?"
          watchme ask "When did I last search for flac?"
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 1 && args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        // Quotes are optional: watchme ask did I listen to a-ha today
        var question = string.Join(" ", args).Trim();
        if (question.Length == 0)
        {
            Console.Error.WriteLine("What do you want to know?");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (!File.Exists(Paths.Database))
        {
            Console.WriteLine($"No data yet ({Paths.Database} does not exist). Run 'watchme start' first.");
            return 0;
        }

        using var db = Database.Open(Paths.Database);
        var answer = Ask.Run(db, question, DateTime.Now);
        var color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        Ask.Print(answer, Console.Out, color);
        return 0;
    }
}
