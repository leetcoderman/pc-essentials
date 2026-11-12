using System.Security.Cryptography;
using System.Text;

namespace PCEssentials.Core;

/// Keeps a secret (an API key) out of settings.json as plain text: Windows encrypts it for the
/// signed-in user, so the stored value is useless on another account or PC.
public static class Secret
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) { Log.Error("secret protect", ex); return ""; }
    }

    /// Empty when there is nothing stored or it was stored by another user or PC.
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return ""; }
    }
}
