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

    public static string NewKey() => "files/" + Guid.NewGuid().ToString("N");

    // S3 checks signed URL expiry against real time, so the expiry uses the system clock.
    public SignedUrl SignedUpload(string key, string contentType) => Sign(new GetPreSignedUrlRequest
    {
        BucketName = bucket, Key = key, Verb = HttpVerb.PUT, ContentType = contentType
    });

    public SignedUrl SignedDownload(string key, string fileName)
    {
        var request = new GetPreSignedUrlRequest { BucketName = bucket, Key = key, Verb = HttpVerb.GET };
        request.ResponseHeaderOverrides.ContentDisposition = Attachment(fileName);
        return Sign(request);
    }

    private SignedUrl Sign(GetPreSignedUrlRequest request)
    {
        var expires = DateTime.UtcNow.Add(lifetime);
        request.Protocol = protocol;
        request.Expires = expires;
        return new(client.GetPreSignedURL(request), new DateTimeOffset(expires));
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

    public Task Delete(string key, CancellationToken cancellation) => client.DeleteObjectAsync(bucket, key, cancellation);

    private static string Attachment(string fileName)
    {
        var fallback = new string([.. fileName.Select(c => c is >= ' ' and <= '~' and not ('"' or '\\') ? c : '_')]);
        return $"attachment; filename=\"{fallback}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    public void Dispose() => client.Dispose();
}

public sealed record SignedUrl(string Url, DateTimeOffset ExpiresAt);
