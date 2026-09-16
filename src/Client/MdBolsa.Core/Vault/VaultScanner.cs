using System.Security.Cryptography;
using System.Text;

namespace MdBolsa.Core.Vault;

public sealed class VaultScanner(string vaultRoot, IVaultIndex index)
{
    public VaultScanResult Scan()
    {
        var existingById = index.GetAll().ToDictionary(n => n.Id);
        var seenIds = new List<Guid>();
        int added = 0, updated = 0, moved = 0, unchanged = 0;

        foreach (var filePath in Directory.EnumerateFiles(vaultRoot, "*.md", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(vaultRoot, filePath).Replace('\\', '/');
            var content = File.ReadAllText(filePath);

            var id = FrontMatter.TryReadId(content);
            if (id is null)
            {
                id = Guid.NewGuid();
                content = FrontMatter.EnsureId(content, id.Value);
                File.WriteAllText(filePath, content);
            }

            var hash = ComputeHash(content);
            var title = Path.GetFileNameWithoutExtension(filePath);

            existingById.TryGetValue(id.Value, out var previous);
            var revision = previous is null
                ? 1
                : previous.ContentHash == hash ? previous.Revision : previous.Revision + 1;
            var createdAt = previous?.CreatedAt ?? new DateTimeOffset(File.GetCreationTimeUtc(filePath), TimeSpan.Zero);
            var updatedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(filePath), TimeSpan.Zero);

            index.Upsert(new NoteMetadata(id.Value, relativePath, title, hash, revision, createdAt, updatedAt));
            seenIds.Add(id.Value);

            if (previous is null) added++;
            else if (previous.RelativePath != relativePath) moved++;
            else if (previous.ContentHash != hash) updated++;
            else unchanged++;
        }

        var deleted = index.DeleteMissing(seenIds);
        return new VaultScanResult(added, updated, moved, deleted, unchanged);
    }

    private static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}

public readonly record struct VaultScanResult(int Added, int Updated, int Moved, int Deleted, int Unchanged);
