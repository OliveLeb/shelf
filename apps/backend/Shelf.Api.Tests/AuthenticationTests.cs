using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Shelf.Api.Tests;

public class AuthenticationTests : TestInstance
{
    [Fact]
    public async Task Public_registration_requires_an_invitation()
    {
        var response = await Client.PostAsJsonAsync("/auth/register", new { username = "visitor", password = "a-long-test-password" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
