using Sockseek.Core.Models;
using System.ComponentModel;

namespace Sockseek.Core.Jobs;

public abstract record DirectoryExecutionState
{
    private DirectoryExecutionState() { }

    public sealed record Unresolved : DirectoryExecutionState;
    public sealed record Resolving : DirectoryExecutionState;
    public sealed record Planned(int AttemptNumber) : DirectoryExecutionState;
    public sealed record Transferring(int AttemptNumber) : DirectoryExecutionState;
}

/// <summary>One immutable plan and the file jobs materialized for that plan.</summary>
public sealed class DirectoryTransferAttempt
{
    private IReadOnlyList<FileDownloadJob> fileJobs = Array.Empty<FileDownloadJob>();
    private bool childrenMaterialized;

    internal DirectoryTransferAttempt(int attemptNumber, DirectoryTransferPlan plan)
    {
        if (attemptNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        ArgumentNullException.ThrowIfNull(plan);
        AttemptNumber = attemptNumber;
        Plan = plan;
    }

    public int AttemptNumber { get; }
    public DirectoryTransferPlan Plan { get; }
    public IReadOnlyList<FileDownloadJob> FileJobs => fileJobs;
    public bool ChildrenMaterialized => childrenMaterialized;

    internal void MaterializeChildren(IEnumerable<FileDownloadJob> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (childrenMaterialized)
            throw new InvalidOperationException("Directory attempt children have already been materialized.");

        var owned = children.ToArray();
        if (owned.Any(child => child is null))
            throw new ArgumentException("Directory attempt children cannot contain null jobs.", nameof(children));
        if (owned.Length != Plan.Entries.Count)
            throw new ArgumentException("Directory attempt must materialize one file job per plan entry.", nameof(children));

        fileJobs = Array.AsReadOnly(owned);
        childrenMaterialized = true;
    }
}

/// <summary>
/// Observable lifecycle shared by directory-shaped downloads. It owns state and
/// attempt association, but no search, retrieval, placement, or finalization.
/// </summary>
public abstract class DirectoryDownloadJob : Job
{
    private DirectoryExecutionState directoryState = new DirectoryExecutionState.Unresolved();
    private DirectoryTransferAttempt? activeAttempt;
    private readonly List<FileDownloadJob> fileJobs = [];
    private readonly List<FileDownloadJob> supplementalFileJobs = [];
    private int attemptNumber;
    private string? downloadPath;

    public DirectoryExecutionState DirectoryState => directoryState;
    public DirectoryTransferAttempt? ActiveAttempt => activeAttempt;
    /// <summary>
    /// All file jobs currently owned by this directory. The active attempt owns the
    /// exact one-per-plan-entry prefix; a specialization may append explicit
    /// post-plan work, such as album art selected during music finalization.
    /// </summary>
    public IReadOnlyList<FileDownloadJob> FileJobs => fileJobs;
    public long BytesTransferred => FileJobs.Sum(file => file.BytesTransferred);
    public long TotalKnownBytes => (ActiveAttempt?.Plan.TotalKnownBytes ?? 0)
        + supplementalFileJobs.Sum(file => file.FileSize ?? 0);
    public double Progress => TotalKnownBytes > 0
        ? Math.Clamp((double)BytesTransferred / TotalKnownBytes, 0, 1)
        : 0;

    public string? DownloadPath
    {
        get => downloadPath;
        set
        {
            if (downloadPath == value)
                return;
            downloadPath = value;
            OnPropertyChanged();
        }
    }

    public void BeginDirectoryResolution()
    {
        if (directoryState is not DirectoryExecutionState.Unresolved)
            throw new InvalidOperationException("Only an unresolved directory can begin resolution.");
        SetDirectoryState(new DirectoryExecutionState.Resolving());
    }

    public DirectoryTransferAttempt BeginDirectoryAttempt(DirectoryTransferPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        DirectoryTransferAdmissionPolicy.Default.Validate(plan);
        if (directoryState is DirectoryExecutionState.Transferring
            && (activeAttempt == null || fileJobs.Any(child => !child.IsTerminal)))
        {
            throw new InvalidOperationException("A directory with active transfers cannot change plans.");
        }

        ClearDirectoryChildren();
        activeAttempt = new DirectoryTransferAttempt(checked(++attemptNumber), plan);
        OnPropertyChanged(nameof(ActiveAttempt));
        OnPropertyChanged(nameof(FileJobs));
        SetDirectoryState(new DirectoryExecutionState.Planned(activeAttempt.AttemptNumber));
        return activeAttempt;
    }

