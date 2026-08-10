using System.Text.Json.Serialization;

namespace BeMusicSeeker.Updater
{
    internal static partial class Program
    {
        [JsonSourceGenerationOptions(
            WriteIndented = false,
            GenerationMode = JsonSourceGenerationMode.Metadata)]
        [JsonSerializable(typeof(TransactionJournalRecord))]
        private sealed partial class UpdaterJsonSerializerContext : JsonSerializerContext
        {
        }
    }
}
