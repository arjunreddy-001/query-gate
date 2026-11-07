using DbAccessManager.Security;

namespace DbAccessManager.Tests;

public sealed class BcryptPasswordHasherTests
{
    [Fact]
    public void HashPassword_StoresVerifiableBcryptHash()
    {
        var hasher = new BcryptPasswordHasher<object>();
        const string password = "Strong-password-123!";

        var hash = hasher.HashPassword(new object(), password);

        Assert.NotEqual(password, hash);
        Assert.Equal(
            Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(new object(), hash, password));
        Assert.Equal(
            Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(new object(), hash, "Wrong-password-123!"));
    }

    [Fact]
    public void HashPassword_UsesEntirePasswordBeyondBcryptInputLimit()
    {
        var hasher = new BcryptPasswordHasher<object>();
        var password = new string('a', 100) + "first";
        var differentPassword = new string('a', 100) + "second";
        var hash = hasher.HashPassword(new object(), password);

        Assert.Equal(
            Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(new object(), hash, password));
        Assert.Equal(
            Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(new object(), hash, differentPassword));
    }
}
