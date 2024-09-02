
using Data;

using SearchResponse = Soulseek.SearchResponse;


public class FileConditions
{
    public int LengthTolerance = -1;
    public int MinBitrate = -1;
    public int MaxBitrate = -1;
    public int MinSampleRate = -1;
    public int MaxSampleRate = -1;
    public int MinBitDepth = -1;
    public int MaxBitDepth = -1;
    public bool StrictTitle = false;
    public bool StrictArtist = false;
    public bool StrictAlbum = false;
    public string[] Formats = Array.Empty<string>();
    public string[] BannedUsers = Array.Empty<string>();
    public bool StrictStringDiacrRemove = true;
    public bool AcceptNoLength = true;
    public bool AcceptMissingProps = true;

    public FileConditions() { }

    public FileConditions(FileConditions other)
    {
        LengthTolerance = other.LengthTolerance;
        MinBitrate = other.MinBitrate;
        MaxBitrate = other.MaxBitrate;
        MinSampleRate = other.MinSampleRate;
        MaxSampleRate = other.MaxSampleRate;
        AcceptNoLength = other.AcceptNoLength;
        StrictArtist = other.StrictArtist;
        StrictTitle = other.StrictTitle;
        MinBitDepth = other.MinBitDepth;
        MaxBitDepth = other.MaxBitDepth;
        Formats = other.Formats.ToArray();
        BannedUsers = other.BannedUsers.ToArray();
    }

    public FileConditions With(FileConditionsMod patch)
    {
        var cond = new FileConditions(this);

        if (patch.LengthTolerance != null)
            cond.LengthTolerance = patch.LengthTolerance.Value;
        if (patch.MinBitrate != null)
            cond.MinBitrate = patch.MinBitrate.Value;
        if (patch.MaxBitrate != null)
            cond.MaxBitrate = patch.MaxBitrate.Value;
        if (patch.MinSampleRate != null)
            cond.MinSampleRate = patch.MinSampleRate.Value;
        if (patch.MaxSampleRate != null)
            cond.MaxSampleRate = patch.MaxSampleRate.Value;
        if (patch.MinBitDepth != null)
            cond.MinBitDepth = patch.MinBitDepth.Value;
        if (patch.MaxBitDepth != null)
            cond.MaxBitDepth = patch.MaxBitDepth.Value;
        if (patch.StrictTitle != null)
            cond.StrictTitle = patch.StrictTitle.Value;
        if (patch.StrictArtist != null)
            cond.StrictArtist = patch.StrictArtist.Value;
        if (patch.StrictAlbum != null)
            cond.StrictAlbum = patch.StrictAlbum.Value;
        if (patch.Formats != null)
            cond.Formats = patch.Formats;
        if (patch.BannedUsers != null)
            cond.BannedUsers = patch.BannedUsers;
        if (patch.StrictStringDiacrRemove != null)
            cond.StrictStringDiacrRemove = patch.StrictStringDiacrRemove.Value;
        if (patch.AcceptNoLength != null)
            cond.AcceptNoLength = patch.AcceptNoLength.Value;
        if (patch.AcceptMissingProps != null)
            cond.AcceptMissingProps = patch.AcceptMissingProps.Value;

        return cond;
    }

    public override bool Equals(object obj)
    {
        if (obj is FileConditions other)
        {
            return LengthTolerance == other.LengthTolerance &&
                   MinBitrate == other.MinBitrate &&
                   MaxBitrate == other.MaxBitrate &&
                   MinSampleRate == other.MinSampleRate &&
                   MaxSampleRate == other.MaxSampleRate &&
                   MinBitDepth == other.MinBitDepth &&
                   MaxBitDepth == other.MaxBitDepth &&
                   StrictTitle == other.StrictTitle &&
                   StrictArtist == other.StrictArtist &&
                   StrictAlbum == other.StrictAlbum &&
                   StrictStringDiacrRemove == other.StrictStringDiacrRemove &&
                   AcceptNoLength == other.AcceptNoLength &&
                   AcceptMissingProps == other.AcceptMissingProps &&
                   Formats.SequenceEqual(other.Formats) &&
                   BannedUsers.SequenceEqual(other.BannedUsers);
        }
        return false;
    }

