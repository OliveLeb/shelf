using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class PersonalSpaceTests : TestInstance
{
    [Fact]
    public async Task Uploaded_file_appears_in_the_personal_space_after_confirmation_and_downloads_under_its_name()
    {
        var (memberId, member) = await User();
        const string name = "Été à Paris \"1\".jpg";
        var upload = await Declare(member, name, Photo.Length);
        Assert.Empty(await Root(member));

        await Put(upload, Photo);
        Assert.Empty(await Root(member));

        var confirmation = await Confirm(member, upload);
        Assert.Equal(HttpStatusCode.Created, confirmation.StatusCode);
        var file = await Json(confirmation);
        Assert.Equal(name, file.GetProperty("name").GetString());
        Assert.Equal(Photo.Length, file.GetProperty("size").GetInt64());
        Assert.Equal("image/jpeg", file.GetProperty("contentType").GetString());
        Assert.InRange(file.GetProperty("addedAt").GetDateTimeOffset(), Clock.GetUtcNow().AddSeconds(-1), Clock.GetUtcNow());
        Assert.Equal(memberId, file.GetProperty("addedBy").GetProperty("id").GetGuid());
        Assert.Equal("member", file.GetProperty("addedBy").GetProperty("username").GetString());
        var listed = Assert.Single(await Root(member));
        Assert.Equal(file.GetProperty("id").GetGuid(), listed.GetProperty("id").GetGuid());
        Assert.Equal(name, listed.GetProperty("name").GetString());

        var download = await Json(await member.GetAsync($"/files/{file.GetProperty("id").GetGuid()}/download"));
        var content = await Transfer.GetAsync(download.GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(Photo, await content.Content.ReadAsByteArrayAsync());
        Assert.Equal(name, content.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal("attachment", content.Content.Headers.ContentDisposition?.DispositionType);

        var logs = string.Join('\n', Logs.Messages);
        foreach (var secret in new[] { upload.GetProperty("parts")[0].GetProperty("url").GetString()!, download.GetProperty("url").GetString()!, "X-Amz-Signature", S3SecretKey })
            Assert.DoesNotContain(secret, logs);
    }
    [Fact]
    public async Task Confirmation_fails_until_the_file_is_transferred_with_the_declared_size()
    {
        var (_, member) = await User();
        var upload = await Declare(member, "video.mp4", Photo.Length, "video/mp4");
        var missing = await Confirm(member, upload);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal("upload_incomplete", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        await Put(upload, Photo[..^1]);
        var truncated = await Confirm(member, upload);
        Assert.Equal(HttpStatusCode.Conflict, truncated.StatusCode);
        Assert.Equal("size_mismatch", (await truncated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Empty(await Root(member));

        await Put(upload, Photo);
        Assert.Equal(HttpStatusCode.Created, (await Confirm(member, upload)).StatusCode);
        Assert.Single(await Root(member));
        Assert.Equal(HttpStatusCode.NotFound, (await Confirm(member, upload)).StatusCode);
    }
    [Fact]
    public async Task Name_already_used_in_the_personal_space_ignoring_case_is_refused_at_declaration_and_confirmation()
    {
        var (_, member) = await User();
        var first = await Declare(member, "Photo.JPG", Photo.Length);
        var second = await Declare(member, "photo.jpg", Photo.Length);
        await Upload(member, "PHOTO.jpg");
        var declaration = await member.PostAsJsonAsync("/uploads", new { name = "photo.jpg", size = 1 });
        Assert.Equal(HttpStatusCode.Conflict, declaration.StatusCode);
        Assert.Equal("name_conflict", (await declaration.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        foreach (var upload in new[] { first, second })
        {
            await Put(upload, Photo);
            var confirmation = await Confirm(member, upload);
            Assert.Equal(HttpStatusCode.Conflict, confirmation.StatusCode);
            Assert.Equal("name_conflict", (await confirmation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
        Assert.Equal("PHOTO.jpg", Assert.Single(await Root(member)).GetProperty("name").GetString());
    }
    [Theory]
    [InlineData("", 1L, null, "invalid_name")]
    [InlineData("holiday/photo.jpg", 1L, null, "invalid_name")]
    [InlineData("..", 1L, null, "invalid_name")]
    [InlineData("line\nbreak.jpg", 1L, null, "invalid_name")]
    [InlineData("photo.jpg", -1L, null, "invalid_size")]
    [InlineData("photo.jpg", null, null, "invalid_size")]
    [InlineData("photo.jpg", 1L, "jpeg", "invalid_content_type")]
    public async Task Invalid_upload_declaration_is_rejected(string name, long? size, string? contentType, string error)
    {
        var (_, member) = await User();
        var response = await member.PostAsJsonAsync("/uploads", new { name, size, contentType });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }
    [Fact]
    public async Task Personal_file_is_invisible_to_other_users_and_administrators()
    {
        var (_, member) = await User();
        var (_, other) = await User("other");
        var file = Id(await Upload(member, "private.jpg"));
        var pending = await Declare(member, "pending.jpg", Photo.Length);
        await Put(pending, Photo);
        // The administrator account comes from the server command and has its own personal space.
        var adminFile = Id(await Upload(Admin, "private.jpg"));

        foreach (var intruder in new[] { other, Admin })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await intruder.GetAsync($"/files/{file}/download")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Confirm(intruder, pending)).StatusCode);
            Assert.DoesNotContain(await Root(intruder), e => e.GetProperty("id").GetGuid() == file);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/files/{adminFile}/download")).StatusCode);
        Assert.Empty(await Root(other));
        Assert.Equal(adminFile, Assert.Single(await Root(Admin)).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"/files/{file}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsJsonAsync("/uploads", new { name = "anonymous.jpg", size = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Confirm(member, pending)).StatusCode);
        Assert.Equal(2, (await Root(member)).Length);
    }
    [Fact]
    public async Task Accounts_existing_before_storage_receive_a_personal_space_on_migration()
    {
        // Rebuild the schema as deployed before storage: only the authentication migration, with existing accounts.
        using var reader = new StreamReader(typeof(Program).Assembly.GetManifestResourceStream("Shelf.Api.Migrations.001_authentication.sql")!);
        await Sql($"DROP SCHEMA public CASCADE; CREATE SCHEMA public; {await reader.ReadToEndAsync()} CREATE TABLE schema_migrations (version integer PRIMARY KEY); INSERT INTO schema_migrations VALUES (1);");
        var hash = new Microsoft.AspNetCore.Identity.PasswordHasher<string>().HashPassword("", Password);
        foreach (var username in new[] { "early", "earlier" })
            await Sql("INSERT INTO accounts VALUES ($1,$2,$3,$4,false,true)", Guid.NewGuid(), username, username + "@example.test", hash);

        Assert.Equal(0, (await Cli(null, "migrate")).Code);

        using var early = await SignIn("early");
        using var earlier = await SignIn("earlier");
        var file = Id(await Upload(early, "before.jpg"));
        Assert.Equal(file, Assert.Single(await Root(early)).GetProperty("id").GetGuid());
        Assert.Empty(await Root(earlier));
        Assert.Equal(HttpStatusCode.NotFound, (await earlier.GetAsync($"/files/{file}/download")).StatusCode);
    }
}
