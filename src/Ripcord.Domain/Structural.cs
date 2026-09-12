namespace Ripcord.Domain;

/// A record holding a list compares that list by reference, so two identical values are
/// unequal. Every record here that carries a list uses these two helpers instead.
internal static class Structural
{
    public static bool Same<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right) =>
        left is null || right is null ? ReferenceEquals(left, right) : left.SequenceEqual(right);

    public static void Add<T>(ref HashCode hash, IReadOnlyList<T>? items)
    {
        if (items is null)
        {
            hash.Add(0);
            return;
        }

        foreach (T item in items)
        {
            hash.Add(item);
        }
    }
}