    public void UnsetClientSpecificFields()
    {
        MinBitrate = -1;
        MaxBitrate = -1;
        MinSampleRate = -1;
        MaxSampleRate = -1;
        MinBitDepth = -1;
        MaxBitDepth = -1;
    }

    public bool FileSatisfies(Soulseek.File file, Track track, SearchResponse? response)
    {
        return FormatSatisfies(file.Filename)
            && LengthToleranceSatisfies(file, track.Length) && BitrateSatisfies(file) && SampleRateSatisfies(file)
            && StrictTitleSatisfies(file.Filename, track.Title) && StrictArtistSatisfies(file.Filename, track.Artist)
            && StrictAlbumSatisfies(file.Filename, track.Album) && BannedUsersSatisfies(response) && BitDepthSatisfies(file);
    }

    public bool FileSatisfies(TagLib.File file, Track track, bool filenameChecks = false)
    {
        return FormatSatisfies(file.Name)
            && LengthToleranceSatisfies(file, track.Length) && BitrateSatisfies(file) && SampleRateSatisfies(file)
            && BitDepthSatisfies(file) && (!filenameChecks || StrictTitleSatisfies(file.Name, track.Title) 
            && StrictArtistSatisfies(file.Name, track.Artist) && StrictAlbumSatisfies(file.Name, track.Album));
    }

    public bool FileSatisfies(SimpleFile file, Track track, bool filenameChecks = false)
    {
        return FormatSatisfies(file.Path)
            && LengthToleranceSatisfies(file, track.Length) && BitrateSatisfies(file) && SampleRateSatisfies(file)
            && BitDepthSatisfies(file) && (!filenameChecks || StrictTitleSatisfies(file.Path, track.Title)
            && StrictArtistSatisfies(file.Path, track.Artist) && StrictAlbumSatisfies(file.Path, track.Album));
    }

    public bool StrictTitleSatisfies(string fname, string tname, bool noPath = true)
    {
        if (!StrictTitle || tname.Length == 0)
            return true;

        fname = noPath ? Utils.GetFileNameWithoutExtSlsk(fname) : fname;
        return StrictString(fname, tname, StrictStringDiacrRemove, ignoreCase: true);
    }

    public bool StrictArtistSatisfies(string fname, string aname)
    {
        if (!StrictArtist || aname.Length == 0)
            return true;

        return StrictString(fname, aname, StrictStringDiacrRemove, ignoreCase: true, boundarySkipWs: false);
    }

    public bool StrictAlbumSatisfies(string fname, string alname)
    {
        if (!StrictAlbum || alname.Length == 0)
            return true;

        return StrictString(Utils.GetDirectoryNameSlsk(fname), alname, StrictStringDiacrRemove, ignoreCase: true);
    }

    public static string StrictStringPreprocess(string str, bool diacrRemove = true)
    {
        str = str.Replace('_', ' ').ReplaceInvalidChars(' ', true, false);
        str = diacrRemove ? str.RemoveDiacritics() : str;
        str = str.Trim().RemoveConsecutiveWs();
        return str;
    }

    public static bool StrictString(string fname, string tname, bool diacrRemove = true, bool ignoreCase = true, bool boundarySkipWs = true)
    {
        if (tname.Length == 0)
            return true;

        fname = StrictStringPreprocess(fname, diacrRemove);
        tname = StrictStringPreprocess(tname, diacrRemove);

        if (boundarySkipWs)
            return fname.ContainsWithBoundaryIgnoreWs(tname, ignoreCase, acceptLeftDigit: true);
        else
            return fname.ContainsWithBoundary(tname, ignoreCase);
    }

    public static bool BracketCheck(Track track, Track other)
    {
        string t1 = track.Title.RemoveFt().Replace('[', '(');
        if (t1.Contains('('))
            return true;

        string t2 = other.Title.RemoveFt().Replace('[', '(');
        if (!t2.Contains('('))
            return true;

        return false;
    }

    public bool FormatSatisfies(string fname)
    {
        if (Formats.Length == 0)
            return true;

        string ext = Path.GetExtension(fname).TrimStart('.').ToLower();
        return ext.Length > 0 && Formats.Any(f => f == ext);
    }

