using Shelf.Api.Authentication;

namespace Shelf.Api.Storage;

public sealed partial class StorageTransaction
{
    public async Task<ThumbnailResult> Thumbnail(Account account, Guid id)
    {
        if ((await Access(account, id)).Kind != "file") throw new StorageError(404, "element_not_found");
        var rows = await Query("SELECT thumbnail_state, thumbnail_key FROM elements WHERE id=$1",
            r => new ThumbnailResult(r.GetString(0), r.GetString(0) == "available" ? store.SignedInline(r.GetString(1)) : null), id);
        return rows.SingleOrDefault() ?? throw new StorageError(404, "element_not_found");
    }

    public async Task<SignedUrl> StreamVideo(Account account, Guid id)
    {
        if ((await Access(account, id)).Kind != "file") throw new StorageError(404, "element_not_found");
        var rows = await Query("SELECT object_key, content_type FROM elements WHERE id=$1",
            r => (Key: r.GetString(0), Type: r.GetString(1)), id);
        if (rows.Count == 0) throw new StorageError(404, "element_not_found");
        if (!rows[0].Type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) throw new StorageError(400, "not_video");
        return store.SignedInline(rows[0].Key);
    }
}
