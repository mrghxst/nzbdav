using NzbWebDAV.Models;

namespace NzbWebDAV.Config;

public class UsenetProviderConfig
{
    public List<ConnectionDetails> Providers { get; set; } = [];

    public int TotalPooledConnections => Math.Max(1, Providers
        .Where(x => x.Type == ProviderType.Pooled)
        .Select(x => x.MaxConnections)
        .Sum());

    public class ConnectionDetails
    {
        public required ProviderType Type { get; set; }
        public required string Host { get; set; }
        public required int Port { get; set; }
        public required bool UseSsl { get; set; }
        public required string User { get; set; }
        public required string Pass { get; set; }
        public required int MaxConnections { get; set; }
        public int Priority { get; set; } = 0;

        /// <summary>
        /// Optional label grouping providers that share the same upstream backbone
        /// (i.e. identical article availability). When set, and one provider on the
        /// backbone reports an article as missing, the remaining providers sharing the
        /// same label are skipped for that request to avoid redundant probes.
        /// Empty (the default) means the provider is never grouped or skipped.
        /// </summary>
        public string Backbone { get; set; } = "";
    }
}