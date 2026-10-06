using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Secrets, encrypted at rest with DPAPI under the current user.
///
/// Kept apart from <see cref="AppSettings"/> deliberately. Settings are the user's own
/// preferences and there is value in their being readable and editable by hand; these
/// are bearer credentials, and a readable file holding a refresh token is a standing
/// invitation. DPAPI's CurrentUser scope ties the ciphertext to the Windows account, so
/// a copied file is useless on another machine or to another user on this one.
///
/// Not a secret store in the hardened sense: anything running as this user can ask
/// DPAPI to decrypt it, exactly as it could read the browser profile sitting beside it.
/// The bar is "not sitting in plain text", which is the bar the data actually needs.
/// </summary>
public sealed class ProtectedStore
{
    private const string FileName = "secrets.dat";

    // Ties the ciphertext to this app, so another DPAPI consumer running as the same
    // user cannot decrypt it by accident.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HomeAssistant.Desktop/secrets/v1");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly Lock _gate = new();
    private readonly string _path;

    private Dictionary<string, string> _values;

    public ProtectedStore(string? directory = null)
    {
        _path = Path.Combine(directory ?? AppSettings.DataDirectory, FileName);
        _values = Load();
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value) ? value : null;
        }
    }

    /// <summary>Stores a value, or removes it when <paramref name="value"/> is null or empty.</summary>
    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(value))
            {
                if (!_values.Remove(key))
                {
                    return;
                }
            }
            else
            {
                if (_values.TryGetValue(key, out var existing) && existing == value)
                {
                    return;
                }

                _values[key] = value;
            }

            Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_values.Count == 0)
            {
                return;
            }

            _values.Clear();
            Save();
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var plaintext = ProtectedData.Unprotect(
                File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext) ?? [];
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            // A store that cannot be read is a store that has to be rebuilt: the user
            // signs in again. Far better than refusing to start.
            Log.Error("secrets", "could not read the secret store; starting empty", ex);
            return [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var plaintext = JsonSerializer.SerializeToUtf8Bytes(_values, SerializerOptions);
            var ciphertext = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

            // Write beside the target and move, so an interrupted write cannot leave a
            // half-written store that reads as corrupt and signs the user out.
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, ciphertext);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Log.Error("secrets", "could not write the secret store", ex);
        }
    }
}
