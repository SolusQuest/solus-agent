using System.Text.Json;

namespace AprHost;

// Trusted synthetic Host storage, deliberately separate from restricted Runtime envelopes and safe receipts.
internal sealed class AprAcceptanceStore
{
    internal sealed record Entry(Guid ExecutionId, Guid SubmissionId, Guid? RepairsSubmissionId, string ItemId, long Value);
    private readonly string directory;
    internal AprAcceptanceStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
    }
    internal IEnumerable<Entry> ReadAccepted()
    {
        foreach (var path in Directory.GetFiles(directory, "accepted-*.json"))
        {
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(path))
                ?? throw new InvalidOperationException("Missing Host acceptance.");
            if (Path.GetFileName(path) != $"accepted-{entry.SubmissionId:D}.json"
                || entry.ExecutionId == Guid.Empty || entry.SubmissionId == Guid.Empty)
                throw new InvalidOperationException("Invalid Host acceptance association.");
            yield return entry;
        }
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
        var path = Path.Combine(directory, $"accepted-{entry.SubmissionId:D}.json");
        if (!WriteOnce(path, JsonSerializer.SerializeToUtf8Bytes(entry))
            && JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(path)) != entry)
            throw new InvalidOperationException("A committed Host submission cannot change.");
    }
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
