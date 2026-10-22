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

    // Standard S3 DELETE is idempotent: repeating after a lost response or process restart is safe.
    public Task Delete(string key, CancellationToken cancellation) => client.DeleteObjectAsync(bucket, key, cancellation);

    private static string Attachment(string fileName)
    {
        var fallback = new string([.. fileName.Select(c => c is >= ' ' and <= '~' and not ('"' or '\\') ? c : '_')]);
        return $"attachment; filename=\"{fallback}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    public void Dispose() => client.Dispose();
}

public sealed record SignedUrl(string Url, DateTimeOffset ExpiresAt);