    public bool LengthToleranceSatisfies(Soulseek.File file, int wantedLength) => LengthToleranceSatisfies(file.Length, wantedLength);
    public bool LengthToleranceSatisfies(TagLib.File file, int wantedLength) => LengthToleranceSatisfies((int)file.Properties.Duration.TotalSeconds, wantedLength);
    public bool LengthToleranceSatisfies(SimpleFile file, int wantedLength) => LengthToleranceSatisfies(file.Length, wantedLength);
    public bool LengthToleranceSatisfies(int? length, int wantedLength)
    {
        if (LengthTolerance < 0 || wantedLength < 0)
            return true;
        if (length == null || length < 0)
            return AcceptNoLength && AcceptMissingProps;
        return Math.Abs((int)length - wantedLength) <= LengthTolerance;
    }

    public bool BitrateSatisfies(Soulseek.File file) => BitrateSatisfies(file.BitRate);
    public bool BitrateSatisfies(TagLib.File file) => BitrateSatisfies(file.Properties.AudioBitrate);
    public bool BitrateSatisfies(SimpleFile file) => BitrateSatisfies(file.Bitrate);
    public bool BitrateSatisfies(int? bitrate)
    {
        return BoundCheck(bitrate, MinBitrate, MaxBitrate);
    }

    public bool SampleRateSatisfies(Soulseek.File file) => SampleRateSatisfies(file.SampleRate);
    public bool SampleRateSatisfies(TagLib.File file) => SampleRateSatisfies(file.Properties.AudioSampleRate);
    public bool SampleRateSatisfies(SimpleFile file) => SampleRateSatisfies(file.Samplerate);
    public bool SampleRateSatisfies(int? sampleRate)
    {
        return BoundCheck(sampleRate, MinSampleRate, MaxSampleRate);
    }

    public bool BitDepthSatisfies(Soulseek.File file) => BitDepthSatisfies(file.BitDepth);
    public bool BitDepthSatisfies(TagLib.File file) => BitDepthSatisfies(file.Properties.BitsPerSample);
    public bool BitDepthSatisfies(SimpleFile file) => BitDepthSatisfies(file.Bitdepth);
    public bool BitDepthSatisfies(int? bitdepth)
    {
        return BoundCheck(bitdepth, MinBitDepth, MaxBitDepth);
    }

    public bool BoundCheck(int? num, int min, int max)
    {
        if (max < 0 && min < 0)
            return true;
        if (num == null || num < 0)
            return AcceptMissingProps;
        if (num < min || max != -1 && num > max)
            return false;
        return true;
    }

    public bool BannedUsersSatisfies(SearchResponse? response)
    {
        return response == null || !BannedUsers.Any(x => x == response.Username);
    }

    public string GetNotSatisfiedName(Soulseek.File file, Track track, SearchResponse? response)
    {
        if (!BannedUsersSatisfies(response))
            return "BannedUsers fails";
        if (!StrictTitleSatisfies(file.Filename, track.Title))
            return "StrictTitle fails";
        if (track.Type == Enums.TrackType.Album && !StrictAlbumSatisfies(file.Filename, track.Artist))
            return "StrictAlbum fails";
        if (!StrictArtistSatisfies(file.Filename, track.Artist))
            return "StrictArtist fails";
        if (!LengthToleranceSatisfies(file, track.Length))
            return "LengthTolerance fails";
        if (!FormatSatisfies(file.Filename))
            return "Format fails";
        if (track.Type != Enums.TrackType.Album && !StrictAlbumSatisfies(file.Filename, track.Artist))
            return "StrictAlbum fails";
        if (!BitrateSatisfies(file))
            return "Bitrate fails";
        if (!SampleRateSatisfies(file))
            return "SampleRate fails";
        if (!BitDepthSatisfies(file))
            return "BitDepth fails";
        return "Satisfied";
    }
}


public class FileConditionsMod
{
    public int? LengthTolerance = null;
    public int? MinBitrate = null;
    public int? MaxBitrate = null;
    public int? MinSampleRate = null;
    public int? MaxSampleRate = null;
    public int? MinBitDepth = null;
    public int? MaxBitDepth = null;
    public bool? StrictTitle = null;
    public bool? StrictArtist = null;
    public bool? StrictAlbum = null;
    public string[]? Formats = null;
    public string[]? BannedUsers = null;
    public bool? StrictStringDiacrRemove = null;
    public bool? AcceptNoLength = null;
    public bool? AcceptMissingProps = null;
}

