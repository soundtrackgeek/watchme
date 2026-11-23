using Watchme.Cli;

namespace Watchme.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "watchme-update-" + Guid.NewGuid().ToString("N"));
    private string From => Path.Combine(_root, "new");
    private string To => Path.Combine(_root, "installed");

    public UpdateTests()
    {
        Directory.CreateDirectory(From);
        Directory.CreateDirectory(To);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Replace_copies_new_and_changed_files_including_subfolders()
    {
        File.WriteAllText(Path.Combine(From, "a.dll"), "new a");
        Directory.CreateDirectory(Path.Combine(From, "runtimes"));
        File.WriteAllText(Path.Combine(From, "runtimes", "b.dll"), "new b");
        File.WriteAllText(Path.Combine(To, "a.dll"), "old a");
        File.WriteAllText(Path.Combine(To, "keep.txt"), "mine");

        Assert.Equal(2, UpdateCommand.Replace(From, To));

        Assert.Equal("new a", File.ReadAllText(Path.Combine(To, "a.dll")));
        Assert.Equal("new b", File.ReadAllText(Path.Combine(To, "runtimes", "b.dll")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(To, "keep.txt")));
    }

    [Fact]
    public void A_file_in_use_is_moved_aside_and_cleaned_up_later()
    {
        File.WriteAllText(Path.Combine(From, "watchme.exe"), "new");
        var target = Path.Combine(To, "watchme.exe");
        File.WriteAllText(target, "old");

        // Like a running .exe: can't be written, but can be renamed.
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            UpdateCommand.Replace(From, To);

            Assert.Equal("new", File.ReadAllText(target));
            Assert.True(File.Exists(target + ".old"));
        }

        UpdateCommand.RemoveLeftovers(To);
        Assert.False(File.Exists(target + ".old"));
    }

    [Fact]
    public void A_second_update_works_while_the_first_ones_leftover_is_still_in_use()
    {
        var target = Path.Combine(To, "watchme.dll");
        File.WriteAllText(Path.Combine(From, "watchme.dll"), "v3");
        File.WriteAllText(target, "v2");
        File.WriteAllText(target + ".old", "v1");

        // v1 is held without delete sharing, v2 like a loaded dll: renamable, not writable.
        using var v1 = new FileStream(target + ".old", FileMode.Open, FileAccess.Read, FileShare.Read);
        using var v2 = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        UpdateCommand.Replace(From, To);

        Assert.Equal("v3", File.ReadAllText(target));
        Assert.Equal(2, Directory.GetFiles(To, "*.old").Length);
    }

    [Theory]
    [InlineData(@"C:\_code\watchme\src\Watchme.Cli\bin\Debug\net10.0-windows", true)]
    [InlineData(@"C:\_code\watchme\src\Watchme.Cli\bin\Release\net10.0-windows\publish", true)]
    [InlineData(@"C:\Users\me\AppData\Local\Watchme\bin", false)]
    public void Build_folders_are_recognised(string dir, bool expected) =>
        Assert.Equal(expected, UpdateCommand.IsBuildFolder(dir));
}
