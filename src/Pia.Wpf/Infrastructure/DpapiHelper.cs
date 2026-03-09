using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Pia.Infrastructure;

public class DpapiHelper
{
    private readonly ILogger<DpapiHelper> _logger;
    private static readonly byte[] Entropy = "Pia.ApiKey.Entropy"u8.ToArray();

    public DpapiHelper(ILogger<DpapiHelper> logger)
    {
        _logger = logger;
    }

    public virtual string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encryptedBytes);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Failed to encrypt data using DPAPI");
            return string.Empty;
        }
    }

    public virtual string Decrypt(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText))
            return string.Empty;

        try
        {
            var encryptedBytes = Convert.FromBase64String(encryptedText);
            var plainBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Failed to decrypt data using DPAPI");
            return string.Empty;
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Invalid base64 format in encrypted data");
            return string.Empty;
        }
    }
}
