using ADFi.Infrastructure.CloudAI;

namespace ADFi.Core.Tests;

public sealed class ChatGptAuthorizationTests
{
    [Fact]
    public void InitialAuthorizationUsesLoopbackPkceAndExactPlanScopes()
    {
        var attempt = ChatGptAuthorization.CreateAttempt(
            Guid.Parse("6b050222-9f45-4e15-a56e-25359083e872"),
            1455,
            TimeProvider.System);
        var uri = ChatGptAuthorization.BuildAuthorizationUri(attempt);

        Assert.Equal("auth.openai.com", uri.Host);
        Assert.Contains("client_id=dynamic_agent_client", uri.Query, StringComparison.Ordinal);
        Assert.Contains("agent_name_hint=ADFi", uri.Query, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", uri.Query, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(ChatGptAuthorization.ApiResource), uri.Query, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(ChatGptAuthorization.PlanUsageScope), uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", uri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CallbackRejectsStateMismatch()
    {
        var attempt = ChatGptAuthorization.CreateAttempt(Guid.NewGuid(), 1455, TimeProvider.System);
        var callback = new ChatGptAuthorizationCallback(
            "code",
            "wrong-state",
            "oaiapp_example",
            new HashSet<string>([ChatGptAuthorization.PlanUsageScope], StringComparer.Ordinal));

        Assert.Throws<InvalidOperationException>(() => ChatGptAuthorization.ValidateCallback(attempt, callback));
    }

    [Fact]
    public void IdentityWithoutGrantedPlanScopeCannotInvokePlanUsage()
    {
        Assert.False(ChatGptAuthorization.GrantsPlanUsage(
            new HashSet<string>(["openid", "profile", "email"], StringComparer.Ordinal)));
    }
}
