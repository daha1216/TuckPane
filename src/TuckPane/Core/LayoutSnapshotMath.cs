using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane.Core;

public sealed class LayoutSnapshotStoreData
{
    public int Version { get; set; } = 1;
    public List<LayoutSnapshot> Snapshots { get; set; } = [];
}

public sealed class LayoutSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string TopologyKey { get; set; } = string.Empty;
    public string TopologySummary { get; set; } = string.Empty;
    public bool IsAutoTopology { get; set; }
    public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<OrganizerLayoutEntry> Entries { get; set; } = [];
}

public sealed class OrganizerLayoutEntry
{
    public Guid OrganizerId { get; set; }
    public OrganizerPlacementMode PlacementMode { get; set; }
    public OrganizerDockEdge DockEdge { get; set; }
    public WidgetPosition? Position { get; set; }
    public WidgetPosition? ExpandedPosition { get; set; }
    public WidgetPosition? DockCenter { get; set; }
}

/// <summary>
/// Pure monitor topology fingerprinting and layout snapshot capture/restore math.
/// </summary>
internal static class LayoutSnapshotMath
{
    internal const int MaximumAutoSnapshots = 16;
    internal const int MaximumNamedSnapshots = 32;

    internal static (string Key, string Summary) BuildTopologyFingerprint(IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0) return ("empty", "0 displays");

        var normalized = displays
            .Select(display =>
            {
                double scale = Math.Max(1.0, display.Scale);
                int workW = (int)Math.Round(display.Work.Width / scale);
                int workH = (int)Math.Round(display.Work.Height / scale);
                int dpiPercent = (int)Math.Round(scale * 100);
                string dev = (display.Device ?? string.Empty).Trim().ToUpperInvariant();
                return (Device: dev, WorkW: workW, WorkH: workH, Dpi: dpiPercent, Left: display.Monitor.Left, Top: display.Monitor.Top);
            })
            .OrderBy(d => d.Device, StringComparer.Ordinal)
            .ThenBy(d => d.Left)
            .ThenBy(d => d.Top)
            .ToArray();

        string canonical = string.Join("|", normalized.Select(d =>
            string.Create(CultureInfo.InvariantCulture, $"{d.Device}:{d.WorkW}x{d.WorkH}@{d.Dpi}")));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16].ToLowerInvariant();
        string summary = string.Join(" + ", normalized.Select(d =>
            string.Create(CultureInfo.InvariantCulture, $"{d.WorkW}×{d.WorkH} ({d.Dpi}%)")));
        return (hash, summary);
    }

    internal static LayoutSnapshot Capture(
        string name,
        bool isAutoTopology,
        IReadOnlyList<DisplayInfo> displays,
        IEnumerable<OrganizerDefinition> organizers)
    {
        (string key, string summary) = BuildTopologyFingerprint(displays);
        return new LayoutSnapshot
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? summary : name.Trim(),
            TopologyKey = key,
            TopologySummary = summary,
            IsAutoTopology = isAutoTopology,
            SavedAtUtc = DateTimeOffset.UtcNow,
            Entries = organizers.Select(organizer => new OrganizerLayoutEntry
            {
                OrganizerId = organizer.Id,
                PlacementMode = organizer.PlacementMode,
                DockEdge = organizer.DockEdge,
                Position = organizer.Position?.Clone(),
                ExpandedPosition = organizer.ExpandedPosition?.Clone(),
                DockCenter = organizer.DockCenter?.Clone()
            }).ToList()
        };
    }

    internal static int ApplySnapshot(LayoutSnapshot snapshot, IList<OrganizerDefinition> organizers)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(organizers);

        var byId = snapshot.Entries.ToDictionary(entry => entry.OrganizerId);
        int restoredCount = 0;
        foreach (OrganizerDefinition organizer in organizers)
        {
            if (!byId.TryGetValue(organizer.Id, out OrganizerLayoutEntry? entry)) continue;
            if (entry.Position is not null) organizer.Position = entry.Position.Clone();
            if (entry.ExpandedPosition is not null) organizer.ExpandedPosition = entry.ExpandedPosition.Clone();
            if (entry.DockCenter is not null) organizer.DockCenter = entry.DockCenter.Clone();
            restoredCount++;
        }
        return restoredCount;
    }

    internal static LayoutSnapshot? FindAutoSnapshot(LayoutSnapshotStoreData store, string topologyKey) =>
        store.Snapshots
            .Where(snapshot => snapshot.IsAutoTopology &&
                string.Equals(snapshot.TopologyKey, topologyKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(snapshot => snapshot.SavedAtUtc)
            .FirstOrDefault();

    internal static void UpsertSnapshot(LayoutSnapshotStoreData store, LayoutSnapshot snapshot)
    {
        if (snapshot.IsAutoTopology)
        {
            store.Snapshots.RemoveAll(existing =>
                existing.IsAutoTopology &&
                string.Equals(existing.TopologyKey, snapshot.TopologyKey, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            store.Snapshots.RemoveAll(existing => existing.Id == snapshot.Id);
        }

        store.Snapshots.Insert(0, snapshot);

        var auto = store.Snapshots.Where(s => s.IsAutoTopology).OrderByDescending(s => s.SavedAtUtc).Take(MaximumAutoSnapshots);
        var named = store.Snapshots.Where(s => !s.IsAutoTopology).OrderByDescending(s => s.SavedAtUtc).Take(MaximumNamedSnapshots);
        store.Snapshots = named.Concat(auto).OrderByDescending(s => s.SavedAtUtc).ToList();
    }
}
