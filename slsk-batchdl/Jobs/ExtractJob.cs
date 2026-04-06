using Enums;
using Models;

namespace Jobs
{
    // Holds a single input string to be resolved by an extractor.
    // The engine runs the appropriate extractor on it and replaces it in
    // the tree with a JobList containing the extracted jobs.
    public class ExtractJob : Job
    {
        public string     Input     { get; }
        public InputType? InputType { get; set; }

        public override bool     OutputsDirectory      => false;
        protected override bool  DefaultCanBeSkipped   => false;

        public override SongQuery QueryTrack => new SongQuery { Title = Input };

        public ExtractJob(string input, InputType? inputType = null)
        {
            Input     = input;
            InputType = inputType;
        }

        public override string ToString(bool noInfo) => Input;
    }
}
