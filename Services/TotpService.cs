using OtpNet;

namespace OfficeAschiApi.Services;

public class TotpService
{
    /// <summary>
    /// Validates a TOTP code against a base32-encoded secret key.
    /// Uses a 30-second window with ±1 step tolerance.
    /// </summary>
    public bool ValidateTotp(string base32Secret, string totpCode)
    {
        var secretBytes = Base32Encoding.ToBytes(base32Secret);
        var totp = new Totp(secretBytes, step: 30, totpSize: 6);
        return totp.VerifyTotp(totpCode, out _, new VerificationWindow(previous: 1, future: 1));
    }

    /// <summary>
    /// Generates a new random base32 secret key for TOTP setup.
    /// </summary>
    public string GenerateSecret()
    {
        var key = KeyGeneration.GenerateRandomKey(20);
        return Base32Encoding.ToString(key);
    }
}
