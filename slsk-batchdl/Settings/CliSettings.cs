namespace Settings;

/// CLI-only settings — control presentation and UI behavior.
/// Not passed to the engine; consumed by the CLI layer before/when constructing the engine.
///
/// The engine interacts with UI through IProgressReporter and the SelectAlbumVersion callback.
/// These settings tell the CLI which implementations to wire up.
///
/// Note: will eventually live in a separate CLI project once the library/CLI split is made.
public class CliSettings
{
    /// When true, the CLI wires up InteractiveModeManager as engine.SelectAlbumVersion.
    public bool InteractiveMode { get; set; }

    /// When true, the CLI uses NullProgressReporter (no console output).
    public bool NoProgress { get; set; }

    /// When true, the CLI uses JsonProgressReporter instead of the default CLI reporter.
    public bool ProgressJson { get; set; }
}
