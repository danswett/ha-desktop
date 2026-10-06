using System.Text.Json;

namespace HomeAssistant.Desktop.Services;

/// <summary>One thing a jump list entry can point at.</summary>
public sealed record CatalogueItem(string Label, string Target, JumpTargetKind Kind)
{
    public override string ToString() => Label;
}

/// <summary>
/// The pages and entities this Home Assistant has, as offered to the jump list editor.
///
/// Gathered through the dashboard's own websocket rather than a second connection of
/// the app's own: it needs no extra credentials, and it can only ever offer what the
/// signed-in account can actually reach.
/// </summary>
public sealed class HaCatalogue
{
    public IReadOnlyList<CatalogueItem> Pages { get; init; } = [];

    public IReadOnlyList<CatalogueItem> Entities { get; init; } = [];

    public bool IsEmpty => Pages.Count == 0 && Entities.Count == 0;

    /// <summary>
    /// Asks for every dashboard, each of its views, and every entity.
    ///
    /// A dashboard whose configuration cannot be read - one written in YAML the
    /// websocket will not hand over, or a strategy dashboard with no stored views -
    /// still contributes itself as a destination; only its views are missed.
    /// </summary>
    public const string Script = """
        (async () => {
          const c = await window.hassConnection;
          const conn = c.conn;

          let dashboards = [];
          try { dashboards = await conn.sendMessagePromise({ type: 'lovelace/dashboards/list' }); }
          catch (e) { dashboards = []; }

          const all = [{ url_path: null, title: 'Overview' }].concat(dashboards || []);
          const pages = [];

          for (const d of all) {
            const base = d.url_path ? ('/' + d.url_path) : '/lovelace';
            const name = d.title || d.url_path || 'Overview';
            pages.push({ label: name, target: base });

            try {
              const msg = d.url_path
                ? { type: 'lovelace/config', url_path: d.url_path }
                : { type: 'lovelace/config' };
              const cfg = await conn.sendMessagePromise(msg);
              (cfg.views || []).forEach((v, i) => {
                const seg = (v.path !== undefined && v.path !== null) ? v.path : String(i);
                pages.push({ label: name + ' \u2013 ' + (v.title || seg), target: base + '/' + seg });
              });
            } catch (e) { /* views unavailable for this dashboard */ }
          }

          const states = await conn.sendMessagePromise({ type: 'get_states' });
          const entities = (states || []).map(s => ({
            label: ((s.attributes && s.attributes.friendly_name) || s.entity_id) + '  (' + s.entity_id + ')',
            target: s.entity_id,
          }));

          return { pages, entities };
        })()
        """;

    public static HaCatalogue From(JsonElement root)
    {
        return new HaCatalogue
        {
            Pages = Read(root, "pages", JumpTargetKind.Page),
            Entities = Read(root, "entities", JumpTargetKind.Entity),
        };
    }

    private static List<CatalogueItem> Read(JsonElement root, string name, JumpTargetKind kind)
    {
        var items = new List<CatalogueItem>();

        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var entry in array.EnumerateArray())
        {
            var label = entry.TryGetProperty("label", out var l) ? l.GetString() : null;
            var target = entry.TryGetProperty("target", out var t) ? t.GetString() : null;

            if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(target))
            {
                items.Add(new CatalogueItem(label, target, kind));
            }
        }

        return items;
    }
}
