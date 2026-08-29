using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PowerDial
{
    /// <summary>
    /// Compile-time serialisers for everything this app reads or writes.
    ///
    /// System.Text.Json will happily work these out by reflection at run time, and used to.
    /// Source generation does it at build time instead: no reflection on the startup path
    /// where Config and the restore point are read, and none once a minute where History
    /// appends a line. It also means the JSON survives trimming, which reflection-based
    /// serialisation does not - the single-file publish does not trim today, but nothing
    /// here now stops it.
    ///
    /// Two contexts because the options are baked in at generation time and the two groups
    /// want different ones: files a person may open are indented, the append-only log is not.
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(Config))]
    [JsonSerializable(typeof(BaselineFile))]
    internal sealed partial class PrettyJson : JsonSerializerContext
    {
    }

    /// <summary>One line a minute, and the offender tally beside it. Not for reading.</summary>
    [JsonSerializable(typeof(HistPoint))]
    [JsonSerializable(typeof(List<Offender>))]
    internal sealed partial class CompactJson : JsonSerializerContext
    {
    }
}
