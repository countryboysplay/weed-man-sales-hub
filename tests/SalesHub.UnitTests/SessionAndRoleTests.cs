using SalesHub.Application.Auth;
using SalesHub.Domain;
using Xunit;

namespace SalesHub.UnitTests;

public class SessionTokensTests
{
    [Fact]
    public void A_verifier_matches_its_own_hash()
    {
        var verifier = SessionTokens.NewVerifier();
        Assert.True(SessionTokens.Matches(verifier, SessionTokens.Hash(verifier)));
    }

    [Fact]
    public void A_different_verifier_does_not_match()
    {
        var hash = SessionTokens.Hash(SessionTokens.NewVerifier());
        Assert.False(SessionTokens.Matches(SessionTokens.NewVerifier(), hash));
    }

    [Fact]
    public void Garbage_stored_hash_never_matches() =>
        Assert.False(SessionTokens.Matches(SessionTokens.NewVerifier(), "not-hex!"));

    [Fact]
    public void Verifiers_are_unique() =>
        Assert.NotEqual(SessionTokens.NewVerifier(), SessionTokens.NewVerifier());
}

public class RoleTests
{
    [Fact]
    public void Exactly_the_four_roles_exist() =>
        Assert.Equal(
            ["SalesAgent", "SalesSupervisor", "SalesManager", "Owner"],
            Roles.All);

    [Fact]
    public void Management_means_supervisor_and_above()
    {
        Assert.False(Roles.IsManagement(Roles.SalesAgent));
        Assert.True(Roles.IsManagement(Roles.SalesSupervisor));
        Assert.True(Roles.IsManagement(Roles.SalesManager));
        Assert.True(Roles.IsManagement(Roles.Owner));
    }

    [Fact]
    public void Unknown_roles_are_invalid()
    {
        Assert.False(Roles.IsValid("Admin"));
        Assert.False(Roles.IsValid("salesagent")); // case-sensitive by design
    }
}

public class IdleCapabilityStateTests
{
    [Theory]
    [InlineData(IdleCapabilityState.Unknown, false)]
    [InlineData(IdleCapabilityState.Unsupported, false)]
    [InlineData(IdleCapabilityState.PermissionDenied, false)]
    [InlineData(IdleCapabilityState.Starting, false)]
    [InlineData(IdleCapabilityState.Verified, true)]
    [InlineData(IdleCapabilityState.Stale, false)]
    [InlineData(IdleCapabilityState.Revoked, false)]
    [InlineData(IdleCapabilityState.Error, false)]
    public void Only_verified_permits_monitored_work(IdleCapabilityState state, bool permitted) =>
        Assert.Equal(permitted, state.PermitsMonitoredWork());
}
