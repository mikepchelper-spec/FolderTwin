using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CopyLab;

public partial class ProductWindow
{
    private VersionBrowser? recoveryBrowser;
    private string? recoveryIndexKey;
    private (CatalogSnapshot Current, CatalogSnapshot? Previous)? recoveryCatalogs;
    private string? recoveryComparisonId;
    private string recoveryDirectory = "";
    private string recoveryQuery = "";
    private int recoveryOffset;
    private const int RecoveryPageSize = 250;

    private string IndexNote() => recoveryIndexKey is null ? " Índice local no disponible. Vista leída desde los catálogos." : "";

    private async Task LoadRecoveryPageAsync()
    {
        ClearRestoreReview();
        bool changes = RecoveryOnlyChanges.IsChecked == true;
        var page = await Task.Run(() =>
        {
            if (recoveryIndexKey is { } key)
            {
                try { return productIndex.Page(key, recoveryDirectory, changes, recoveryQuery, recoveryOffset, RecoveryPageSize); }
                catch (Exception error) when (VersionIndex.IsUnavailable(error)) { recoveryIndexKey = null; }
            }
            recoveryBrowser ??= recoveryCatalogs is { } catalogs ? new(catalogs.Current, catalogs.Previous) :
                throw new InvalidDataException("Selecciona un punto recuperable antes de navegar.");
            return recoveryBrowser.Page(recoveryDirectory, changes, recoveryQuery, recoveryOffset, RecoveryPageSize);
        });
        RecoveryFiles.ItemsSource = page.Entries;
        PreviousRecoveryPageButton.IsEnabled = recoveryOffset > 0;
        NextRecoveryPageButton.IsEnabled = page.HasMore;
        RecoveryParentButton.IsEnabled = recoveryDirectory.Length > 0;
        RecoveryPath.Text = recoveryQuery.Length > 0 ? $"Búsqueda en todas las carpetas · {recoveryQuery}" :
            "Carpeta · " + (recoveryDirectory.Length == 0 ? "Raíz del respaldo" : recoveryDirectory);
        RecoveryPageText.Text = page.Entries.Length == 0 ? "Sin resultados." :
            $"{recoveryOffset + 1:N0}–{recoveryOffset + page.Entries.Length:N0}";
        RefreshRecoverySelection();
        SetStatus((page.Entries.Length == 0 ? "No se encontraron archivos en esta vista." : "Resultados del respaldo.") + IndexNote());
    }

    private void SearchRecoveryClick(object sender, RoutedEventArgs e)
    {
        if (busy || recoveryCatalogs is null) return;
        recoveryQuery = RecoverySearch.Text.Trim();
        recoveryOffset = 0;
        Start(LoadRecoveryPageAsync);
    }

    private void RecoverySearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; SearchRecoveryClick(sender, e); }
    }

    private void RecoveryChangesClick(object sender, RoutedEventArgs e)
    {
        if (busy || recoveryCatalogs is null) return;
        recoveryOffset = 0;
        Start(LoadRecoveryPageAsync);
    }

    private void RecoveryParentClick(object sender, RoutedEventArgs e)
    {
        if (!busy && recoveryDirectory.Length > 0) OpenRecoveryDirectory(VersionBrowser.Parent(recoveryDirectory));
    }

    private void OpenRecoveryFolderClick(object sender, RoutedEventArgs e)
    {
        if (!busy && RecoveryFiles.SelectedItem is VersionEntry { IsDirectory: true } entry) OpenRecoveryDirectory(entry.Path);
    }

    private void OpenRecoveryDirectory(string path)
    {
        recoveryDirectory = path;
        recoveryOffset = 0;
        recoveryQuery = RecoverySearch.Text = "";
        Start(LoadRecoveryPageAsync);
    }

    private void PreviousRecoveryPageClick(object sender, RoutedEventArgs e)
    {
        if (busy || recoveryOffset == 0) return;
        recoveryOffset = Math.Max(0, recoveryOffset - RecoveryPageSize);
        Start(LoadRecoveryPageAsync);
    }

    private void NextRecoveryPageClick(object sender, RoutedEventArgs e)
    {
        if (busy || !NextRecoveryPageButton.IsEnabled) return;
        recoveryOffset = checked(recoveryOffset + RecoveryPageSize);
        Start(LoadRecoveryPageAsync);
    }

    private void RecoveryFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!busy) ClearRestoreReview();
        RefreshRecoverySelection();
    }

    private void RefreshRecoverySelection()
    {
        if (OpenRecoveryFolderButton is null || ReviewFileRestoreButton is null) return;
        OpenRecoveryFolderButton.IsEnabled = recoveryCatalogs is not null && RecoveryFiles.SelectedItem is VersionEntry { IsDirectory: true };
        ReviewFileRestoreButton.IsEnabled = recoveryCatalogs is not null && RecoveryFiles.SelectedItem is VersionEntry { IsDirectory: false } entry &&
            (entry.Change != VersionChange.Removed || recoveryComparisonId is not null);
    }

    private void ReviewFileRestoreClick(object sender, RoutedEventArgs e)
    {
        if (RecoveryFiles.SelectedItem is VersionEntry { IsDirectory: false } entry)
            ReviewRestore(entry.Path, entry.Change == VersionChange.Removed);
    }
}
