using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Shelf.Api.Storage;

/// <summary>Bucket access through the standard S3 API only, so any S3-compatible provider works.</summary>
public sealed class ObjectStore : IDisposable
{
    private readonly AmazonS3Client client;
    private readonly string bucket;
    private readonly Protocol protocol;
    private readonly TimeSpan lifetime;
    private readonly long singleCopyLimit;
    private readonly long copyPartSize;
    private const long MinPartSize = 5L * 1024 * 1024;
    private const long MaxPartSize = 5L * 1024 * 1024 * 1024;

    /// <summary>Size of every part but the last; files up to this size are sent with a single PUT.</summary>
    public long PartSize { get; }
    /// <summary>Delay after which an unconfirmed upload expires and its objects are purged.</summary>
    public TimeSpan UploadExpiry { get; }
    public TimeSpan CleanupInterval { get; }

    public ObjectStore(IConfiguration configuration)
    {
        var s3 = configuration.GetSection("Storage:S3");
        string Required(string key) => s3[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"Set Storage__S3__{key}.");
        var endpoint = Required("Endpoint");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Set Storage__S3__Endpoint to an absolute HTTP(S) URL.");
        bucket = Required("Bucket");
        protocol = uri.Scheme == "http" ? Protocol.HTTP : Protocol.HTTPS;
        lifetime = TimeSpan.FromMinutes(configuration.GetValue("Storage:SignedUrlMinutes", 15));
        // S3 refuses parts under 5 MiB, except the last one.
        PartSize = Math.Max(5, configuration.GetValue("Storage:PartSizeMiB", 16)) * MiB;
        UploadExpiry = TimeSpan.FromHours(configuration.GetValue("Storage:UploadExpiryHours", 24));
        CleanupInterval = TimeSpan.FromSeconds(configuration.GetValue("Storage:UploadCleanupSeconds", 300.0));
        singleCopyLimit = s3.GetValue("SingleCopyLimitBytes", MaxPartSize);
        copyPartSize = s3.GetValue("CopyPartSizeBytes", 128L * 1024 * 1024);
        if (singleCopyLimit is < MinPartSize or > MaxPartSize || copyPartSize is < MinPartSize or > MaxPartSize)
            throw new InvalidOperationException("S3 copy sizes must be between 5 MiB and 5 GiB.");
        client = new AmazonS3Client(new BasicAWSCredentials(Required("AccessKey"), Required("SecretKey")), new AmazonS3Config
        {
            ServiceURL = endpoint,
            AuthenticationRegion = Required("Region"),
            ForcePathStyle = s3.GetValue("ForcePathStyle", false),
            // Checksums beyond the S3 baseline are not supported by every S3-compatible provider.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });
    }

    private const long MiB = 1024 * 1024;
    private const int MaxParts = 10_000;

    public static string NewKey() => "files/" + Guid.NewGuid().ToString("N");

    /// <summary>Part size of a multipart upload: the configured size, grown in whole MiB to stay within the S3 part count.</summary>
    public long MultipartPartSize(long size) => Math.Max(PartSize, (size + MaxParts * MiB - 1) / (MaxParts * MiB) * MiB);

    // S3 checks signed URL expiry against real time, so the expiry uses the system clock.
    /// <summary>Expiry of URLs signed now, at most <paramref name="remaining"/> from now.</summary>
    public DateTime UrlExpiry(TimeSpan remaining) => DateTime.UtcNow.Add(remaining < lifetime ? remaining : lifetime);

    public string SignedUpload(string key, string contentType, DateTime expires) => Sign(new GetPreSignedUrlRequest
    {
        BucketName = bucket, Key = key, Verb = HttpVerb.PUT, ContentType = contentType, Expires = expires
    });

    public string SignedPart(string key, string uploadId, int number, DateTime expires) => Sign(new GetPreSignedUrlRequest
    {
        BucketName = bucket, Key = key, Verb = HttpVerb.PUT, UploadId = uploadId, PartNumber = number, Expires = expires
    });

    public SignedUrl SignedDownload(string key, string fileName)
    {
        var expires = UrlExpiry(lifetime);
        var request = new GetPreSignedUrlRequest { BucketName = bucket, Key = key, Verb = HttpVerb.GET, Expires = expires };
        request.ResponseHeaderOverrides.ContentDisposition = Attachment(fileName);
        return new(Sign(request), new DateTimeOffset(expires));
    }

    private string Sign(GetPreSignedUrlRequest request)
    {
        request.Protocol = protocol;
        return client.GetPreSignedURL(request);
    }

    /// <summary>Starts a multipart upload and returns its S3 upload ID.</summary>
    public async Task<string> StartMultipart(string key, string contentType, CancellationToken cancellation) =>
        (await client.InitiateMultipartUploadAsync(new() { BucketName = bucket, Key = key, ContentType = contentType }, cancellation)).UploadId;

    /// <summary>Parts received so far by number, or null when the multipart upload no longer exists (already assembled).</summary>
    public async Task<Dictionary<int, PartDetail>?> Parts(string key, string uploadId, CancellationToken cancellation)
    {
        var parts = new Dictionary<int, PartDetail>();
        var request = new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId };
        try
        {
            ListPartsResponse page;
            do
            {
                page = await client.ListPartsAsync(request, cancellation);
                foreach (var part in page.Parts ?? []) parts[part.PartNumber!.Value] = part;
                request.PartNumberMarker = page.NextPartNumberMarker?.ToString();
            } while (page.IsTruncated == true);
        }
        catch (AmazonS3Exception error) when (error.ErrorCode == "NoSuchUpload") { return null; }
        return parts;
    }

    public Task Assemble(string key, string uploadId, IEnumerable<PartDetail> parts, CancellationToken cancellation) =>
        client.CompleteMultipartUploadAsync(new()
        {
            BucketName = bucket, Key = key, UploadId = uploadId,
            PartETags = [.. parts.Select(part => new PartETag(part.PartNumber!.Value, part.ETag))]
        }, cancellation);

    /// <summary>Removes everything an unconfirmed upload may have left: its pending parts and its object.</summary>
    public async Task Discard(string key, string? uploadId, CancellationToken cancellation)
    {
        if (uploadId is not null)
        {
            try { await client.AbortMultipartUploadAsync(bucket, key, uploadId, cancellation); }
            catch (AmazonS3Exception error) when (error.ErrorCode == "NoSuchUpload") { }
        }
        await client.DeleteObjectAsync(bucket, key, cancellation);
    }

    /// <summary>Size of a stored object, or null when it does not exist.</summary>
    public async Task<long?> Size(string key, CancellationToken cancellation)
    {
        try { return (await client.GetObjectMetadataAsync(bucket, key, cancellation)).ContentLength; }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    /// <summary>Creates an independent object within the bucket, without relaying file bytes.</summary>
    public async Task Copy(string source, string destination, long expectedSize, CancellationToken cancellation)
    {
        var metadata = await client.GetObjectMetadataAsync(bucket, source, cancellation);
        if (metadata.ContentLength != expectedSize) throw new StorageError(409, "source_changed");
        if (expectedSize <= singleCopyLimit)
        {
            await client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = bucket, SourceKey = source, DestinationBucket = bucket, DestinationKey = destination,
                ETagToMatch = metadata.ETag
            }, cancellation);
            return;
        }
        // Adapt to large objects without exceeding S3's 10,000 parts or 5 GiB per part.
        var partSize = Math.Max(copyPartSize, (expectedSize - 1) / 10000 + 1);
        if (partSize > MaxPartSize) throw new StorageError(503, "storage_unavailable");
        var upload = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket, Key = destination, ContentType = metadata.Headers.ContentType
        }, cancellation);
        try
        {
            var parts = new List<PartETag>();
            for (long offset = 0; offset < expectedSize; offset += partSize)
            {
                var number = parts.Count + 1;
                var part = await client.CopyPartAsync(new CopyPartRequest
                {
                    SourceBucket = bucket, SourceKey = source, DestinationBucket = bucket, DestinationKey = destination,
                    UploadId = upload.UploadId, PartNumber = number, FirstByte = offset,
                    LastByte = Math.Min(offset + partSize, expectedSize) - 1, ETagToMatch = [metadata.ETag]
                }, cancellation);
                parts.Add(new PartETag(number, part.ETag));
            }
            await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket, Key = destination, UploadId = upload.UploadId, PartETags = parts
            }, cancellation);
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = bucket, Key = destination, UploadId = upload.UploadId
                }, cleanup.Token);
            }
            catch (Exception error) when (error is AmazonServiceException or HttpRequestException or OperationCanceledException)
            { /* Bucket lifecycle rules must also abort abandoned multipart uploads after a crash. */ }
            throw;
        }
    }

    // Standard S3 DELETE is idempotent: repeating after a lost response or process restart is safe.
    public Task Delete(string key, CancellationToken cancellation) => client.DeleteObjectAsync(bucket, key, cancellation);

    public async Task<(string[] Keys, string? Continuation)> ListFiles(string? continuation, CancellationToken cancellation)
    {
        var page = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket, Prefix = "files/", MaxKeys = 1000, ContinuationToken = continuation
        }, cancellation);
        return ([.. (page.S3Objects ?? []).Select(item => item.Key)], page.NextContinuationToken);
    }

    private static string Attachment(string fileName)
    {
        var fallback = new string([.. fileName.Select(c => c is >= ' ' and <= '~' and not ('"' or '\\') ? c : '_')]);
        return $"attachment; filename=\"{fallback}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    public void Dispose() => client.Dispose();
}

public sealed record SignedUrl(string Url, DateTimeOffset ExpiresAt);
