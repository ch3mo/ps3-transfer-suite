using PS3Transfer.App.Mvvm;
using PS3Transfer.Core;

namespace PS3Transfer.App.ViewModels;

public sealed class KindOption
{
    public KindOption(ContentKind? kind, string label, string? romFolder = null)
    {
        Kind = kind;
        Label = label;
        RomFolder = romFolder;
    }

    public ContentKind? Kind { get; }
    public string Label { get; }
    public string? RomFolder { get; }
}

public sealed class QueueItemViewModel : ObservableObject
{
    private readonly Action _changed;
    private KindOption _selectedKind;
    private TransferPlan? _plan;
    private bool _sticky;
    private string _status = "";
    private string _tone = "Muted";
    private double _fraction;

    public QueueItemViewModel(InspectedSource source, Action changed)
    {
        Source = source;
        _changed = changed;
        KindOptions = BuildKinds(source);
        _selectedKind = KindOptions[0];
    }

    public InspectedSource Source { get; }
    public IReadOnlyList<KindOption> KindOptions { get; }
    public TransferPlan? Plan => _plan;
    public bool Replace { get; private set; }
    public bool IsRunning { get; private set; }
    public long SessionBytes { get; private set; }
    public bool CanRetype => !IsRunning;

    public KindOption SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedKind))
                return;
            _selectedKind = value;
            _sticky = false;
            Raise(nameof(SelectedKind));
            _changed();
        }
    }

    public string Title => string.IsNullOrWhiteSpace(Source.Title) ? Source.Label : Source.Title;
    public string Meta => BuildMeta();
    public string Destination => _plan?.DisplayPath ?? "";
    public string Status => _status;
    public string Tone => _tone;
    public double Fraction => _fraction;
    public bool ShowReplace => _plan is { Action: TransferAction.Skip, Disk: ExistState.Conflict };

    public void ArmReplace()
    {
        Replace = true;
        _sticky = false;
        _changed();
    }

    public void Apply(TransferPlan plan)
    {
        _plan = plan;
        if (!_sticky)
        {
            _status = plan.StatusLine;
            _tone = ToneFor(plan);
            _fraction = plan.Action == TransferAction.Skip && plan.Disk == ExistState.Complete ? 1 : 0;
        }

        Raise(nameof(Plan));
        Raise(nameof(Meta));
        Raise(nameof(Destination));
        Raise(nameof(Status));
        Raise(nameof(Tone));
        Raise(nameof(Fraction));
        Raise(nameof(ShowReplace));
    }

    public void Hold(string message)
    {
        if (_sticky)
            return;
        _plan = null;
        _status = message;
        _tone = "Bad";
        _fraction = 0;
        Raise(nameof(Plan));
        Raise(nameof(Destination));
        Raise(nameof(Status));
        Raise(nameof(Tone));
        Raise(nameof(Fraction));
        Raise(nameof(ShowReplace));
    }

    public void BeginRun()
    {
        IsRunning = true;
        SessionBytes = 0;
        Raise(nameof(IsRunning));
        Raise(nameof(CanRetype));
    }

    public void EndRun()
    {
        IsRunning = false;
        SessionBytes = 0;
        Raise(nameof(IsRunning));
        Raise(nameof(CanRetype));
    }

    public void Report(TransferProgress progress)
    {
        SessionBytes = progress.SessionBytes;
        _status = progress.Detail;
        _tone = "Accent";
        _fraction = progress.FileLength <= 0 ? 0 : Math.Clamp((double)progress.FilePosition / progress.FileLength, 0, 1);
        Raise(nameof(Status));
        Raise(nameof(Tone));
        Raise(nameof(Fraction));
    }

    public void Fail(string error)
    {
        _sticky = true;
        _status = error;
        _tone = "Bad";
        Raise(nameof(Status));
        Raise(nameof(Tone));
    }

    public void Warn(string warning)
    {
        _sticky = true;
        _status = warning;
        _tone = "Warn";
        Raise(nameof(Status));
        Raise(nameof(Tone));
    }

    public void ClearSticky() => _sticky = false;

    private string BuildMeta()
    {
        var kind = KindText();
        var size = ByteSize.Format(Source.Length);
        if (_plan is { Splits: true })
            return kind + " · " + size + " · " + _plan.PartCount + " parts";
        if (Source.Parts.Count > 1)
            return kind + " · " + size + " · " + Source.Parts.Count + " source parts, joined on copy";
        return kind + " · " + size;
    }

    private static string ToneFor(TransferPlan plan) => plan.Action switch
    {
        TransferAction.Blocked => "Bad",
        TransferAction.Skip when plan.Disk == ExistState.Complete => "Good",
        TransferAction.Skip or TransferAction.Resume or TransferAction.Replace => "Warn",
        _ => "Muted",
    };

    private string KindText()
    {
        var kind = SelectedKind.Kind ?? Source.Kind;
        if (kind != ContentKind.Rom)
            return Ps3Layout.KindLabel(kind);
        var system = RomLayout.Find(SelectedKind.RomFolder ?? Source.RomFolder);
        return system is null ? "ROM" : system.Label;
    }

    private static IReadOnlyList<KindOption> BuildKinds(InspectedSource source)
    {
        var list = new List<KindOption>();
        if (source.Kind == ContentKind.Rom)
        {
            list.Add(new KindOption(null, "Detected"));
            foreach (var system in RomLayout.Systems)
                list.Add(new KindOption(ContentKind.Rom, system.Label, system.Folder));
            return list;
        }

        if (source.Kind != ContentKind.Unknown)
            list.Add(new KindOption(null, "Detected"));
        if (source.Kind is ContentKind.Rap or ContentKind.Edat)
            return list.Count == 0 ? [new KindOption(null, "Detected")] : list;
        if (source.AllowMemberSplit)
        {
            list.Add(new KindOption(ContentKind.Ps3Folder, "PS3 folder"));
            return list;
        }

        if (source.Kind == ContentKind.PkgFolder)
        {
            list.Add(new KindOption(ContentKind.PkgFolder, "PKG folder"));
            return list;
        }

        if (source.Group == GroupKind.Tree)
        {
            list.Add(new KindOption(ContentKind.Ps1Disc, "PS1"));
            list.Add(new KindOption(ContentKind.Ps2Iso, "PS2 ISO"));
            list.Add(new KindOption(ContentKind.PspIso, "PSP ISO"));
            return list;
        }

        list.Add(new KindOption(ContentKind.Ps3Iso, "PS3 ISO"));
        list.Add(new KindOption(ContentKind.Ps2Iso, "PS2 ISO"));
        list.Add(new KindOption(ContentKind.Ps1Disc, "PS1"));
        list.Add(new KindOption(ContentKind.PspIso, "PSP ISO"));
        list.Add(new KindOption(ContentKind.Pkg, "PKG"));
        list.Add(new KindOption(ContentKind.BdIso, "Blu-ray"));
        list.Add(new KindOption(ContentKind.DvdIso, "DVD"));
        if (source.Kind == ContentKind.Unknown)
            list.Insert(0, new KindOption(null, "Choose type"));
        if (list.Count == 0)
            list.Add(new KindOption(null, "Detected"));
        return list;
    }
}

public sealed class LibraryRowViewModel
{
    public LibraryRowViewModel(LibraryEntry entry) => Entry = entry;

    public LibraryEntry Entry { get; }
    public string Title => Entry.Title;
    public string Meta => Entry.Category + " · " + ByteSize.Format(Entry.Bytes)
        + (Entry.Split ? " · " + Entry.Parts + " parts" : "")
        + (Entry.OfferMerge ? " · split PKG, merge to install" : "")
        + (string.IsNullOrEmpty(Entry.Note) ? "" : " · " + Entry.Note);
    public string Path => Entry.RelativePath;
}
