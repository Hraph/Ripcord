namespace Ripcord.Domain;

/// The two path operations the Domain needs, on Windows paths, decided here rather than by
/// `System.IO.Path`: the tests run on Linux, where a backslash is an ordinary character and
/// `GetDirectoryName` would answer that `D:\Ripcord\state.json` has no folder at all.
public static class WindowsPath
{
    private static readonly char[] Separators = ['\\', '/'];

    /// The folder a path names, or the empty string when the path is a bare file name. A root
    /// keeps its separator: the folder of `D:\state.json` is `D:\`, not `D:`, which names the
    /// current directory on that volume instead.
    public static string FolderOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }

        int at = path.LastIndexOfAny(Separators);

        if (at < 0)
        {
            return "";
        }

        return at == 0 || path[at - 1] == ':' ? path[..(at + 1)] : path[..at];
    }

    public static string Join(string? folder, string name)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return name;
        }

        return Separators.Contains(folder[^1]) ? folder + name : folder + '\\' + name;
    }
}