    public void MaterializeDirectoryChildren(IEnumerable<FileDownloadJob> children)
    {
        if (activeAttempt is null || directoryState is not DirectoryExecutionState.Planned)
            throw new InvalidOperationException("A planned directory attempt is required before materializing children.");
        activeAttempt.MaterializeChildren(children);
        foreach (var child in activeAttempt.FileJobs)
            AttachDirectoryChild(child);
        NotifyDirectoryChildrenChanged();
    }

    public void BeginDirectoryTransfer()
    {
        if (activeAttempt is null
            || directoryState is not DirectoryExecutionState.Planned planned
            || planned.AttemptNumber != activeAttempt.AttemptNumber
            || !activeAttempt.ChildrenMaterialized)
        {
            throw new InvalidOperationException("A materialized planned attempt is required before transfer.");
        }

        SetDirectoryState(new DirectoryExecutionState.Transferring(activeAttempt.AttemptNumber));
    }

    public void ResetDirectoryResolution()
    {
        if (directoryState is DirectoryExecutionState.Transferring)
            throw new InvalidOperationException("A transferring directory cannot be reset.");
        ClearDirectoryChildren();
        activeAttempt = null;
        OnPropertyChanged(nameof(ActiveAttempt));
        OnPropertyChanged(nameof(FileJobs));
        SetDirectoryState(new DirectoryExecutionState.Unresolved());
    }

    private void ChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileDownloadJob.BytesTransferred)
            or nameof(FileDownloadJob.FileSize)
            or nameof(FileDownloadJob.Progress))
        {
            OnPropertyChanged(nameof(BytesTransferred));
            OnPropertyChanged(nameof(TotalKnownBytes));
            OnPropertyChanged(nameof(Progress));
        }
    }

    /// <summary>
    /// Registers work deliberately added by a semantic specialization after the
    /// immutable directory plan has been materialized.
    /// </summary>
    protected void AddSupplementalDirectoryChild(FileDownloadJob child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (activeAttempt is null || !activeAttempt.ChildrenMaterialized)
            throw new InvalidOperationException("Supplemental directory work requires a materialized active attempt.");
        if (fileJobs.Contains(child))
            return;

        supplementalFileJobs.Add(child);
        AttachDirectoryChild(child);
        NotifyDirectoryChildrenChanged();
    }

    protected void ClearDirectoryChildren()
    {
        if (directoryState is DirectoryExecutionState.Transferring
            && fileJobs.Any(child => !child.IsTerminal))
        {
            throw new InvalidOperationException("Active directory children cannot be discarded.");
        }

        foreach (var child in fileJobs)
            child.PropertyChanged -= ChildPropertyChanged;
        fileJobs.Clear();
        supplementalFileJobs.Clear();
        NotifyDirectoryChildrenChanged();
    }

    private void AttachDirectoryChild(FileDownloadJob child)
    {
        fileJobs.Add(child);
        child.PropertyChanged += ChildPropertyChanged;
    }

    private void NotifyDirectoryChildrenChanged()
    {
        OnPropertyChanged(nameof(FileJobs));
        OnPropertyChanged(nameof(BytesTransferred));
        OnPropertyChanged(nameof(TotalKnownBytes));
        OnPropertyChanged(nameof(Progress));
    }

    private void SetDirectoryState(DirectoryExecutionState value)
    {
        directoryState = value;
        OnPropertyChanged(nameof(DirectoryState));
    }

    public override void SetDone()
        => SetDone(downloadPath: null);

    public void SetDone(string? downloadPath)
    {
        if (downloadPath != null)
            DownloadPath = downloadPath;
        base.SetDone();
    }

    public override void SetAlreadyExists()
        => SetAlreadyExists(path: null);

    public void SetAlreadyExists(string? path)
    {
        if (path != null)
            DownloadPath = path;
        base.SetAlreadyExists();
    }
}
