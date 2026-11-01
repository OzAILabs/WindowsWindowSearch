using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using WindowSearcher.Core;

namespace WindowSearcher.Tests;

public sealed class EncryptedIndexTests : IDisposable
{
    private const string Key = "test-key";
    private const string Marker = "zebrafinch7741";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wws-enc-{Guid.NewGuid():N}.db");

    private static DocRecord Doc(string key, string body) =>
        new(key, DocKind.Tab, 1, 2, "msedge", "Title", "https://example.com/" + key, body, null, DateTimeOffset.UtcNow);

    private byte[] AllFiles()
    {
        using var all = new MemoryStream();
        foreach (var file in new[] { _path, _path + "-wal" }.Where(File.Exists))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.CopyTo(all);
        }
        return all.ToArray();
    }

    private static bool Contains(byte[] haystack, string text) =>
        haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    [Fact]
    public void Text_can_be_searched_but_is_not_readable_in_the_files()
    {
        using (var index = new SearchIndex(_path, Key))
        {
            index.Upsert(Doc("a", $"The marker {Marker} is in this body"));
            Assert.Single(index.Search(FtsQuery.Build(Marker)!));
            Assert.False(Contains(AllFiles(), Marker)); // while open, with the write-ahead log in use
        }
        Assert.False(SearchIndex.IsUnencrypted(_path));
        Assert.False(Contains(AllFiles(), Marker));

        using var reopened = new SearchIndex(_path, Key);
        Assert.Single(reopened.Search(FtsQuery.Build(Marker)!));
    }

    [Fact]
    public void Another_key_cannot_open_it_and_leaves_the_file_deletable()
    {
        using (var index = new SearchIndex(_path, Key)) index.Upsert(Doc("a", "body"));

        Assert.True(SearchIndex.CanOpen(_path, Key));
        Assert.False(SearchIndex.CanOpen(_path, "another-key"));
        File.Delete(_path);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void An_index_from_before_encryption_is_recognized()
    {
        using (var index = new SearchIndex(_path)) index.Upsert(Doc("a", Marker));

        Assert.True(SearchIndex.IsUnencrypted(_path));
        Assert.False(SearchIndex.CanOpen(_path, Key));
    }

    [Fact]
    public void A_missing_file_is_neither_unencrypted_nor_unopenable()
    {
        Assert.False(SearchIndex.IsUnencrypted(_path));
        Assert.True(SearchIndex.CanOpen(_path, Key));
    }

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            try { File.Delete(file); } catch (IOException) { }
    }
}

public class SealedFileTests
{
    private static DataKeys Keys(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    [Fact]
    public void Sealed_data_opens_with_the_same_key_and_hides_the_content()
    {
        var plain = Encoding.UTF8.GetBytes("a saved preview of a private page");
        var sealedData = Keys(1).Seal(plain);

        Assert.Equal(plain, Keys(1).Open(sealedData));
        Assert.True(sealedData.AsSpan().IndexOf("private"u8) < 0);
    }

    [Fact]
    public void Each_seal_is_different() =>
        Assert.NotEqual(Keys(1).Seal("same"u8), Keys(1).Seal("same"u8));

    [Fact]
    public void Another_key_tampering_or_a_plain_file_cannot_be_opened()
    {
        var sealedData = Keys(1).Seal("content"u8);
        Assert.ThrowsAny<CryptographicException>(() => Keys(2).Open(sealedData));

        sealedData[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => Keys(1).Open(sealedData));

        Assert.ThrowsAny<CryptographicException>(() => Keys(1).Open("short"u8));
    }

    [Fact]
    public void Database_and_file_keys_differ_by_master_key() =>
        Assert.NotEqual(Keys(1).DatabaseKey, Keys(2).DatabaseKey);
}

[SupportedOSPlatform("windows")]
public sealed class DataKeyFileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wws-key-{Guid.NewGuid():N}.key");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void The_key_is_created_once_then_reused()
    {
        var first = DataKeys.LoadOrCreate(_path);
        var second = DataKeys.LoadOrCreate(_path);

        Assert.True(first.IsNew);
        Assert.False(second.IsNew);
        Assert.Equal(first.DatabaseKey, second.DatabaseKey);
        Assert.Equal("data"u8.ToArray(), second.Open(first.Seal("data"u8)));
    }

    [Fact]
    public void A_damaged_key_file_is_replaced()
    {
        var first = DataKeys.LoadOrCreate(_path);
        File.WriteAllBytes(_path, [1, 2, 3]);

        var replaced = DataKeys.LoadOrCreate(_path);

        Assert.True(replaced.IsNew);
        Assert.NotEqual(first.DatabaseKey, replaced.DatabaseKey);
    }
}
