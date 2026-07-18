namespace Tomix.Core.Update;

/// <summary>
/// A parsed SemVer 2.0 version, just enough for update comparisons. Hand-rolled so Core
/// stays dependency-light (no NuGet.Versioning). Accepts a leading <c>v</c> (release tags)
/// and ignores <c>+build</c> metadata, matching what MinVer stamps on the assembly.
/// </summary>
public sealed record CliVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<CliVersion>
{
    public static bool TryParse(string? value, out CliVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        var plus = text.IndexOf('+');
        if (plus >= 0)
            text = text[..plus];

        string? prerelease = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];
            if (prerelease.Length == 0)
                return false;
        }

        var parts = text.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var patch)
            || major < 0 || minor < 0 || patch < 0)
        {
            return false;
        }

        version = new CliVersion(major, minor, patch, prerelease);
        return true;
    }

    public bool IsNewerThan(CliVersion other) => CompareTo(other) > 0;

    public static bool operator <(CliVersion? left, CliVersion? right)
        => left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(CliVersion? left, CliVersion? right)
        => left is null || left.CompareTo(right) <= 0;

    public static bool operator >(CliVersion? left, CliVersion? right)
        => left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(CliVersion? left, CliVersion? right)
        => left is null ? right is null : left.CompareTo(right) >= 0;

    public int CompareTo(CliVersion? other)
    {
        if (other is null)
            return 1;

        var core = Major.CompareTo(other.Major);
        if (core != 0)
            return core;
        core = Minor.CompareTo(other.Minor);
        if (core != 0)
            return core;
        core = Patch.CompareTo(other.Patch);
        if (core != 0)
            return core;

        // A release outranks any prerelease of the same core version.
        if (Prerelease is null)
            return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null)
            return -1;

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    private static int ComparePrerelease(string left, string right)
    {
        var leftIds = left.Split('.');
        var rightIds = right.Split('.');

        for (var i = 0; i < Math.Min(leftIds.Length, rightIds.Length); i++)
        {
            var l = leftIds[i];
            var r = rightIds[i];
            var lNumeric = long.TryParse(l, out var lValue);
            var rNumeric = long.TryParse(r, out var rValue);

            int cmp;
            if (lNumeric && rNumeric)
                cmp = lValue.CompareTo(rValue);
            else if (lNumeric != rNumeric)
                cmp = lNumeric ? -1 : 1; // numeric identifiers sort below alphanumeric
            else
                cmp = string.CompareOrdinal(l, r);

            if (cmp != 0)
                return cmp;
        }

        // All shared identifiers equal: the longer identifier list is the higher version.
        return leftIds.Length.CompareTo(rightIds.Length);
    }

    public override string ToString()
        => Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
