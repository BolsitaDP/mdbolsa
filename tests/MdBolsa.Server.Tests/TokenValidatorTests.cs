using MdBolsa.Server.Auth;

namespace MdBolsa.Server.Tests;

// The token check, including the cases that matter for a shared secret: a wrong
// token, a missing token, an unset token, and a token that is a prefix of the
// real one (which a naive StartsWith check would wave through).
public class TokenValidatorTests
{
    private const string Configured = "s3cret-token-value";

    [Fact]
    public void Accepts_TheConfiguredToken() => Assert.True(TokenValidator.IsValid(Configured, Configured));

    [Theory]
    [InlineData("wrong")]
    [InlineData("s3cret")]
    [InlineData("s3cret-token-valu")]
    [InlineData("s3cret-token-valueX")]
    [InlineData("S3CRET-TOKEN-VALUE")]
    [InlineData(" s3cret-token-value")]
    [InlineData("s3cret-token-value ")]
    public void Rejects_AnythingElse(string presented) =>
        Assert.False(TokenValidator.IsValid(presented, Configured));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Rejects_AMissingPresentedToken(string? presented) =>
        Assert.False(TokenValidator.IsValid(presented, Configured));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Rejects_Everything_WhenNoTokenIsConfigured(string? configured)
    {
        // The dangerous case: an unset token must never mean "no auth required".
        Assert.False(TokenValidator.IsValid("anything", configured));
        Assert.False(TokenValidator.IsValid(null, configured));
    }
}
