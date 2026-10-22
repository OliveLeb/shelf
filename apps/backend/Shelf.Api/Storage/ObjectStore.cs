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

    private static string Attachment(string fileName)
    {
        var fallback = new string([.. fileName.Select(c => c is >= ' ' and <= '~' and not ('"' or '\\') ? c : '_')]);
        return $"attachment; filename=\"{fallback}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    public void Dispose() => client.Dispose();
}

public sealed record SignedUrl(string Url, DateTimeOffset ExpiresAt);
