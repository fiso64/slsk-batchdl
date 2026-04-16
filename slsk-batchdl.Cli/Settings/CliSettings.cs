namespace Sldl.Cli;

/// CLI-only settings — control presentation and UI behavior.
/// Not passed to the engine; consumed by the CLI layer before/when constructing the engine.
///
/// The engine interacts with UI through EngineEvents and the SelectAlbumVersion callback.
/// These settings tell the CLI which subscribers to wire up.
///
public class CliSettings
{
    /// When true, the CLI wires up InteractiveModeManager as engine.SelectAlbumVersion.
    public bool InteractiveMode { get; set; }

    /// When true, the CLI skips progress event subscribers.
    public bool NoProgress { get; set; }

    /// When true, the CLI uses JsonStreamProgressReporter instead of the default CLI reporter.
    public bool ProgressJson { get; set; }
}
