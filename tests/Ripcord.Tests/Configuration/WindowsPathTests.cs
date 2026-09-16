using Ripcord.Domain;

namespace Ripcord.Tests.Configuration;

/// Windows paths, split by the Domain rather than by `System.IO.Path`.
///
/// The reason is the whole point of the type: these tests run on Linux, where a backslash is
/// an ordinary character and `Path.GetDirectoryName(@"D:\Ripcord\state.json")` answers that
/// there is no folder at all. What it feeds is a quoted `icacls` argument and the folder the
/// service account is granted access to, so a wrong answer here is a permission granted
/// somewhere nobody meant.
public class WindowsPathTests
{
    [Theory]
    [InlineData(@"D:\Ripcord\state.json", @"D:\Ripcord")]
    [InlineData(@"C:\Program Files\Ripcord\state.json", @"C:\Program Files\Ripcord")]
    [InlineData(@"\\server\share\state.json", @"\\server\share")]
    [InlineData(@"D:\Ripcord\", @"D:\Ripcord")]
    public void The_folder_is_what_comes_before_the_last_separator(string path, string folder) =>
        Assert.Equal(folder, WindowsPath.FolderOf(path));

    /// `D:\` is the root of that volume; `D:` is the *current directory* on it, which is
    /// somewhere else entirely and depends on who is asking.
    [Theory]
    [InlineData(@"D:\state.json", @"D:\")]
    [InlineData(@"\state.json", @"\")]
    public void A_root_keeps_its_separator(string path, string folder) =>
        Assert.Equal(folder, WindowsPath.FolderOf(path));

    /// No folder is answered as no folder. The caller decides what that means — the validator
    /// refuses such a configuration, and the deployment never invents one.
    [Theory]
    [InlineData("state.json")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void A_path_with_no_separator_has_no_folder(string? path) =>
        Assert.Equal("", WindowsPath.FolderOf(path));

    /// A drive-relative path — `D:state.json` means "in the current directory on D:". There is
    /// no folder in it, and the drive letter is not one.
    [Fact]
    public void A_drive_relative_path_has_no_folder() =>
        Assert.Equal("", WindowsPath.FolderOf("D:state.json"));

    [Theory]
    [InlineData(@"D:\Ripcord", "state.json", @"D:\Ripcord\state.json")]
    [InlineData(@"D:\", "state.json", @"D:\state.json")]
    [InlineData("", "state.json", "state.json")]
    [InlineData(null, "state.json", "state.json")]
    public void Joining_adds_one_separator_and_no_more(
        string? folder, string name, string expected) =>
        Assert.Equal(expected, WindowsPath.Join(folder, name));

    /// The pair the configuration default is built from: the snapshot lands beside the file
    /// that named it.
    [Fact]
    public void A_folder_taken_apart_and_put_back_names_the_same_place() =>
        Assert.Equal(
            @"C:\Program Files\Ripcord\state.json",
            WindowsPath.Join(
                WindowsPath.FolderOf(@"C:\Program Files\Ripcord\ripcord.yaml"), "state.json"));
}
