using Microsoft.AspNetCore.Identity;
using System.Security.Cryptography;
using System.Text;

namespace DbAccessManager.Security;

public sealed class BcryptPasswordHasher<TUser> : IPasswordHasher<TUser> where TUser : class
{
    private const int WorkFactor = 12;

    public string HashPassword(TUser user, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return BCrypt.Net.BCrypt.HashPassword(PreparePassword(password), WorkFactor);
    }

    public PasswordVerificationResult VerifyHashedPassword(
        TUser user,
        string hashedPassword,
        string providedPassword)
    {
        if (string.IsNullOrWhiteSpace(hashedPassword) || providedPassword is null)
        {
            return PasswordVerificationResult.Failed;
        }

        return BCrypt.Net.BCrypt.Verify(PreparePassword(providedPassword), hashedPassword)
            ? PasswordVerificationResult.Success
            : PasswordVerificationResult.Failed;
    }

    private static string PreparePassword(string password)
    {
        var passwordBytes = Encoding.UTF8.GetBytes($"DbAccessManager:Bcrypt:v1:{password}");
        return Convert.ToBase64String(SHA256.HashData(passwordBytes));
    }
}
