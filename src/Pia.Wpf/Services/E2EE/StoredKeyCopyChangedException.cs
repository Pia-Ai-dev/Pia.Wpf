using System.Security.Cryptography;

namespace Pia.Services.E2EE;

public sealed class StoredKeyCopyChangedException() : CryptographicException(
    "This device's stored copy of the key was changed on the server.");
