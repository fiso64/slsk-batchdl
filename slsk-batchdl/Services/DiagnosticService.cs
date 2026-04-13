using Soulseek;
using Utilities;
using Models;
using Enums;
using Jobs;
using Settings;

namespace Services
{
    public class DiagnosticService
    {
        private readonly SoulseekClientManager _clientManager;

        public DiagnosticService(SoulseekClientManager clientManager)
        {
            _clientManager = clientManager;
        }

        public async Task PerformNoInputActions(DownloadSettings config, CancellationToken ct)
        {

            if (config.PrintOption.HasFlag(PrintOption.Index))
            {
                if (string.IsNullOrEmpty(config.Output.IndexFilePath))
                { Logger.Fatal("Error: No index file path provided"); return; }

                var indexFilePath = Utils.GetFullPath(Utils.ExpandVariables(config.Output.IndexFilePath));
                if (!System.IO.File.Exists(indexFilePath))
                { Logger.Fatal($"Error: Index file {indexFilePath} does not exist"); return; }

                var index = new M3uEditor(indexFilePath, new JobList(), M3uOption.Index, true);
                var data = index.GetPreviousRunData();

                if (config.PrintOption.HasFlag(PrintOption.IndexFailed))
                    data = data.Where(e => e.State == JobState.Failed).ToList();

                JsonPrinter.PrintIndexJson(data);
            }
        }
    }
}
