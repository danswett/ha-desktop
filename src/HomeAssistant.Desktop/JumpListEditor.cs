using HomeAssistant.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace HomeAssistant.Desktop;

/// <summary>
/// The jump list editor: a row per taskbar entry, each naming a destination and what
/// choosing it should do.
///
/// Built in code rather than markup because the rows are a variable-length list over a
/// catalogue that is only known once the dashboard has answered, and because the rest
/// of this app's dialogs are built the same way.
/// </summary>
internal sealed class JumpListEditor
{
    private readonly List<Row> _rows = [];
    private readonly StackPanel _rowHost = new() { Spacing = 8 };
    private readonly Button _add;
    private readonly TextBlock _count;
    private readonly TextBlock _status;

    /// <summary>
    /// What the rows offer as suggestions. Settable because the dashboard is asked for
    /// it only when the section is first opened, so the editor exists before the answer
    /// does; rows read it on each keystroke rather than capturing it.
    /// </summary>
    internal HaCatalogue Catalogue { get; set; } = new();

    internal JumpListEditor(IEnumerable<JumpListSlot> existing)
    {
        _add = new Button { Content = "Add an entry", Margin = new Thickness(0, 12, 0, 0) };
        _add.Click += (_, _) => AddRow(new JumpListSlot());

        _count = new TextBlock
        {
            Opacity = 0.7,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        _status = new TextBlock
        {
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text = "Reading your dashboards and entities\u2026",
        };

        foreach (var slot in existing)
        {
            AddRow(Clone(slot));
        }

        UpdateCount();
    }

    /// <summary>Reports what the dashboard offered, or that it could not be asked.</summary>
    internal void SetCatalogue(HaCatalogue catalogue)
    {
        Catalogue = catalogue;
        _status.Text = catalogue.IsEmpty
            ? "Could not read your dashboards - the dashboard has to be loaded. "
              + "You can still type a path or an entity id by hand."
            : $"Offering {catalogue.Pages.Count} page(s) and {catalogue.Entities.Count} entities.";
    }

    internal UIElement Build()
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "These appear when you right-click the app on the taskbar.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(_status);
        panel.Children.Add(_rowHost);
        panel.Children.Add(_add);
        panel.Children.Add(_count);
        return panel;
    }

    /// <summary>The configured entries, in order, dropping any left incomplete.</summary>
    internal List<JumpListSlot> Result() =>
        _rows.Select(r => r.ToSlot()).Where(s => s.IsUsable).ToList();

    private void AddRow(JumpListSlot slot)
    {
        if (_rows.Count >= JumpList.MaxSlots)
        {
            return;
        }

        var row = new Row(slot, () => Catalogue);
        row.RemoveRequested += () =>
        {
            _rows.Remove(row);
            _rowHost.Children.Remove(row.Element);
            UpdateCount();
        };

        _rows.Add(row);
        _rowHost.Children.Add(row.Element);
        UpdateCount();
    }

    private void UpdateCount()
    {
        _add.IsEnabled = _rows.Count < JumpList.MaxSlots;
        _count.Text = _rows.Count >= JumpList.MaxSlots
            ? $"Windows shows at most {JumpList.MaxSlots} entries, which is as many as there are."
            : $"{_rows.Count} of {JumpList.MaxSlots} entries.";
    }

    private static JumpListSlot Clone(JumpListSlot slot) => new()
    {
        Title = slot.Title,
        Kind = slot.Kind,
        Target = slot.Target,
        Action = slot.Action,
    };

    private sealed class Row
    {
        private readonly TextBox _title;
        private readonly ComboBox _kind;
        private readonly AutoSuggestBox _target;
        private readonly ComboBox _action;
        private readonly Func<HaCatalogue> _catalogue;

        internal event Action? RemoveRequested;

        internal Grid Element { get; }

