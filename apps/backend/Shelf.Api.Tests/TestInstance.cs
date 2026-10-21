using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class TestClock : TimeProvider
{
    private long ticks = DateTimeOffset.UtcNow.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
}

public abstract class TestInstance : IAsyncLifetime
{
    protected const string Password = "integration-only-password";
    protected readonly TestClock Clock = new();
    protected WebApplicationFactory<Program> App = null!;
    protected HttpClient Client = null!;
    protected HttpClient Admin = null!;
    protected Guid AdminId;
    protected readonly CapturedLogs Logs = new();
    // Signed URLs point straight at the S3 service, outside the test host.
    protected static readonly HttpClient Transfer = new();
    protected static readonly string S3SecretKey = Environment.GetEnvironmentVariable("SHELF_TEST_S3_SECRET_KEY") ?? "change-me-local-only";
    protected AmazonS3Client S3 = null!;
    protected readonly string Bucket = "shelf-test-" + Guid.NewGuid().ToString("N");
    private readonly string databaseName = "shelf_test_" + Guid.NewGuid().ToString("N");
    private string connectionString = "";
    private string adminConnection = "";

    public async Task InitializeAsync()
    {
        adminConnection = Environment.GetEnvironmentVariable("SHELF_TEST_POSTGRES") ?? "Host=localhost;Username=shelf;Database=postgres";
        await using (var connection = new NpgsqlConnection(adminConnection))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {databaseName}", connection);
            await command.ExecuteNonQueryAsync();
        }
        connectionString = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
        var migration = await Cli(null, "migrate");
        Assert.Equal(0, migration.Code);
        var bootstrap = await Cli(Password, "admin", "create", "owner", "owner@example.test");
        Assert.Equal(0, bootstrap.Code);
        AdminId = Guid.Parse(bootstrap.Output.Trim());
        var s3Endpoint = Environment.GetEnvironmentVariable("SHELF_TEST_S3_ENDPOINT") ?? "http://localhost:9000";
        var s3AccessKey = Environment.GetEnvironmentVariable("SHELF_TEST_S3_ACCESS_KEY") ?? "shelf";
        S3 = new AmazonS3Client(s3AccessKey, S3SecretKey, new AmazonS3Config { ServiceURL = s3Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        await S3.PutBucketAsync(Bucket);
        App = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Shelf"] = connectionString,
                ["Authentication:ClientOrigin"] = "https://shelf.example.test",
                ["Authentication:PublicPermitLimit"] = "1000",
                ["Storage:S3:Endpoint"] = s3Endpoint,
                ["Storage:S3:Region"] = "us-east-1",
                ["Storage:S3:Bucket"] = Bucket,
                ["Storage:S3:AccessKey"] = s3AccessKey,
                ["Storage:S3:SecretKey"] = S3SecretKey,
                ["Storage:S3:ForcePathStyle"] = "true"
            }));
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(Clock));
            builder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(Logs));
        });
        Client = App.CreateClient();
        Admin = await SignIn("owner");
    }

    protected async Task<(int Code, string Output, string Error)> Cli(string? password, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        start.Environment["ConnectionStrings__Shelf"] = connectionString;
        start.Environment["Logging__LogLevel__Default"] = "None";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (password is not null) await process.StandardInput.WriteLineAsync(password);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }
    protected async Task Sql(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }
    protected async Task<HttpClient> SignIn(string identifier, string password = Password, string device = "test-device")
    {
        var response = await Client.PostAsJsonAsync("/auth/login", new { identifier, password, deviceName = device });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await Json(response);
        var client = App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.GetProperty("token").GetString());
        return client;
    }
    protected static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {content}");
        Assert.False(string.IsNullOrWhiteSpace(content), $"Empty JSON at HTTP {(int)response.StatusCode}");
        return JsonDocument.Parse(content).RootElement.Clone();
    }
    protected async Task<JsonElement> Invite(string email)
    {
        var response = await Admin.PostAsJsonAsync("/admin/invitations", new { email });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Json(response);
    }
    protected static string Token(JsonElement link) => link.GetProperty("link").GetString()!.Split("#token=")[1];
    protected async Task<(Guid Id, HttpClient Client)> User(string username = "member")
    {
        var link = await Invite(username + "@example.test");
        var response = await Client.PostAsJsonAsync("/auth/register", new { token = Token(link), username, password = Password });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await Json(response)).GetProperty("id").GetGuid();
        return (id, await SignIn(username));
    }
    public async Task DisposeAsync()
    {
        Admin?.Dispose();
        Client?.Dispose();
        if (App is not null) await App.DisposeAsync();
        try
        {
            if (S3 is not null) await DeleteBucket();
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(adminConnection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
    private async Task DeleteBucket()
    {
        using (S3)
        {
            var listing = new ListObjectsV2Request { BucketName = Bucket };
            ListObjectsV2Response page;
            do
            {
                page = await S3.ListObjectsV2Async(listing);
                foreach (var item in page.S3Objects ?? []) await S3.DeleteObjectAsync(Bucket, item.Key);
                listing.ContinuationToken = page.NextContinuationToken;
            } while (page.IsTruncated == true);
            await S3.DeleteBucketAsync(Bucket);
        }
    }
}

public sealed class CapturedLogs : ILoggerProvider
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
    public void Dispose() { }
    private sealed class CaptureLogger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
    }
}
