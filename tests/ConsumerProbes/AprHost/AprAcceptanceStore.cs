using System.Text.Json;

namespace AprHost;

// Trusted synthetic Host storage, deliberately separate from restricted Runtime envelopes and safe receipts.
internal sealed class AprAcceptanceStore
{
    internal sealed record Entry(Guid ExecutionId, Guid SubmissionId, Guid? RepairsSubmissionId, string ItemId, long Value, long Sequence = 0);
    private readonly string directory;
    internal AprAcceptanceStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
    }
    internal IEnumerable<Entry> ReadAccepted()
    {
        var entries = new List<Entry>();
        foreach (var path in Directory.GetFiles(directory, "accepted-*.json"))
        {
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(path))
                ?? throw new InvalidOperationException("Missing Host acceptance.");
            if (Path.GetFileName(path) != $"accepted-{entry.SubmissionId:D}.json"
                || entry.ExecutionId == Guid.Empty || entry.SubmissionId == Guid.Empty)
                throw new InvalidOperationException("Invalid Host acceptance association.");
            entries.Add(entry);
        }
        if (entries.Any(e => e.Sequence <= 0) || entries.Select(e => e.Sequence).Distinct().Count() != entries.Count)
            throw new InvalidOperationException("Invalid Host acceptance order.");
        return entries.OrderBy(e => e.Sequence).ToArray();
    }
    internal IEnumerable<Guid> ReadApplied()
    {
        foreach (var path in Directory.GetFiles(directory, "effect-*.json"))
        {
            var id = JsonSerializer.Deserialize<Guid>(File.ReadAllBytes(path));
            if (id == Guid.Empty || Path.GetFileName(path) != $"effect-{id:D}.json")
                throw new InvalidOperationException("Invalid Host effect association.");
            yield return id;
        }
    }
    internal void RecordAccepted(Entry entry)
    {
        // The synthetic store serializes acknowledgement writes; a competing writer fails closed rather than inventing order.
        using var writer = new FileStream(Path.Combine(directory, "acceptance-order.lock"), FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.None);
        var entries = ReadAccepted().ToArray();
        var prior = entries.SingleOrDefault(e => e.SubmissionId == entry.SubmissionId);
        if (prior is not null)
        {
            if (!SameFacts(prior, entry)) throw new InvalidOperationException("A committed Host submission cannot change.");
            return;
        }
        entry = entry with { Sequence = entries.Length == 0 ? 1 : checked(entries[^1].Sequence + 1) };
        var path = Path.Combine(directory, $"accepted-{entry.SubmissionId:D}.json");
        if (!WriteOnce(path, JsonSerializer.SerializeToUtf8Bytes(entry))
            && !SameFacts(JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(path)), entry))
            throw new InvalidOperationException("A committed Host submission cannot change.");
    }
    private static bool SameFacts(Entry? left, Entry right) => left is not null && left with { Sequence = 0 } == right with { Sequence = 0 };
    internal bool Apply(Guid id)
    {
        var path = Path.Combine(directory, $"effect-{id:D}.json");
        if (WriteOnce(path, JsonSerializer.SerializeToUtf8Bytes(id))) return true;
        if (JsonSerializer.Deserialize<Guid>(File.ReadAllBytes(path)) != id)
            throw new InvalidOperationException("A committed Host effect cannot change.");
        return false;
    }
    private static bool WriteOnce(string path, byte[] bytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
            stream.Flush(true);
            return true;
        }
        catch (IOException) when (File.Exists(path)) { return false; }
    }
}
