using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Sempulse.Services;

[StructLayout(LayoutKind.Sequential)]
public struct IconPoint(int x, int y) : IEquatable<IconPoint>
{
    public int X = x;
    public int Y = y;
    public readonly bool Equals(IconPoint other) => X == other.X && Y == other.Y;
    public override readonly bool Equals(object? obj) => obj is IconPoint other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y);
    public override readonly string ToString() => $"{X},{Y}";
}

public readonly record struct IconRect(int Left, int Top, int Right, int Bottom)
{
    public bool Contains(IconPoint point) => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
}

// Grid maths and the undo snapshot for moving desktop icons. No Explorer access, so it is unit tested.
public static class DesktopIconLayout
{
    const string SnapshotHeader = "SempulseDesktopIconsV1";

    public static bool Overlaps(IconPoint a, IconPoint b, IconPoint spacing) =>
        Math.Abs(a.X - b.X) < spacing.X && Math.Abs(a.Y - b.Y) < spacing.Y;

    public static int Mod(int value, int step)
    {
        int remainder = value % step;
        return remainder < 0 ? remainder + step : remainder;
    }

    // Smallest value >= value that lands on the grid defined by residue.
    public static int AlignUp(int value, int step, int residue) => value + Mod(residue - value, step);

    static bool IsFree(IconPoint slot, List<IconPoint> occupied, IconPoint spacing) =>
        !occupied.Any(existing => Overlaps(slot, existing, spacing));

    // Free grid slots inside the area (view coordinates), column by column, aligned with an existing icon.
    public static List<IconPoint> PlanDestinations(int needed, IconPoint topLeft, IconPoint bottomRight, IconPoint spacing,
        IconPoint sample, List<IconPoint> occupied)
    {
        var taken = new List<IconPoint>(occupied);
        var destinations = new List<IconPoint>();
        int firstX = AlignUp(topLeft.X, spacing.X, Mod(sample.X, spacing.X));
        int firstY = AlignUp(topLeft.Y, spacing.Y, Mod(sample.Y, spacing.Y));
        for (int x = firstX; x <= bottomRight.X && destinations.Count < needed; x += spacing.X)
        {
            for (int y = firstY; y <= bottomRight.Y && destinations.Count < needed; y += spacing.Y)
            {
                var slot = new IconPoint(x, y);
                if (!IsFree(slot, taken, spacing)) continue;
                destinations.Add(slot);
                taken.Add(slot);
            }
        }
        return destinations;
    }

    // The saved position when it is free, otherwise the nearest free grid slot in the work area.
    public static IconPoint? FindRestoreSlot(IconPoint preferred, IconRect work, IconPoint spacing, List<IconPoint> occupied)
    {
        if (work.Contains(preferred) && IsFree(preferred, occupied, spacing)) return preferred;
        IconPoint? best = null;
        long bestDistance = long.MaxValue;
        int firstX = AlignUp(work.Left, spacing.X, Mod(preferred.X, spacing.X));
        int firstY = AlignUp(work.Top, spacing.Y, Mod(preferred.Y, spacing.Y));
        for (int x = firstX; x <= work.Right - spacing.X; x += spacing.X)
        {
            for (int y = firstY; y <= work.Bottom - spacing.Y; y += spacing.Y)
            {
                var slot = new IconPoint(x, y);
                if (!IsFree(slot, occupied, spacing)) continue;
                long dx = x - preferred.X, dy = y - preferred.Y;
                long distance = dx * dx + dy * dy;
                if (distance < bestDistance) { best = slot; bestDistance = distance; }
            }
        }
        return best;
    }

    // ---- Snapshot: original screen positions keyed by file path, in the order they were moved ----

    public sealed class Snapshot
    {
        public Dictionary<string, IconPoint> Positions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Order { get; } = [];
        public int Count => Order.Count;

        public void Add(string path, IconPoint position)
        {
            if (Positions.ContainsKey(path)) return;
            Positions[path] = position;
            Order.Add(path);
        }

        public Snapshot Clone()
        {
            var copy = new Snapshot();
            foreach (string path in Order) copy.Add(path, Positions[path]);
            return copy;
        }
    }

    public static Snapshot ReadSnapshot(string file)
    {
        var snapshot = new Snapshot();
        if (!File.Exists(file)) return snapshot;
        string[] lines = File.ReadAllLines(file, Encoding.UTF8);
        if (lines.Length == 0 || lines[0] != SnapshotHeader) throw new DesktopIconException("SNAPSHOT_INVALID");
        foreach (string line in lines.Skip(1))
        {
            string[] parts = line.Split('\t');
            if (parts.Length != 3) throw new DesktopIconException("SNAPSHOT_INVALID");
            string path;
            try { path = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])); }
            catch (FormatException) { throw new DesktopIconException("SNAPSHOT_INVALID"); }
            if (path.Length == 0 || !int.TryParse(parts[1], out int x) || !int.TryParse(parts[2], out int y) ||
                snapshot.Positions.ContainsKey(path)) throw new DesktopIconException("SNAPSHOT_INVALID");
            snapshot.Add(path, new IconPoint(x, y));
        }
        return snapshot;
    }

    public static void WriteSnapshot(string file, Snapshot snapshot)
    {
        if (snapshot.Count == 0)
        {
            File.Delete(file);
            return;
        }
        var lines = new List<string> { SnapshotHeader };
        foreach (string path in snapshot.Order)
        {
            var position = snapshot.Positions[path];
            lines.Add($"{Convert.ToBase64String(Encoding.UTF8.GetBytes(path))}\t{position.X}\t{position.Y}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temporary = file + ".tmp";
        File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
        File.Move(temporary, file, overwrite: true);
    }
}

public sealed class DesktopIconException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
