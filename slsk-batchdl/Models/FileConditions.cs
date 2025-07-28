

using SearchResponse = Soulseek.SearchResponse;

namespace Models
{
    public class FileConditions
    {
        public int? LengthTolerance;
        public int? MinBitrate;
        public int? MaxBitrate;
        public int? MinSampleRate;
        public int? MaxSampleRate;
        public int? MinBitDepth;
        public int? MaxBitDepth;
        public bool? StrictTitle;
        public bool? StrictArtist;
        public bool? StrictAlbum;
        public string[]? Formats;
        public string[]? BannedUsers;
        public bool? AcceptNoLength;
        public bool? AcceptMissingProps;

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
            StrictAlbum = other.StrictAlbum;
            MinBitDepth = other.MinBitDepth;
            MaxBitDepth = other.MaxBitDepth;
            AcceptMissingProps = other.AcceptMissingProps;
            Formats = other.Formats?.ToArray();
            BannedUsers = other.BannedUsers?.ToArray();
        }

        public FileConditions With(FileConditions other)
        {
            var res = new FileConditions(this);
            res.AddConditions(other);
            return res;
        }

        public FileConditions AddConditions(FileConditions mod)
        {
            var undoMod = new FileConditions();

            if (mod.LengthTolerance != null)
            {
                undoMod.LengthTolerance = LengthTolerance;
                LengthTolerance = mod.LengthTolerance.Value;
            }
            if (mod.MinBitrate != null)
            {
                undoMod.MinBitrate = MinBitrate;
                MinBitrate = mod.MinBitrate.Value;
            }
            if (mod.MaxBitrate != null)
            {
                undoMod.MaxBitrate = MaxBitrate;
                MaxBitrate = mod.MaxBitrate.Value;
            }
            if (mod.MinSampleRate != null)
            {
                undoMod.MinSampleRate = MinSampleRate;
                MinSampleRate = mod.MinSampleRate.Value;
            }
            if (mod.MaxSampleRate != null)
            {
                undoMod.MaxSampleRate = MaxSampleRate;
                MaxSampleRate = mod.MaxSampleRate.Value;
            }
            if (mod.MinBitDepth != null)
            {
                undoMod.MinBitDepth = MinBitDepth;
                MinBitDepth = mod.MinBitDepth.Value;
            }
            if (mod.MaxBitDepth != null)
            {
                undoMod.MaxBitDepth = MaxBitDepth;
                MaxBitDepth = mod.MaxBitDepth.Value;
            }
            if (mod.StrictTitle != null)
            {
                undoMod.StrictTitle = StrictTitle;
                StrictTitle = mod.StrictTitle.Value;
            }
            if (mod.StrictArtist != null)
            {
                undoMod.StrictArtist = StrictArtist;
                StrictArtist = mod.StrictArtist.Value;
            }
            if (mod.StrictAlbum != null)
            {
                undoMod.StrictAlbum = StrictAlbum;
                StrictAlbum = mod.StrictAlbum.Value;
            }
            if (mod.Formats != null)
            {
                undoMod.Formats = Formats;
                Formats = mod.Formats;
            }
            if (mod.BannedUsers != null)
            {
                undoMod.BannedUsers = BannedUsers;
                BannedUsers = mod.BannedUsers;
            }
            if (mod.AcceptNoLength != null)
            {
                undoMod.AcceptNoLength = AcceptNoLength;
                AcceptNoLength = mod.AcceptNoLength.Value;
            }
            if (mod.AcceptMissingProps != null)
            {
                undoMod.AcceptMissingProps = AcceptMissingProps;
                AcceptMissingProps = mod.AcceptMissingProps.Value;
            }

            return undoMod;
        }

        public override bool Equals(object? obj)
        {
            if (obj == null || GetType() != obj.GetType())
                return false;

            var other = (FileConditions)obj;

            return LengthTolerance == other.LengthTolerance
                && MinBitrate == other.MinBitrate
                && MaxBitrate == other.MaxBitrate
                && MinSampleRate == other.MinSampleRate
                && MaxSampleRate == other.MaxSampleRate
                && MinBitDepth == other.MinBitDepth
                && MaxBitDepth == other.MaxBitDepth
                && StrictTitle == other.StrictTitle
                && StrictArtist == other.StrictArtist
                && StrictAlbum == other.StrictAlbum
                && AcceptNoLength == other.AcceptNoLength
                && AcceptMissingProps == other.AcceptMissingProps
                && ((Formats == null && other.Formats == null) || (Formats != null && other.Formats != null && Formats.SequenceEqual(other.Formats)))
                && ((BannedUsers == null && other.BannedUsers == null) || (BannedUsers != null && other.BannedUsers != null && BannedUsers.SequenceEqual(other.BannedUsers)));
        }

        public void UnsetClientSpecificFields()
        {
            MinBitrate = null;
            MaxBitrate = null;
            MinSampleRate = null;
            MaxSampleRate = null;
            MinBitDepth = null;
            MaxBitDepth = null;
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
            if (StrictTitle == null || !StrictTitle.Value || tname.Length == 0)
                return true;

            fname = noPath ? Utils.GetFileNameWithoutExtSlsk(fname) : fname;
            return StrictString(fname, tname, diacrRemove: true, ignoreCase: true);
        }

        public bool StrictArtistSatisfies(string fname, string aname)
        {
            if (StrictArtist == null || !StrictArtist.Value || aname.Length == 0)
                return true;

            return StrictString(fname, aname, diacrRemove: true, ignoreCase: true, boundarySkipWs: false);
        }

        public bool StrictAlbumSatisfies(string fname, string alname)
        {
            if (StrictAlbum == null || !StrictAlbum.Value || alname.Length == 0)
                return true;

            return StrictString(Utils.GetDirectoryNameSlsk(fname), alname, diacrRemove: true, ignoreCase: true, boundarySkipWs: true);
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
            if (Formats == null || Formats.Length == 0)
                return true;

            string ext = Path.GetExtension(fname).TrimStart('.').ToLower();
            return ext.Length > 0 && Formats.Any(f => f == ext);
        }

        public bool LengthToleranceSatisfies(Soulseek.File file, int wantedLength) => LengthToleranceSatisfies(file.Length, wantedLength);
        public bool LengthToleranceSatisfies(TagLib.File file, int wantedLength) => LengthToleranceSatisfies((int)file.Properties.Duration.TotalSeconds, wantedLength);
        public bool LengthToleranceSatisfies(SimpleFile file, int wantedLength) => LengthToleranceSatisfies(file.Length, wantedLength);
        public bool LengthToleranceSatisfies(int? length, int wantedLength)
        {
            if (LengthTolerance == null || LengthTolerance < 0 || wantedLength < 0)
                return true;
            if (length == null || length < 0)
                return AcceptNoLength == null || AcceptNoLength.Value;
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

        public bool BoundCheck(int? num, int? min, int? max)
        {
            if (max == null && min == null)
                return true;
            if (num == null)
                return AcceptMissingProps == null || AcceptMissingProps.Value;
            if ((min != null && num < min) || (max != null && num > max))
                return false;
            return true;
        }

        public bool BannedUsersSatisfies(SearchResponse? response)
        {
            return response == null || BannedUsers == null || !BannedUsers.Any(x => x == response.Username);
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

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }
    }
}
