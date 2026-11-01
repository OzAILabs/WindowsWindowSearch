using WindowSearcher.Core;

namespace WindowSearcher.App.Services;

/// <summary>
/// Encryption for what the app saves: the index and the preview files. The index is only a cache of what's
/// on screen, so anything that can't be read with the current key is deleted and rebuilt.
/// </summary>
internal static class SecureStore
{
    private static DataKeys? _keys;

    private static DataKeys Keys => _keys ?? throw new InvalidOperationException("SecureStore.Initialize wasn't called");

    public static string DatabaseKey => Keys.DatabaseKey;

    /// <summary>Call once at startup, before anything opens the index.</summary>
    public static void Initialize()
    {
        _keys = DataKeys.LoadOrCreate(AppPaths.Keys);
        var reason = _keys.IsNew ? "new key"
            : SearchIndex.IsUnencrypted(AppPaths.Database) ? "not encrypted"
            : !SearchIndex.CanOpen(AppPaths.Database, _keys.DatabaseKey) ? "can't be opened"
            : null;
        if (reason is not null) DeleteSavedData(reason);
    }

    public static byte[] Seal(byte[] plain) => Keys.Seal(plain);

    public static byte[] Open(byte[] sealedData) => Keys.Open(sealedData);

    private static void DeleteSavedData(string reason)
    {
        var files = new[] { AppPaths.Database, AppPaths.Database + "-wal", AppPaths.Database + "-shm" }
            .Concat(Directory.EnumerateFiles(AppPaths.Thumbs))
            .Where(File.Exists)
            .ToList();
        foreach (var file in files) File.Delete(file);
        if (files.Count > 0) Log.Info($"Deleted the saved index and previews ({reason}); they're rebuilt as windows are read");
    }
}