        internal Row(JumpListSlot slot, Func<HaCatalogue> catalogue)
        {
            _catalogue = catalogue;

            _title = new TextBox
            {
                Text = slot.Title,
                PlaceholderText = "Name on the menu",
                MinWidth = 120,
            };
            AutomationProperties.SetName(_title, "Entry name");

            _kind = new ComboBox { MinWidth = 104 };
            _kind.Items.Add("Page");
            _kind.Items.Add("Entity");
            _kind.SelectedIndex = slot.Kind == JumpTargetKind.Entity ? 1 : 0;
            AutomationProperties.SetName(_kind, "Entry kind");

            _target = new AutoSuggestBox
            {
                Text = slot.Target,
                PlaceholderText = "Start typing",
                MinWidth = 160,
            };
            AutomationProperties.SetName(_target, "Entry target");
            _target.TextChanged += OnTargetTextChanged;
            _target.SuggestionChosen += (_, e) =>
            {
                if (e.SelectedItem is CatalogueItem item)
                {
                    _target.Text = item.Target;

                    // Picking from the list settles the kind too, so the two cannot
                    // disagree about what the target means.
                    _kind.SelectedIndex = item.Kind == JumpTargetKind.Entity ? 1 : 0;

                    if (string.IsNullOrWhiteSpace(_title.Text))
                    {
                        _title.Text = item.Label.Split("  (")[0];
                    }
                }
            };

            _action = new ComboBox { MinWidth = 116 };
            _action.Items.Add("Open it");
            _action.Items.Add("Toggle it");
            _action.SelectedIndex = slot.Action == JumpTargetAction.Perform ? 1 : 0;
            AutomationProperties.SetName(_action, "Entry action");

            _kind.SelectionChanged += (_, _) => UpdateActionState();
            UpdateActionState();

            var remove = new Button { Content = "\uE711", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") };
            AutomationProperties.SetName(remove, "Remove entry");
            ToolTipService.SetToolTip(remove, "Remove this entry");
            remove.Click += (_, _) => RemoveRequested?.Invoke();

            Element = new Grid { ColumnSpacing = 8 };
            for (var i = 0; i < 5; i++)
            {
                Element.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = i switch
                    {
                        0 => new GridLength(1.1, GridUnitType.Star),
                        2 => new GridLength(1.5, GridUnitType.Star),
                        _ => GridLength.Auto,
                    },
                });
            }

            Add(_title, 0);
            Add(_kind, 1);
            Add(_target, 2);
            Add(_action, 3);
            Add(remove, 4);
        }

        private void Add(FrameworkElement element, int column)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(element, column);
            Element.Children.Add(element);
        }

        /// <summary>
        /// Only an entity can be acted on. A page has nothing to toggle, so the choice
        /// is disabled rather than quietly ignored.
        /// </summary>
        private void UpdateActionState()
        {
            var isEntity = _kind.SelectedIndex == 1;
            _action.IsEnabled = isEntity;
            if (!isEntity)
            {
                _action.SelectedIndex = 0;
            }

            OnTargetTextChanged(_target, new AutoSuggestBoxTextChangedEventArgs());
        }

        private void OnTargetTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            var source = _kind.SelectedIndex == 1 ? _catalogue().Entities : _catalogue().Pages;
            var text = sender.Text ?? string.Empty;

            sender.ItemsSource = source
                .Where(i => text.Length == 0
                    || i.Label.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || i.Target.Contains(text, StringComparison.OrdinalIgnoreCase))
                .Take(40)
                .ToList();
        }

        internal JumpListSlot ToSlot() => new()
        {
            Title = _title.Text.Trim(),
            Kind = _kind.SelectedIndex == 1 ? JumpTargetKind.Entity : JumpTargetKind.Page,
            Target = (_target.Text ?? string.Empty).Trim(),
            Action = _action.SelectedIndex == 1 ? JumpTargetAction.Perform : JumpTargetAction.Open,
        };
    }
}
