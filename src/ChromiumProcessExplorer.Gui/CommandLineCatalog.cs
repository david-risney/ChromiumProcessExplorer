namespace ChromiumProcessExplorer.Gui;

public sealed record ChromiumCommandLineCatalogEntry(
    string Kind,
    string Name,
    string Argument,
    string Description,
    string Source);

public sealed record CommandLineSuggestionViewModel(
    string Kind,
    string Argument,
    string Description,
    string Origin);
