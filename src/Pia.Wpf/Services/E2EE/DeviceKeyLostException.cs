using System.Security.Cryptography;

namespace Pia.Services.E2EE;

public sealed class DeviceKeyLostException() : CryptographicException(
    "This device's local keys do not match its registered keys.");
