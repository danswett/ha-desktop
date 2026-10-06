namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Counts notifications the user has not seen yet.
///
/// Tagged notifications are tracked by tag rather than counted, so that a sensor which
/// re-sends under the same tag replaces its predecessor instead of inflating the badge.
/// That matches what the toast itself does, and what the phones do: one doorbell is one
/// badge however many times it fires. Untagged notifications have nothing to collapse
/// on, so they simply accumulate.
/// </summary>
public sealed class UnseenNotifications
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _tags = new(StringComparer.OrdinalIgnoreCase);
    private int _untagged;

    /// <summary>Raised whenever the count changes. Not raised on the UI thread.</summary>
    public event Action<int>? CountChanged;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _tags.Count + _untagged;
            }
        }
    }

    public void Add(string? tag)
    {
        int count;
        lock (_gate)
        {
            if (string.IsNullOrEmpty(tag))
            {
                _untagged++;
            }
            else if (!_tags.Add(tag))
            {
                return;
            }

            count = _tags.Count + _untagged;
        }

        CountChanged?.Invoke(count);
    }

    /// <summary>
    /// Drops a notification the user has dealt with, or that Home Assistant has cleared.
    /// </summary>
    public void Remove(string? tag)
    {
        int count;
        lock (_gate)
        {
            if (string.IsNullOrEmpty(tag))
            {
                // Without a tag there is no way to tell which one went, so take the
                // newest. Undercounting is better than a badge that will not clear.
                if (_untagged == 0)
                {
                    return;
                }

                _untagged--;
            }
            else if (!_tags.Remove(tag))
            {
                return;
            }

            count = _tags.Count + _untagged;
        }

        CountChanged?.Invoke(count);
    }

    /// <summary>Called when the user looks at the dashboard, which reads everything.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_tags.Count == 0 && _untagged == 0)
            {
                return;
            }

            _tags.Clear();
            _untagged = 0;
        }

        CountChanged?.Invoke(0);
    }
}
