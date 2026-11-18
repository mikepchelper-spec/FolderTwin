using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace FolderTwin;

public partial class ProductWindow
{
    private sealed class HistoryRow(Guid id, string label) : INotifyPropertyChanged
    {
        public Guid Id { get; } = id;
        public string Label { get; private set; } = label;
        public event PropertyChangedEventHandler? PropertyChanged;

        internal void Update(string value)
        {
            if (Label == value) return;
            Label = value;
            PropertyChanged?.Invoke(this, new(nameof(Label)));
        }

        public override string ToString() => Label;
    }

    private const int HistoryPageSize = 30;
    private readonly ObservableCollection<HistoryRow> historyRows = [];
    private Guid? historyJob;
    private int historyOffset;
    private bool moveHistoryPage;

    private void HistoryFilterChanged(object sender, RoutedEventArgs e)
    {
        if (RunHistoryList is null) return;
        historyOffset = 0;
        moveHistoryPage = true;
        RefreshRuntimeDisplay();
    }

    private void NewerHistoryClick(object sender, RoutedEventArgs e)
    {
        historyOffset = Math.Max(0, historyOffset - HistoryPageSize);
        moveHistoryPage = true;
        RefreshRuntimeDisplay();
    }

    private void OlderHistoryClick(object sender, RoutedEventArgs e)
    {
        historyOffset += HistoryPageSize;
        moveHistoryPage = true;
        RefreshRuntimeDisplay();
    }

    private void RefreshHistoryPage(Guid? job, ProductRunHistory history)
    {
        bool changedJob = historyJob != job;
        if (changedJob) { historyJob = job; historyOffset = 0; }
        var runs = HistoryOnlyIncidents.IsChecked == true ? history.Runs.Where(HasIncident).ToArray() : history.Runs;
        var selected = changedJob || moveHistoryPage ? null : RunHistoryList.SelectedItem as HistoryRow;
        bool hadFocus = RunHistoryList.IsKeyboardFocusWithin;
        // Hold an older page at its first run while new attempts arrive.
        if (!changedJob && !moveHistoryPage && historyOffset > 0 && historyRows.FirstOrDefault() is { } anchor)
        {
            int position = Array.FindIndex(runs, run => run.Intent.Id == anchor.Id);
            if (position >= 0) historyOffset = position;
        }
        historyOffset = Math.Min(historyOffset, Math.Max(0, runs.Length - 1));
        if (selected is not null)
        {
            int position = Array.FindIndex(runs, run => run.Intent.Id == selected.Id);
            if (position >= historyOffset + HistoryPageSize) historyOffset = position - HistoryPageSize + 1;
        }
        var page = runs.Skip(historyOffset).Take(HistoryPageSize).ToArray();
        RunHistoryList.ItemsSource ??= historyRows;
        for (int index = 0; index < page.Length; index++)
        {
            var run = page[index];
            string label = HistoryLabel(run);
            var row = historyRows.FirstOrDefault(item => item.Id == run.Intent.Id);
            if (row is null) historyRows.Insert(index, new(run.Intent.Id, label));
            else
            {
                int previous = historyRows.IndexOf(row);
                if (previous != index) historyRows.Move(previous, index);
                row.Update(label);
            }
        }
        while (historyRows.Count > page.Length) historyRows.RemoveAt(historyRows.Count - 1);
        if (selected is not null)
        {
            RunHistoryList.SelectedItem = historyRows.FirstOrDefault(row => row.Id == selected.Id);
            if (hadFocus && RunHistoryList.SelectedItem is { } focused)
            {
                RunHistoryList.UpdateLayout();
                if (RunHistoryList.ItemContainerGenerator.ContainerFromItem(focused) is ListBoxItem item)
                    RestoreKeyboardFocus(item);
            }
        }
        NewerHistoryButton.IsEnabled = historyOffset > 0;
        OlderHistoryButton.IsEnabled = historyOffset + page.Length < runs.Length;
        HistoryPageText.Text = runs.Length == 0 ? "Sin intentos en esta vista." :
            $"{historyOffset + 1:N0}–{historyOffset + page.Length:N0} de {runs.Length:N0}";
        moveHistoryPage = false;
    }

    private static bool HasIncident(ProductRunView run) => !run.Running &&
        (run.Result is null || run.Result.State is not (ProductRunState.Confirmed or ProductRunState.Unchanged) ||
        run.Result.Degraded || run.Mirror is { ResumePending: true } or { Conflicts: > 0 } or
            { RequiresEmptyReview: true } or { RequiresMassReview: true } ||
        run.Intent.Operation is ProductRunOperation.ReviewAbandon or ProductRunOperation.AbandonVersion);

    private static string HistoryLabel(ProductRunView run) =>
        $"Intento {run.Intent.Sequence} · {run.Intent.StartedUtc.ToLocalTime():g} · " +
        (run.Result is { } result ? ResultText(run, result) : run.Running ? "En ejecución" : "Interrumpido") +
        (run.Restore is { } restore && run.Result?.State != ProductRunState.Confirmed ?
            "\nCarpeta revisada · " + Path.Combine(restore.Target.Parent.VolumePath, restore.Target.FolderName) : "");
}
