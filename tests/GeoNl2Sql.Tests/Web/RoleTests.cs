using GeoNl2Sql.Core.Agent;
using GeoNl2Sql.Web.Controllers;
using GeoNl2Sql.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace GeoNl2Sql.Tests.Web;

/// <summary>
/// 角色選擇的離線測試（docs/m4-implementation-plan.md §4.1、§4.3）：缺省為 analyst、admin 生效、
/// 非法值回 400 且不呼叫 Agent、請求本文以外（查詢字串、標頭）的 <c>role</c> 無效。兩個端點行為一致。
/// </summary>
public class RoleTests
{
    private static GeoAgentResult Done() => new("好的", null, [], null, false);

    /// <summary>建立 <c>POST /query</c> 控制器，並把收到的角色記進 <paramref name="seen"/>。</summary>
    private static QueryController Query(List<UserRole> seen, HttpContext? http = null) =>
        new((_, role, _) => { seen.Add(role); return Task.FromResult(Done()); }, NullLogger<QueryController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http ?? new DefaultHttpContext() },
        };

    /// <summary>建立 <c>POST /query/stream</c> 控制器，並把收到的角色記進 <paramref name="seen"/>。</summary>
    private static QueryStreamController Stream(List<UserRole> seen, HttpContext? http = null)
    {
        http ??= new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        return new QueryStreamController((_, role, _) => Events(seen, role), NullLogger<QueryStreamController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static async IAsyncEnumerable<GeoAgentEvent> Events(List<UserRole> seen, UserRole role)
    {
        seen.Add(role);
        yield return new Completed(Done());
        await Task.CompletedTask;
    }

    /// <summary>省略 role 為 analyst；analyst／admin 不分大小寫與前後空白。</summary>
    [Theory]
    [InlineData(null, UserRole.Analyst)]
    [InlineData("analyst", UserRole.Analyst)]
    [InlineData("ADMIN", UserRole.Admin)]
    [InlineData(" Admin ", UserRole.Admin)]
    public async Task ValidRole_IsPassedToAgent(string? role, UserRole expected)
    {
        var seenQuery = new List<UserRole>();
        var seenStream = new List<UserRole>();

        Assert.IsType<OkObjectResult>(await Query(seenQuery).Post(new QueryRequest("q", role), default));
        await Stream(seenStream).Post(new QueryRequest("q", role), default);

        Assert.Equal([expected], seenQuery);
        Assert.Equal([expected], seenStream);
    }

    /// <summary>非法值（含空字串、數字、其他角色名）回 400，而且不呼叫 Agent。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("superuser")]
    [InlineData("1")]
    [InlineData("analyst,admin")]
    public async Task InvalidRole_Returns400_WithoutCallingAgent(string role)
    {
        var seenQuery = new List<UserRole>();
        var seenStream = new List<UserRole>();

        var a = Assert.IsType<BadRequestObjectResult>(await Query(seenQuery).Post(new QueryRequest("q", role), default));
        var b = Assert.IsType<BadRequestObjectResult>(await Stream(seenStream).Post(new QueryRequest("q", role), default));

        Assert.Equal(QueryController.InvalidRoleMessage, Assert.IsType<QueryResponse>(a.Value).Error);
        Assert.Equal(QueryController.InvalidRoleMessage, Assert.IsType<QueryResponse>(b.Value).Error);
        Assert.Empty(seenQuery);
        Assert.Empty(seenStream);
    }

    /// <summary>查詢字串與標頭帶 role=admin，但請求本文沒有：仍是 analyst。</summary>
    [Fact]
    public async Task RoleOutsideBody_IsIgnored()
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString("?role=admin");
        http.Request.Headers["role"] = "admin";
        http.Request.Headers["X-Role"] = "admin";
        var seenQuery = new List<UserRole>();
        var seenStream = new List<UserRole>();

        await Query(seenQuery, http).Post(new QueryRequest("q"), default);
        await Stream(seenStream, http).Post(new QueryRequest("q"), default);

        Assert.Equal([UserRole.Analyst], seenQuery);
        Assert.Equal([UserRole.Analyst], seenStream);
    }
}
