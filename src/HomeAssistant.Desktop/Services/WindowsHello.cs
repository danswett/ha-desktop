using Windows.Security.Credentials.UI;

namespace HomeAssistant.Desktop.Services;

/// <summary>Why a verification could not be asked for, or did not succeed.</summary>
public enum HelloOutcome
{
    Verified,
    Declined,
    Unavailable,
}

/// <summary>
/// Windows Hello, used as a lock in front of the dashboard.
///
/// This is deliberately not sold as a security boundary. The app's refresh token is
/// already protected by DPAPI, which is bound to the Windows account, so anyone who
/// is inside this user's session can read it with or without a lock here. What this
/// does buy is the ordinary case it is meant for: a machine left unattended and
/// unlocked, with the house on screen.
///
/// The desktop flavour of the API is the interop one. UserConsentVerifier's own
/// RequestVerificationAsync needs a CoreWindow, which a Win32 app does not have, so
/// the verification has to be attached to a window handle instead.
/// </summary>
public static class WindowsHello
{
    /// <summary>
    /// Whether this machine could verify right now. Checked before the setting can be
    /// turned on, so that enabling a lock cannot be the thing that locks you out.
    /// </summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            return await UserConsentVerifier.CheckAvailabilityAsync()
                == UserConsentVerifierAvailability.Available;
        }
        catch (Exception ex)
        {
            Log.Warn("hello", $"could not check for Windows Hello: {ex.Message}");
            return false;
        }
    }

    public static async Task<HelloOutcome> VerifyAsync(IntPtr hwnd, string message)
    {
        try
        {
            var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(hwnd, message);

            switch (result)
            {
                case UserConsentVerificationResult.Verified:
                    return HelloOutcome.Verified;

                // Nothing on this machine can answer, so holding the dashboard back
                // would lock the user out of their own app rather than protect it.
                case UserConsentVerificationResult.DeviceNotPresent:
                case UserConsentVerificationResult.NotConfiguredForUser:
                case UserConsentVerificationResult.DisabledByPolicy:
                    Log.Warn("hello", $"Windows Hello is not usable here ({result})");
                    return HelloOutcome.Unavailable;

                default:
                    Log.Info("hello", $"verification did not succeed ({result})");
                    return HelloOutcome.Declined;
            }
        }
        catch (Exception ex)
        {
            Log.Error("hello", "could not ask for Windows Hello", ex);
            return HelloOutcome.Unavailable;
        }
    }
}
